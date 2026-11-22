using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using LiteNetLib;

namespace CanEmAllMP
{
    /// Port-forward-free connections through the lobby server (lobby.pookies.org by default).
    ///
    /// The host gets a lobby code. A joiner with the code gets connected by trying, in order:
    ///   1. direct  - UDP hole punch, addresses found with STUN on LiteNetLib's own socket (lowest latency)
    ///   2. TURN    - both sides allocate on Cloudflare TURN (UDP, else TCP/TLS 443) and talk relay-to-relay
    ///   3. relay   - game packets go through the lobby server's WebSocket (works anywhere HTTPS works)
    /// Paths 2 and 3 hand packets to LiteNetLib through a LoopProxy, so the game code never knows the difference.
    ///
    /// Everything here runs on the game thread via Tick(), except socket callbacks (which only queue work).
    public sealed class OnlineLink : IDisposable
    {
        public const int Proto = 1;

        // settings (from the config, or overridden by tests)
        public static string ForcePath = "auto";            // auto | direct | turn | relay
        public static List<TurnServer> TurnOverride;        // tests: use these TURN servers instead of the server's
        public static string StunOverride;                  // tests: "host:port"
        public static string TestFakeLoc;                   // tests: an unreachable LAN candidate listed first

        public bool IsHost { get; }
        public string Code { get; private set; }
        public string Status { get; private set; } = "Contacting lobby server...";
        public string FailReason { get; private set; }
        public string PathName { get; private set; } = "";
        /// Host: a lobby code arrived (or changed). Joiner: unused.
        public event Action<string> OnCode;
        /// Joiner: a connection attempt to the host should start at this endpoint. Returns the attempt's peer.
        public Func<IPEndPoint, LiteNetPeer> Connect;
        public LiteNetPeer Attempt { get; private set; }

        readonly LiteNetManager _net;
        readonly OnlineLayer _layer;
        readonly string _server, _ver, _name, _code;
        readonly int _max;
        readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        LobbySocket _ws;
        List<TurnServer> _turns = new List<TurnServer>();
        IPEndPoint _stunEp;
        int _stunSends; double _nextStun;
        Task<bool> _turnTask; TurnClient _turn;
        readonly ConcurrentDictionary<string, LoopProxy> _turnRoutes = new ConcurrentDictionary<string, LoopProxy>();
        readonly ConcurrentDictionary<int, LoopProxy> _relayRoutes = new ConcurrentDictionary<int, LoopProxy>();
        bool _disposed, _loggedMapped;
        readonly uint _token = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);

        // host
        sealed class PeerLink
        {
            public int Id; public string Name;
            public List<IPEndPoint> PunchTo = new List<IPEndPoint>();
            public double PunchUntil, NextPunch;
            public LoopProxy Proxy; public IPEndPoint TurnPeer;
        }
        readonly Dictionary<int, PeerLink> _peers = new Dictionary<int, PeerLink>();

        // joiner
        enum Stage { Waiting, Probing, Direct, Turn, Relay, Connected, Failed }
        Stage _stage = Stage.Waiting;
        bool _candSent, _candHadPub, _gotHostCand, _stunGaveUp;
        double _joinedAt = -1, _candSentAt = -1;
        LoopProxy _proxy;
        List<IPEndPoint> _probeTo = new List<IPEndPoint>();
        double _probeUntil, _nextProbe;

        double Now => _clock.Elapsed.TotalSeconds;

        public OnlineLink(LiteNetManager net, OnlineLayer layer, string server, bool host, string code, string ver, string name, int max)
        {
            _net = net; _layer = layer; IsHost = host;
            _server = (server ?? "").Trim().TrimEnd('/');
            _code = code; _ver = ver; _name = name; _max = max;
        }

