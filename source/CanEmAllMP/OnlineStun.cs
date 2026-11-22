using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CanEmAllMP
{
    /// Logging hook for the online code (kept free of BepInEx/Unity so it can be tested on its own).
    public static class OnlineLog
    {
        public static Action<string> Info = s => { };
        public static Action<string> Warn = s => { };
    }

    /// Minimal STUN / TURN message codec (RFC 8489 / RFC 8656), IPv4 only.
    public static class Stun
    {
        public const uint Cookie = 0x2112A442;
        public const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101;
        public const ushort AllocateRequest = 0x0003, AllocateSuccess = 0x0103, AllocateError = 0x0113;
        public const ushort RefreshRequest = 0x0004, RefreshSuccess = 0x0104, RefreshError = 0x0114;
        public const ushort ChannelBindRequest = 0x0009, ChannelBindSuccess = 0x0109, ChannelBindError = 0x0119;
        public const ushort DataIndication = 0x0017;

        public const ushort AttrMappedAddress = 0x0001, AttrUsername = 0x0006, AttrIntegrity = 0x0008, AttrErrorCode = 0x0009,
            AttrChannelNumber = 0x000C, AttrLifetime = 0x000D, AttrXorPeer = 0x0012, AttrData = 0x0013, AttrRealm = 0x0014,
            AttrNonce = 0x0015, AttrXorRelayed = 0x0016, AttrRequestedTransport = 0x0019, AttrXorMapped = 0x0020;

        /// True if this looks like a STUN message (not ChannelData, not LiteNetLib).
        public static bool IsStun(byte[] d, int off, int len) =>
            len >= 20 && (d[off] & 0xC0) == 0 &&
            d[off + 4] == 0x21 && d[off + 5] == 0x12 && d[off + 6] == 0xA4 && d[off + 7] == 0x42 &&
            ((d[off + 2] << 8) | d[off + 3]) + 20 == len;

        public static byte[] NewTxId() { var t = new byte[12]; RandomNumberGenerator.Fill(t); return t; }

        public sealed class Msg
        {
            public ushort Type;
            public byte[] TxId;
            public readonly List<KeyValuePair<ushort, byte[]>> Attrs = new List<KeyValuePair<ushort, byte[]>>();

            public Msg(ushort type, byte[] txId = null) { Type = type; TxId = txId ?? NewTxId(); }

            public Msg Add(ushort type, byte[] value) { Attrs.Add(new KeyValuePair<ushort, byte[]>(type, value)); return this; }
            public Msg AddString(ushort type, string s) => Add(type, Encoding.UTF8.GetBytes(s));
            public Msg AddU32(ushort type, uint v) => Add(type, new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
            public Msg AddXorAddr(ushort type, IPEndPoint ep) => Add(type, XorAddr(ep));

            public byte[] Get(ushort type) { foreach (var a in Attrs) if (a.Key == type) return a.Value; return null; }
            public string GetString(ushort type) { var v = Get(type); return v == null ? null : Encoding.UTF8.GetString(v); }
            public IPEndPoint GetXorAddr(ushort type) { var v = Get(type); return v == null ? null : ParseXorAddr(v); }
            public uint GetU32(ushort type, uint def) { var v = Get(type); return v == null || v.Length < 4 ? def : (uint)(v[0] << 24 | v[1] << 16 | v[2] << 8 | v[3]); }
            public int ErrorCode { get { var v = Get(AttrErrorCode); return v == null || v.Length < 4 ? 0 : (v[2] & 7) * 100 + v[3]; } }

            /// Serialize; with a key, MESSAGE-INTEGRITY is appended.
            public byte[] Encode(byte[] integrityKey = null)
            {
                var ms = new MemoryStream();
                ms.Write(new byte[20], 0, 20);
                foreach (var a in Attrs) WriteAttr(ms, a.Key, a.Value);
                var b = ms.ToArray();
                if (integrityKey != null)
                {
                    int len = b.Length - 20 + 24; // header length must already count MESSAGE-INTEGRITY
                    Header(b, Type, len, TxId);
                    byte[] mac;
                    using (var h = new HMACSHA1(integrityKey)) mac = h.ComputeHash(b, 0, b.Length);
                    WriteAttr(ms, AttrIntegrity, mac);
                    b = ms.ToArray();
                }
                Header(b, Type, b.Length - 20, TxId);
                return b;
            }

            public static Msg Decode(byte[] d, int off, int len)
            {
                if (!IsStun(d, off, len)) return null;
                var tx = new byte[12]; Buffer.BlockCopy(d, off + 8, tx, 0, 12);
                var m = new Msg((ushort)((d[off] << 8) | d[off + 1]), tx);
                int p = off + 20, end = off + len;
                while (p + 4 <= end)
                {
                    ushort t = (ushort)((d[p] << 8) | d[p + 1]);
                    int l = (d[p + 2] << 8) | d[p + 3];
                    if (p + 4 + l > end) break;
                    var v = new byte[l]; Buffer.BlockCopy(d, p + 4, v, 0, l);
                    m.Attrs.Add(new KeyValuePair<ushort, byte[]>(t, v));
                    p += 4 + ((l + 3) & ~3);
                }
                return m;
            }
        }

        static void Header(byte[] b, ushort type, int len, byte[] tx)
        {
            b[0] = (byte)(type >> 8); b[1] = (byte)type; b[2] = (byte)(len >> 8); b[3] = (byte)len;
            b[4] = 0x21; b[5] = 0x12; b[6] = 0xA4; b[7] = 0x42;
            Buffer.BlockCopy(tx, 0, b, 8, 12);
        }

        static void WriteAttr(Stream s, ushort t, byte[] v)
        {
            s.WriteByte((byte)(t >> 8)); s.WriteByte((byte)t); s.WriteByte((byte)(v.Length >> 8)); s.WriteByte((byte)v.Length);
            s.Write(v, 0, v.Length);
            for (int i = v.Length; (i & 3) != 0; i++) s.WriteByte(0);
        }

        public static byte[] XorAddr(IPEndPoint ep)
        {
            var a = ep.Address.GetAddressBytes();
            int port = ep.Port ^ 0x2112;
            return new byte[] { 0, 1, (byte)(port >> 8), (byte)port, (byte)(a[0] ^ 0x21), (byte)(a[1] ^ 0x12), (byte)(a[2] ^ 0xA4), (byte)(a[3] ^ 0x42) };
        }

        public static IPEndPoint ParseXorAddr(byte[] v)
        {
            if (v.Length < 8 || v[1] != 1) return null; // IPv4 only
            int port = ((v[2] << 8) | v[3]) ^ 0x2112;
            var a = new[] { (byte)(v[4] ^ 0x21), (byte)(v[5] ^ 0x12), (byte)(v[6] ^ 0xA4), (byte)(v[7] ^ 0x42) };
            return new IPEndPoint(new IPAddress(a), port);
        }

        public static IPEndPoint ParseAddr(byte[] v)
        {
            if (v == null || v.Length < 8 || v[1] != 1) return null;
            return new IPEndPoint(new IPAddress(new[] { v[4], v[5], v[6], v[7] }), (v[2] << 8) | v[3]);
        }

        public static byte[] LongTermKey(string user, string realm, string pass)
        {
            using (var md5 = MD5.Create()) return md5.ComputeHash(Encoding.UTF8.GetBytes(user + ":" + realm + ":" + pass));
        }
    }

    /// One TURN server entry from the lobby server's ICE list.
    public sealed class TurnServer
    {
        public string Host; public int Port; public bool Tcp; public bool Tls;
        public string Username, Credential;
        public override string ToString() => $"{(Tls ? "turns" : "turn")}:{Host}:{Port}/{(Tcp ? "tcp" : "udp")}";

        /// "turn:host:port?transport=udp" / "turns:host:443?transport=tcp" / "stun:host:port"
        public static TurnServer Parse(string url, string user, string cred)
        {
            try
            {
                bool tls = url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
                if (!tls && !url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)) return null;
                string rest = url.Substring(tls ? 6 : 5);
                string transport = tls ? "tcp" : "udp";
                int q = rest.IndexOf('?');
                if (q >= 0)
                {
                    foreach (var kv in rest.Substring(q + 1).Split('&'))
                        if (kv.StartsWith("transport=", StringComparison.OrdinalIgnoreCase)) transport = kv.Substring(10).ToLowerInvariant();
                    rest = rest.Substring(0, q);
                }
                int c = rest.LastIndexOf(':');
                string host = c > 0 ? rest.Substring(0, c) : rest;
                int port = c > 0 ? int.Parse(rest.Substring(c + 1)) : (tls ? 5349 : 3478);
                return new TurnServer { Host = host, Port = port, Tcp = tls || transport == "tcp", Tls = tls, Username = user, Credential = cred };
            }
            catch { return null; }
        }
    }

    /// TURN client: one allocation, channels to any number of peers. Works over UDP, TCP or TLS.
    /// Data from peers arrives on a background thread via OnData.
    public sealed class TurnClient : IDisposable
    {
        public IPEndPoint Relayed { get; private set; }
        public TurnServer Server { get; }
        public Action<IPEndPoint, byte[], int, int> OnData; // (peer, buf, offset, length)
        public static bool InsecureTlsForTests;

        readonly object _sendLock = new object();
        readonly ConcurrentDictionary<string, TaskCompletionSource<Stun.Msg>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<Stun.Msg>>();
        readonly ConcurrentDictionary<string, ushort> _channelOf = new ConcurrentDictionary<string, ushort>(); // peer -> channel
        readonly ConcurrentDictionary<ushort, IPEndPoint> _peerOf = new ConcurrentDictionary<ushort, IPEndPoint>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        Socket _udp; IPEndPoint _serverEp;
        Stream _stream; TcpClient _tcp;
        string _realm, _nonce; byte[] _key;
        int _nextChannel = 0x4000;
        volatile bool _closed;

        public TurnClient(TurnServer s) { Server = s; }

        public async Task<bool> AllocateAsync(int timeoutMs = 6000)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(Server.Host);
                IPAddress ip = null;
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip == null) { OnlineLog.Warn($"[TURN] {Server}: no IPv4 address"); return false; }
                _serverEp = new IPEndPoint(ip, Server.Port);

                if (Server.Tcp)
                {
                    _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                    var ct = Task.Delay(timeoutMs);
                    var conn = _tcp.ConnectAsync(ip, Server.Port);
                    if (await Task.WhenAny(conn, ct) != conn || !_tcp.Connected) { OnlineLog.Warn($"[TURN] {Server}: can't connect"); return false; }
                    Stream s = _tcp.GetStream();
                    if (Server.Tls)
                    {
                        var ssl = InsecureTlsForTests ? new SslStream(s, false, (a, b, c, d) => true) : new SslStream(s, false);
                        var auth = ssl.AuthenticateAsClientAsync(Server.Host);
                        if (await Task.WhenAny(auth, Task.Delay(timeoutMs)) != auth) { OnlineLog.Warn($"[TURN] {Server}: TLS timed out"); return false; }
                        await auth;
                        s = ssl;
                    }
                    _stream = s;
                    _ = Task.Run(StreamReceiveLoop);
                }
                else
                {
                    _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                    _ = Task.Run(UdpReceiveLoop);
                }

                // 1st Allocate gets 401 + realm/nonce, 2nd one authenticates
                var req = new Stun.Msg(Stun.AllocateRequest).AddU32(Stun.AttrRequestedTransport, 17u << 24);
                var res = await Request(req, false, timeoutMs);
                if (res == null) { OnlineLog.Warn($"[TURN] {Server}: no answer"); return false; }
                if (res.Type == Stun.AllocateError && res.ErrorCode == 401)
                {
                    _realm = res.GetString(Stun.AttrRealm); _nonce = res.GetString(Stun.AttrNonce);
                    if (_realm == null || _nonce == null) return false;
                    _key = Stun.LongTermKey(Server.Username ?? "", _realm, Server.Credential ?? "");
                    res = await Request(new Stun.Msg(Stun.AllocateRequest).AddU32(Stun.AttrRequestedTransport, 17u << 24), true, timeoutMs);
                }
                if (res == null || res.Type != Stun.AllocateSuccess)
                {
                    OnlineLog.Warn($"[TURN] {Server} allocate failed ({res?.ErrorCode})");
                    return false;
                }
                Relayed = res.GetXorAddr(Stun.AttrXorRelayed);
                uint life = res.GetU32(Stun.AttrLifetime, 600);
                OnlineLog.Info($"[TURN] allocated {Relayed} on {Server} for {life}s");
                _ = Task.Run(() => RefreshLoop((int)Math.Max(60, life / 2)));
                return Relayed != null;
            }
            catch (Exception e)
            {
                OnlineLog.Warn($"[TURN] {Server}: {e.Message}");
                return false;
            }
        }

        /// Bind a channel to a peer (also gives it permission). Re-bound every 4 minutes while open.
        public async Task<bool> BindAsync(IPEndPoint peer)
        {
            string k = peer.ToString();
            ushort ch = _channelOf.TryGetValue(k, out var existing) ? existing : (ushort)Interlocked.Increment(ref _nextChannel);
            if (ch > 0x7FFE) return false;
            var res = await Request(new Stun.Msg(Stun.ChannelBindRequest)
                .Add(Stun.AttrChannelNumber, new byte[] { (byte)(ch >> 8), (byte)ch, 0, 0 })
                .AddXorAddr(Stun.AttrXorPeer, peer), true, 5000);
            if (res == null || res.Type != Stun.ChannelBindSuccess)
            {
                OnlineLog.Warn($"[TURN] channel bind to {peer} failed ({res?.ErrorCode})");
                return false;
            }
            bool first = !_channelOf.ContainsKey(k);
            _channelOf[k] = ch; _peerOf[ch] = peer;
            if (first) _ = Task.Run(async () =>
            {
                while (!_closed && _channelOf.ContainsKey(k))
                {
                    try { await Task.Delay(240000, _cts.Token); } catch { return; }
                    if (_channelOf.ContainsKey(k)) await BindAsync(peer);
                }
            });
            return true;
        }

        public void Unbind(IPEndPoint peer)
        {
            if (_channelOf.TryRemove(peer.ToString(), out var ch)) _peerOf.TryRemove(ch, out _); // expires on the server by itself
        }

        /// Send to a bound peer as ChannelData.
        public void SendTo(IPEndPoint peer, byte[] data, int off, int len)
        {
            if (_closed || !_channelOf.TryGetValue(peer.ToString(), out var ch)) return;
            int padded = Server.Tcp ? (len + 3) & ~3 : len;
            var b = new byte[4 + padded];
            b[0] = (byte)(ch >> 8); b[1] = (byte)ch; b[2] = (byte)(len >> 8); b[3] = (byte)len;
            Buffer.BlockCopy(data, off, b, 4, len);
            SendRaw(b);
        }

        // ---- internals

        async Task<Stun.Msg> Request(Stun.Msg m, bool auth, int timeoutMs)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var req = new Stun.Msg(m.Type);
                foreach (var a in m.Attrs) req.Add(a.Key, a.Value);
                if (auth)
                {
                    req.AddString(Stun.AttrUsername, Server.Username ?? "").AddString(Stun.AttrRealm, _realm).AddString(Stun.AttrNonce, _nonce);
                }
                var tcs = new TaskCompletionSource<Stun.Msg>(TaskCreationOptions.RunContinuationsAsynchronously);
                string id = Convert.ToBase64String(req.TxId);
                _pending[id] = tcs;
                var bytes = req.Encode(auth ? _key : null);
                var deadline = Task.Delay(timeoutMs);
                int rto = 300;
                SendRaw(bytes);
                Stun.Msg res = null;
                while (true)
                {
                    var t = await Task.WhenAny(tcs.Task, Task.Delay(rto), deadline);
                    if (t == tcs.Task) { res = tcs.Task.Result; break; }
                    if (t == deadline) break;
                    if (!Server.Tcp) { SendRaw(bytes); rto = Math.Min(rto * 2, 1600); } // UDP retransmit
                }
                _pending.TryRemove(id, out _);
                if (res == null) return null;
                if (auth && res.ErrorCode == 438) // stale nonce: take the new one and retry once
                {
                    var n = res.GetString(Stun.AttrNonce);
                    if (n != null) { _nonce = n; continue; }
                }
                return res;
            }
            return null;
        }

        void SendRaw(byte[] b)
        {
            try
            {
                if (_udp != null) _udp.SendTo(b, _serverEp);
                else if (_stream != null) lock (_sendLock) _stream.Write(b, 0, b.Length);
            }
            catch (Exception e) { if (!_closed) OnlineLog.Warn("[TURN] send: " + e.Message); }
        }

        async Task RefreshLoop(int everySec)
        {
            while (!_closed)
            {
                try { await Task.Delay(everySec * 1000, _cts.Token); } catch { return; }
                var res = await Request(new Stun.Msg(Stun.RefreshRequest).AddU32(Stun.AttrLifetime, 600), true, 5000);
                if (res == null || res.Type != Stun.RefreshSuccess) OnlineLog.Warn($"[TURN] refresh failed ({res?.ErrorCode})");
            }
        }

        void UdpReceiveLoop()
        {
            var buf = new byte[65536];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!_closed)
            {
                int n;
                try { n = _udp.ReceiveFrom(buf, ref from); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                catch { if (_closed) return; continue; }
                Handle(buf, 0, n);
            }
        }

        async Task StreamReceiveLoop()
        {
            var buf = new byte[65536 + 8];
            int have = 0;
            try
            {
                while (!_closed)
                {
                    int n = await _stream.ReadAsync(buf, have, buf.Length - have, _cts.Token);
                    if (n <= 0) break;
                    have += n;
                    int p = 0;
                    while (have - p >= 4)
                    {
                        int body = (buf[p + 2] << 8) | buf[p + 3];
                        int total = (buf[p] & 0xC0) == 0 ? 20 + body : 4 + ((body + 3) & ~3);
                        if (have - p < total) break;
                        Handle(buf, p, (buf[p] & 0xC0) == 0 ? total : 4 + body);
                        p += total;
                    }
                    if (p > 0) { Buffer.BlockCopy(buf, p, buf, 0, have - p); have -= p; }
                }
            }
            catch (Exception e) { if (!_closed) OnlineLog.Warn("[TURN] stream closed: " + e.Message); }
        }

        void Handle(byte[] b, int off, int len)
        {
            if (len < 4) return;
            if ((b[off] & 0xC0) == 0x40) // ChannelData
            {
                ushort ch = (ushort)((b[off] << 8) | b[off + 1]);
                int l = (b[off + 2] << 8) | b[off + 3];
                if (l > len - 4 || !_peerOf.TryGetValue(ch, out var peer)) return;
                OnData?.Invoke(peer, b, off + 4, l);
                return;
            }
            var m = Stun.Msg.Decode(b, off, len);
            if (m == null) return;
            if (m.Type == Stun.DataIndication)
            {
                var peer = m.GetXorAddr(Stun.AttrXorPeer); var data = m.Get(Stun.AttrData);
                if (peer != null && data != null) OnData?.Invoke(peer, data, 0, data.Length);
                return;
            }
            if (_pending.TryGetValue(Convert.ToBase64String(m.TxId), out var tcs)) tcs.TrySetResult(m);
        }

        public void Dispose()
        {
            if (_closed) return;
            try
            {
                if (Relayed != null && _key != null) // release the allocation (best effort, no wait)
                {
                    var m = new Stun.Msg(Stun.RefreshRequest).AddU32(Stun.AttrLifetime, 0)
                        .AddString(Stun.AttrUsername, Server.Username ?? "").AddString(Stun.AttrRealm, _realm).AddString(Stun.AttrNonce, _nonce);
                    SendRaw(m.Encode(_key));
                }
            }
            catch { }
            _closed = true;
            _cts.Cancel();
            try { _udp?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
        }
    }
}
