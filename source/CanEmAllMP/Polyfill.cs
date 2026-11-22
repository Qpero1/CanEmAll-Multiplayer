namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All)]
    internal sealed class IsUnmanagedAttribute : Attribute { }

    // the compiler emits these for lambdas over nullable-annotated delegates (used by the online code)
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte b) { NullableFlags = new[] { b }; }
        public NullableAttribute(byte[] b) { NullableFlags = b; }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte b) { Flag = b; }
    }
}