        public static string NormalizeCode(string raw)
        {
            var s = new string((raw ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            return s.Length == 6 ? s : null;
        }

        public void Start()
        {
            string baseWs = _server.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + _server.Substring(8)
                          : _server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + _server.Substring(7)
                          : _server;
            string q = $"?proto={Proto}&ver={Uri.EscapeDataString(_ver)}&name={Uri.EscapeDataString(_name ?? "Player")}";
            var uri = new Uri(IsHost ? $"{baseWs}/v1/host{q}&max={_max}" : $"{baseWs}/v1/join/{_code}{q}");
            _ws = new LobbySocket();
            _ws.OnRelay = OnRelayFrame;
            var ws = _ws;
            Task.Run(async () =>
            {
                bool ok = await ws.ConnectAsync(uri);
                if (!ok) _main.Enqueue(() => Fail(ws.CloseReason ?? "Can't reach the lobby server"));
            });
            if (ForcePath == "turn" || ForcePath == "relay") OnlineLog.Info($"[Online] forcing path: {ForcePath}");
        }

        // ------------------------------------------------------------------ game thread

        public void Tick()
        {
            if (_disposed) return;
            while (_main.TryDequeue(out var a)) { try { a(); } catch (Exception e) { OnlineLog.Warn("[Online] " + e); } }
            if (_ws != null)
                while (_ws.Messages.TryDequeue(out var m)) { try { Handle(m); } catch (Exception e) { OnlineLog.Warn("[Online] message: " + e); } }

            double now = Now;
            var mapped = _layer.Mapped;
            if (mapped != null && !_loggedMapped) { _loggedMapped = true; OnlineLog.Info($"[Online] public address {mapped} (STUN)"); }

            // connectivity probes: answer every probe; an answer to ours means that address works
            while (_layer.Probes.TryDequeue(out var pr))
            {
                var ack = OnlineLayer.MakeProbe(1, pr.token);
                try { _net.SendRaw(ack, 0, ack.Length, pr.ep); } catch { }
            }
            while (_layer.Acks.TryDequeue(out var ak))
            {
                if (IsHost || ak.token != _token || _stage != Stage.Probing) continue;
                _stage = Stage.Direct; PathName = "direct";
                Status = "Connecting directly...";
                OnlineLog.Info($"[Online] {ak.ep} answered - connecting directly");
                Attempt = Connect(ak.ep);
            }
            if (!IsHost && _stage == Stage.Probing)
            {
                if (now >= _probeUntil)
                {
                    OnlineLog.Info($"[Online] no direct answer from {string.Join(", ", _probeTo)}");
                    TryTurn();
                }
                else if (now >= _nextProbe)
                {
                    _nextProbe = now + 0.15;
                    var probe = OnlineLayer.MakeProbe(0, _token);
                    foreach (var ep in _probeTo) try { _net.SendRaw(probe, 0, probe.Length, ep); } catch { }
                }
            }

            // STUN on LiteNetLib's socket (a few tries until we know our public address)
            if (_stunEp != null && _layer.Mapped == null && _stunSends >= 8 && now >= _nextStun && !_stunGaveUp)
            {
                _stunGaveUp = true;
                OnlineLog.Warn($"[Online] STUN ({_stunEp}) never answered");
            }
            if (_stunEp != null && _layer.Mapped == null && _stunSends < 8 && now >= _nextStun)
            {
                _stunSends++; _nextStun = now + 0.3;
                var tx = Stun.NewTxId();
                _layer.ExpectStun(tx);
                var b = new Stun.Msg(Stun.BindingRequest, tx).Encode();
                try { _net.SendRaw(b, 0, b.Length, _stunEp); } catch { }
            }

            if (IsHost)
            {
                foreach (var p in _peers.Values)
                    if (now < p.PunchUntil && now >= p.NextPunch)
                    {
                        p.NextPunch = now + 0.15;
                        var probe = OnlineLayer.MakeProbe(0, _token); // opens our NAT towards them (their acks are ignored)
                        foreach (var ep in p.PunchTo) try { _net.SendRaw(probe, 0, probe.Length, ep); } catch { }
                    }
            }
            else
            {
                // send our candidates once STUN answered (or after a short wait)
                // send our candidates once STUN answered; without an answer after 2.5s, send what we have
                if (_joinedAt >= 0 && !_candSent && (_layer.Mapped != null || now - _joinedAt > 2.5 || _stunGaveUp))
                {
                    _candSent = true; _candSentAt = now; _candHadPub = _layer.Mapped != null;
                    if (!_candHadPub) OnlineLog.Warn("[Online] no public address yet (STUN) - sending LAN addresses only");
                    Signal(0, Cand());
                }
                // STUN answered late: send again so the host can open its router towards our public address
                if (_candSent && !_candHadPub && _layer.Mapped != null && _stage != Stage.Connected)
                {
                    _candHadPub = true;
                    Signal(0, Cand());
                    if (_stage == Stage.Probing) _probeUntil = Math.Max(_probeUntil, now + 2.0);
                }
                if (_candSent && !_gotHostCand && _stage == Stage.Waiting && now - _candSentAt > 8)
                    Fail("The host didn't answer (are they still hosting?)");
            }

            if (_ws != null && !_ws.Open && FailReason == null && _joinedAt < 0 && Code == null && _ws.CloseReason != null)
                Fail(_ws.CloseReason);
        }

        /// Joiner: LiteNetLib gave up on the current attempt. Returns true if another path is being tried.
        public bool OnAttemptFailed(LiteNetPeer peer)
        {
            if (IsHost || peer != Attempt) return false;
            Attempt = null;
            switch (_stage)
            {
                case Stage.Direct: OnlineLog.Info("[Online] direct connection failed"); TryTurn(); return _stage != Stage.Failed;
                case Stage.Turn: OnlineLog.Info("[Online] TURN connection failed"); TryRelay(); return _stage != Stage.Failed;
                default: return false;
            }
        }

        /// Joiner: LiteNetLib connected.
        public void OnConnected()
        {
            if (IsHost) return;
            _stage = Stage.Connected;
            Status = "Connected (" + PathName + ")";
            OnlineLog.Info($"[Online] connected to the host via {PathName}");
        }

        void Fail(string why)
        {
            if (FailReason != null) return;
            FailReason = why; Status = why; _stage = Stage.Failed;
            OnlineLog.Warn("[Online] " + why);
        }

        // ------------------------------------------------------------------ lobby messages

        void Handle(JsonElement m)
        {
            string t = Str(m, "t");
            switch (t)
            {
                case "hosted":
                    Code = Str(m, "code");
                    ReadIce(m);
                    Status = "Lobby code " + Code;
                    OnlineLog.Info($"[Online] hosting, lobby code {Code} ({_turns.Count} TURN server(s))");
                    OnCode?.Invoke(Code);
                    break;
                case "joined":
                    ReadIce(m);
                    _joinedAt = Now;
                    Status = "Finding the host...";
                    OnlineLog.Info($"[Online] joined lobby {_code} as peer {Int(m, "peer")} ({_turns.Count} TURN server(s))");
                    break;
                case "peer-join":
                {
                    int id = Int(m, "peer");
                    _peers[id] = new PeerLink { Id = id, Name = Str(m, "name") };
                    OnlineLog.Info($"[Online] {Str(m, "name")} is connecting (peer {id})");
                    break;
                }
                case "peer-leave":
                    DropPeer(Int(m, "peer"));
                    break;
                case "signal":
                    if (m.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        if (IsHost) HostSignal(Int(m, "from"), data); else JoinSignal(data);
                    }
                    break;
                case "ice":
                    ReadIce(m);
                    break;
                case "error":
                    Fail(Str(m, "msg") ?? Str(m, "error") ?? "Lobby error");
                    break;
                case "closed":
                    if (!IsHost && _stage != Stage.Connected) Fail(Str(m, "reason") ?? "Host closed the lobby");
                    break;
            }
        }

        void ReadIce(JsonElement m)
        {
            var turns = new List<TurnServer>();
            if (m.TryGetProperty("ice", out var ice) && ice.ValueKind == JsonValueKind.Array)
                foreach (var s in ice.EnumerateArray())
                {
                    string user = Str(s, "username"), cred = Str(s, "credential");
                    var urls = new List<string>();
                    if (s.TryGetProperty("urls", out var u))
                    {
                        if (u.ValueKind == JsonValueKind.String) urls.Add(u.GetString());
                        else if (u.ValueKind == JsonValueKind.Array) foreach (var x in u.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) urls.Add(x.GetString());
                    }
                    foreach (var url in urls)
                    {
                        if (url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) { if (StunOverride == null && _stunEp == null) ResolveStun(url.Substring(5)); continue; }
                        var ts = TurnServer.Parse(url, user, cred);
                        if (ts != null && ts.Port != 53) turns.Add(ts);
                    }
                }
            if (StunOverride != null && _stunEp == null) ResolveStun(StunOverride);
            // fastest first: UDP, then plain TCP, then TLS (443 before 5349: it gets through more firewalls)
            _turns = TurnOverride ?? turns.OrderBy(x => x.Tls ? 2 : x.Tcp ? 1 : 0).ThenBy(x => x.Port == 443 ? 0 : 1).ToList();
        }

        void ResolveStun(string hostPort)
        {
            int c = hostPort.LastIndexOf(':');
            string host = c > 0 ? hostPort.Substring(0, c) : hostPort;
            int port = c > 0 && int.TryParse(hostPort.Substring(c + 1).Split('?')[0], out var p) ? p : 3478;
            Task.Run(async () =>
            {
                try
                {
                    var a = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                    if (a != null) _main.Enqueue(() => _stunEp = new IPEndPoint(a, port));
                    else _main.Enqueue(() => _stunGaveUp = true);
                }
                catch (Exception e) { OnlineLog.Warn("[Online] STUN lookup: " + e.Message); _main.Enqueue(() => _stunGaveUp = true); }
            });
        }

        // ------------------------------------------------------------------ host side

        void HostSignal(int from, JsonElement d)
        {
            if (!_peers.TryGetValue(from, out var p)) _peers[from] = p = new PeerLink { Id = from };
            switch (Str(d, "k"))
            {
                case "cand":
                {
                    p.PunchTo = Eps(d);
                    p.PunchUntil = Now + 8; p.NextPunch = 0;
                    OnlineLog.Info($"[Online] peer {from} candidates: {string.Join(", ", p.PunchTo)}");
                    var mine = Cand();
                    Signal(from, mine);
                    break;
                }
                case "turn":
                {
                    var peerRelay = Ep(Str(d, "relay"));
                    if (peerRelay == null) { Signal(from, new { k = "turn", fail = true }); break; }
                    EnsureTurn().ContinueWith(async t =>
                    {
                        bool ok = t.Result && await _turn.BindAsync(peerRelay);
                        _main.Enqueue(() =>
                        {
                            if (!ok || !_peers.ContainsKey(from)) { Signal(from, new { k = "turn", fail = true }); return; }
                            p.Proxy?.Dispose();
                            var proxy = p.Proxy = new LoopProxy(_net.LocalPort);
                            var turn = _turn;
                            proxy.OnFromGame = (b, n) => turn.SendTo(peerRelay, b, 0, n);
                            p.TurnPeer = peerRelay;
                            _turnRoutes[peerRelay.ToString()] = proxy;
                            Signal(from, new { k = "turn", relay = _turn.Relayed.ToString() });
                            OnlineLog.Info($"[Online] peer {from} uses TURN");
                        });
                    });
                    break;
                }
                case "relay":
                {
                    p.Proxy?.Dispose();
                    var proxy = p.Proxy = new LoopProxy(_net.LocalPort);
                    var ws = _ws; int id = from;
                    proxy.OnFromGame = (b, n) => ws.SendRelay(id, b, n);
                    _relayRoutes[from] = proxy;
                    Signal(from, new { k = "relay-ok" });
                    OnlineLog.Info($"[Online] peer {from} uses the lobby relay");
                    break;
                }
            }
        }

        void DropPeer(int id)
        {
            if (!_peers.TryGetValue(id, out var p)) return;
            _peers.Remove(id);
            _relayRoutes.TryRemove(id, out _);
            if (p.TurnPeer != null) { _turnRoutes.TryRemove(p.TurnPeer.ToString(), out _); _turn?.Unbind(p.TurnPeer); }
            p.Proxy?.Dispose();
        }

        /// Host: how a connected peer reaches us ("direct", "TURN", "relay"), from LiteNetLib's endpoint for it.
        public string PathOf(IPEndPoint ep)
        {
            if (ep == null) return "";
            if (!IPAddress.IsLoopback(ep.Address)) return "direct";
            foreach (var p in _peers.Values)
                if (p.Proxy != null && p.Proxy.Endpoint.Port == ep.Port) return p.TurnPeer != null ? "TURN" : "relay";
            return "direct";
        }

        /// Host: the lobby can drop a player that never made it in game.
        public void Kick(int lobbyPeer, string reason) => _ws?.Send(new { t = "kick", peer = lobbyPeer, reason });

        // ------------------------------------------------------------------ joiner side

        void JoinSignal(JsonElement d)
        {
            switch (Str(d, "k"))
            {
                case "cand":
                {
                    if (_gotHostCand) break;
                    _gotHostCand = true;
                    var eps = Eps(d);
                    IPEndPoint hostPub = Ep(Str(d, "pub"));
                    OnlineLog.Info($"[Online] host candidates: {string.Join(", ", eps)}");
                    if (ForcePath == "turn" || ForcePath == "relay" || eps.Count == 0) { if (ForcePath == "relay") TryRelay(); else TryTurn(); break; }
                    // probe every candidate at once (public + each LAN address) and connect to the first that answers
                    _probeTo = eps;
                    _stage = Stage.Probing;
                    _probeUntil = Now + 4.0; _nextProbe = 0;
                    Status = "Finding a direct route...";
                    break;
                }
                case "turn":
                {
                    if (_stage != Stage.Turn) break;
                    var hostRelay = Ep(Str(d, "relay"));
                    if (hostRelay == null || _turn == null) { TryRelay(); break; }
                    _turn.BindAsync(hostRelay).ContinueWith(t => _main.Enqueue(() =>
                    {
                        if (_stage != Stage.Turn) return;
                        if (!t.Result) { TryRelay(); return; }
                        _proxy?.Dispose();
                        var proxy = _proxy = new LoopProxy(_net.LocalPort);
                        var turn = _turn;
                        proxy.OnFromGame = (b, n) => turn.SendTo(hostRelay, b, 0, n);
                        _turnRoutes[hostRelay.ToString()] = proxy;
                        PathName = "TURN";
                        OnlineLog.Info($"[Online] trying TURN ({turn.Server}) to {hostRelay}");
                        Attempt = Connect(proxy.Endpoint);
                    }));
                    break;
                }
                case "relay-ok":
                {
                    if (_stage != Stage.Relay) break;
                    _proxy?.Dispose();
                    var proxy = _proxy = new LoopProxy(_net.LocalPort);
                    var ws = _ws;
                    proxy.OnFromGame = (b, n) => ws.SendRelay(0, b, n);
                    _relayRoutes[0] = proxy;
                    PathName = "lobby relay";
                    OnlineLog.Info("[Online] trying the lobby relay");
                    Attempt = Connect(proxy.Endpoint);
                    break;
                }
            }
        }

        void TryTurn()
        {
            if (ForcePath == "direct") { Fail("Couldn't connect directly (path forced to direct)"); return; }
            if (ForcePath == "relay" || _turns.Count == 0) { TryRelay(); return; }
            _stage = Stage.Turn;
            Status = "Direct connection blocked - trying Cloudflare TURN...";
            EnsureTurn().ContinueWith(t => _main.Enqueue(() =>
            {
                if (_stage != Stage.Turn) return;
                if (!t.Result) { TryRelay(); return; }
                Signal(0, new { k = "turn", relay = _turn.Relayed.ToString() });
            }));
        }

        void TryRelay()
        {
            if (ForcePath == "direct") { Fail("Couldn't connect directly (path forced to direct)"); return; }
            if (_ws == null || !_ws.Open) { Fail("Couldn't connect to the host (lobby connection lost)"); return; }
            _stage = Stage.Relay;
            Status = "Trying the lobby relay...";
            Signal(0, new { k = "relay" });
        }

        // ------------------------------------------------------------------ shared

        Task<bool> EnsureTurn()
        {
            if (_turnTask != null) return _turnTask;
            var list = _turns.ToList();
            return _turnTask = Task.Run(async () =>
            {
                foreach (var s in list)
                {
                    var c = new TurnClient(s);
                    if (await c.AllocateAsync())
                    {
                        c.OnData = (peer, b, off, len) => { if (_turnRoutes.TryGetValue(peer.ToString(), out var px)) px.Inject(b, off, len); };
                        if (_disposed) { c.Dispose(); return false; }
                        _turn = c;
                        return true;
                    }
                    c.Dispose();
                }
                return false;
            });
        }

        void OnRelayFrame(int peer, byte[] b, int off, int len)
        {
            if (_relayRoutes.TryGetValue(peer, out var px)) px.Inject(b, off, len);
        }

        object Cand()
        {
            var mapped = _layer.Mapped;
            // LAN addresses: real adapters (with a gateway) first, virtual ones (Hyper-V, WSL, VMs) last
            var loc = new List<(string ep, int rank)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool virt = desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("vmware")
                             || desc.Contains("virtualbox") || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("bluetooth");
                    bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        if (a.GetAddressBytes()[0] == 169) continue; // link-local
                        loc.Add((new IPEndPoint(a, _net.LocalPort).ToString(), virt ? 2 : gw ? 0 : 1));
                    }
                }
            }
            catch { }
            var locs = loc.OrderBy(x => x.rank).Select(x => x.ep).Distinct().Take(6).ToArray();
            if (TestFakeLoc != null) locs = new[] { TestFakeLoc }.Concat(locs).ToArray();
            OnlineLog.Info($"[Online] my candidates: {(mapped != null ? mapped + ", " : "")}{string.Join(", ", locs)}");
            return new { k = "cand", pub = mapped?.ToString(), loc = locs };
        }

        void Signal(int to, object data) => _ws?.Send(new { t = "signal", to, data });

        static List<IPEndPoint> Eps(JsonElement d)
        {
            var l = new List<IPEndPoint>();
            var pub = Ep(Str(d, "pub"));
            if (pub != null) l.Add(pub);
            if (d.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array)
                foreach (var x in loc.EnumerateArray()) { var e = x.ValueKind == JsonValueKind.String ? Ep(x.GetString()) : null; if (e != null && !l.Contains(e)) l.Add(e); }
            return l;
        }

        static IPEndPoint Ep(string s) => s != null && IPEndPoint.TryParse(s, out var e) && e.AddressFamily == AddressFamily.InterNetwork && e.Port > 0 ? e : null;
        static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ws?.Dispose(); } catch { }
            try { _turn?.Dispose(); } catch { }
            try { _proxy?.Dispose(); } catch { }
            foreach (var p in _peers.Values) try { p.Proxy?.Dispose(); } catch { }
            _peers.Clear(); _turnRoutes.Clear(); _relayRoutes.Clear();
        }
    }
}
