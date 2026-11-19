using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace CanEmAllMP
{
    /// <summary>
    /// Applies the [HarmonyPatch] methods in Patches one by one, skipping any listed in
    /// [Debug] DisabledPatches ("*" = none, "Enemy" = every Enemy hook, "Enemy.Damage" = one method).
    /// </summary>
    /// Hook a compiler-generated coroutine (e.g. GameManager._SuicideCoroutine_d__174) by its name prefix: the
    /// number at the end changes whenever the game is rebuilt, so naming it directly breaks on every game update.
    [AttributeUsage(AttributeTargets.Method)]
    public class CoroutinePatchAttribute : Attribute
    {
        public Type Owner; public string Prefix; public string Method;
        public CoroutinePatchAttribute(Type owner, string prefix, string method = "MoveNext") { Owner = owner; Prefix = prefix; Method = method; }
    }

    /// Patch every overload of the target method (e.g. SaveManager.Save() and Save(out string), which the game
    /// uses for different saves since its update). The patch method must not take the target's parameters.
    [AttributeUsage(AttributeTargets.Method)]
    public class AllOverloadsAttribute : Attribute { }

    public static class PatchRunner
    {
        public static Type FindNested(Type owner, string prefix)
        {
            foreach (var t in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (t.Name.StartsWith(prefix, StringComparison.Ordinal)) return t;
            return null;
        }

        public static void Run(Harmony harmony, string disabledCsv)
        {
            var disabled = new HashSet<string>((disabledCsv ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            bool all = disabled.Contains("*");
            int applied = 0, skipped = 0;
            foreach (var m in typeof(Patches).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                HarmonyPatch hp; CoroutinePatchAttribute cp; bool isPrefix, isPostfix;
                try
                {
                    // a hook whose target type no longer exists (game update) must not stop the others
                    hp = m.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().FirstOrDefault();
                    cp = m.GetCustomAttributes(typeof(CoroutinePatchAttribute), false).Cast<CoroutinePatchAttribute>().FirstOrDefault();
                    isPrefix = m.GetCustomAttributes(typeof(HarmonyPrefix), false).Any();
                    isPostfix = m.GetCustomAttributes(typeof(HarmonyPostfix), false).Any();
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[patch] {m.Name}: its target is missing in this game version ({e.GetType().Name}), skipped"); continue; }
                if (!isPrefix && !isPostfix) continue;
                Type type; string methodName;
                if (cp != null)
                {
                    type = FindNested(cp.Owner, cp.Prefix);
                    methodName = cp.Method;
                    if (type == null) { Plugin.Log.LogWarning($"[patch] {cp.Owner.Name}.{cp.Prefix}* not found in this game version ({m.Name} skipped)"); continue; }
                }
                else
                {
                    if (hp?.info?.declaringType == null) continue;
                    type = hp.info.declaringType;
                    methodName = hp.info.methodName;
                }
                string key = type.Name + "." + methodName;
                if (all || disabled.Contains(type.Name) || disabled.Contains(key))
                {
                    skipped++;
                    Plugin.Log.LogInfo($"[patch] SKIPPED {key} ({(isPrefix ? "prefix" : "postfix")} {m.Name})");
                    continue;
                }
                bool allOverloads = false;
                try { allOverloads = m.GetCustomAttributes(typeof(AllOverloadsAttribute), false).Any(); } catch { }
                if (allOverloads)
                {
                    foreach (var ov in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (ov.Name != methodName) continue;
                        // a patch that takes __result only fits overloads that return something, and the other way round
                        bool wantsResult = m.GetParameters().Any(p => p.Name == "__result");
                        bool returns = ov is MethodInfo mi && mi.ReturnType != typeof(void);
                        if (isPrefix && wantsResult != returns) continue;
                        try
                        {
                            var hm = new HarmonyMethod(m);
                            if (isPrefix) harmony.Patch(ov, prefix: hm); else harmony.Patch(ov, postfix: hm);
                            applied++;
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[patch] failed {key} overload: {e.Message}"); }
                    }
                    continue;
                }
                MethodBase target = cp != null ? AccessTools.Method(type, methodName)
                    : hp.info.methodType == MethodType.Getter
                    ? AccessTools.PropertyGetter(type, hp.info.methodName)
                    : AccessTools.Method(type, hp.info.methodName, hp.info.argumentTypes);
                if (target == null) { Plugin.Log.LogWarning($"[patch] target not found: {key}"); continue; }
                try
                {
                    var hm = new HarmonyMethod(m);
                    if (isPrefix) harmony.Patch(target, prefix: hm); else harmony.Patch(target, postfix: hm);
                    applied++;
                }
                catch (Exception e) { Plugin.Log.LogError($"[patch] failed {key}: {e.Message}"); }
            }
            Plugin.Log.LogInfo($"[patch] {applied} hooks applied, {skipped} skipped");
        }
    }
}
