using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib.Layers;

namespace CanEmAllMP
{
    /// A local UDP socket on 127.0.0.1 that stands in for one remote player.
    /// LiteNetLib talks to it like a normal peer; whatever it sends here goes out through TURN or the
    /// lobby relay (OnFromGame), and whatever arrives from that path is handed to LiteNetLib (Inject).
    public sealed class LoopProxy : IDisposable
    {
        public IPEndPoint Endpoint { get; }
        public Action<byte[], int> OnFromGame;
        readonly Socket _s;
        readonly IPEndPoint _game;
        volatile bool _closed;

        public LoopProxy(int gamePort)
        {
            _game = new IPEndPoint(IPAddress.Loopback, gamePort);
            _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            try { _s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { } // Windows: ignore ICMP resets
            Endpoint = (IPEndPoint)_s.LocalEndPoint;
            new Thread(Loop) { IsBackground = true, Name = "MP proxy " + Endpoint.Port }.Start();
        }

        void Loop()
        {
            var buf = new byte[65536];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!_closed)
            {
                int n;
                try { n = _s.ReceiveFrom(buf, ref from); }
                catch { if (_closed) return; continue; }
                if (n > 0 && ((IPEndPoint)from).Port == _game.Port) OnFromGame?.Invoke(buf, n);
            }
        }

        public void Inject(byte[] b, int off, int len)
        {
            if (_closed) return;
            try { _s.SendTo(b, off, len, SocketFlags.None, _game); } catch { }
        }

        public void Dispose() { _closed = true; try { _s.Close(); } catch { } }
    }

    /// Sits under LiteNetLib's socket: answers STUN binding responses and handles connectivity probes
    /// ("CEMP" + type + token), so LiteNetLib never sees either.
    ///   probe (type 0): someone is checking if they can reach us -> queue an ack back to where it came from
    ///   ack   (type 1): one of our probes got through -> that endpoint works
    public sealed class OnlineLayer : PacketLayerBase
    {
        public const int ProbeLen = 9;
        volatile byte[] _stunTx;
        public volatile IPEndPoint Mapped;
        public readonly ConcurrentQueue<(IPEndPoint ep, uint token)> Probes = new ConcurrentQueue<(IPEndPoint, uint)>();
        public readonly ConcurrentQueue<(IPEndPoint ep, uint token)> Acks = new ConcurrentQueue<(IPEndPoint, uint)>();

        /// Tests only: drop matching inbound packets (simulates a firewall).
        public Func<IPEndPoint, bool> TestDrop;

        public OnlineLayer() : base(0) { }

        public void ExpectStun(byte[] txId) { _stunTx = txId; }

        public static byte[] MakeProbe(byte type, uint token) =>
            new byte[] { (byte)'C', (byte)'E', (byte)'M', (byte)'P', type, (byte)(token >> 24), (byte)(token >> 16), (byte)(token >> 8), (byte)token };

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            var tx = _stunTx;
            bool stun = tx != null && Stun.IsStun(data, 0, length);
            bool probe = length == ProbeLen && data[0] == 'C' && data[1] == 'E' && data[2] == 'M' && data[3] == 'P';
            if (TestDrop != null && !stun && TestDrop(endPoint)) { length = 0; return; }
            if (probe)
            {
                uint token = (uint)(data[5] << 24 | data[6] << 16 | data[7] << 8 | data[8]);
                var ep = new IPEndPoint(endPoint.Address, endPoint.Port); // LiteNetLib reuses its endpoint object
                if (data[4] == 0) Probes.Enqueue((ep, token)); else Acks.Enqueue((ep, token));
                length = 0;
                return;
            }
            if (stun)
            {
                bool match = true;
                for (int i = 0; i < 12 && match; i++) match = data[8 + i] == tx[i];
                if (!match) return;
                var m = Stun.Msg.Decode(data, 0, length);
                var ep = m?.GetXorAddr(Stun.AttrXorMapped) ?? Stun.ParseAddr(m?.Get(Stun.AttrMappedAddress));
                if (ep != null) Mapped = ep;
                length = 0;
            }
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length) { }
    }

    /// WebSocket client for the lobby server (JSON text messages + binary relay frames).
    /// Everything received is queued; call Drain() from the game thread.
    public sealed class LobbySocket : IDisposable
    {
        public readonly ConcurrentQueue<JsonElement> Messages = new ConcurrentQueue<JsonElement>();
        public Action<int, byte[], int, int> OnRelay; // (peer, buf, offset, length) - background thread!
        public volatile bool Open;
        public volatile string CloseReason;
        public volatile int CloseCode;

        readonly ClientWebSocket _ws = new ClientWebSocket();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly BlockingCollection<(byte[] data, bool binary)> _out = new BlockingCollection<(byte[], bool)>(4096);

        public async Task<bool> ConnectAsync(Uri uri, int timeoutMs = 10000)
        {
            try
            {
                _ws.Options.KeepAliveInterval = TimeSpan.Zero; // we send our own "ping" (answered without waking the server)
                var t = _ws.ConnectAsync(uri, _cts.Token);
                if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t) { CloseReason = "Lobby server didn't answer"; return false; }
                await t;
                Open = true;
                _ = Task.Run(ReceiveLoop);
                new Thread(SendLoop) { IsBackground = true, Name = "MP lobby send" }.Start();
                _ = Task.Run(async () =>
                {
                    while (Open) { try { await Task.Delay(25000, _cts.Token); } catch { return; } SendText("ping"); }
                });
                return true;
            }
            catch (Exception e)
            {
                CloseReason = Friendly(e);
                return false;
            }
        }

        // Short, readable reasons for the menu's Status line (full exception goes to the log).
        static string Friendly(Exception e)
        {
            string raw = e.InnerException?.Message ?? e.Message ?? "";
            Plugin.Log.LogWarning("[Online] lobby connect failed: " + raw);
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"status code '(\d+)'");
            if (m.Success)
            {
                int code = int.Parse(m.Groups[1].Value);
                if (code == 429) return "Too many tries - wait a minute and try again";
                if (code >= 500) return "Lobby server is down or hit its daily limit - use Direct IP or try again later";
                return "Lobby server refused the connection (" + code + ") - use Direct IP";
            }
            return "Can't reach the lobby server - check your internet, or use Direct IP";
        }

        public void Send(object msg) => SendText(JsonSerializer.Serialize(msg));
        public void SendText(string s) { if (Open) _out.TryAdd((Encoding.UTF8.GetBytes(s), false)); }

        public void SendRelay(int peer, byte[] b, int len)
        {
            if (!Open) return;
            var f = new byte[len + 2];
            f[0] = (byte)(peer >> 8); f[1] = (byte)peer;
            Buffer.BlockCopy(b, 0, f, 2, len);
            _out.TryAdd((f, true)); // full queue = drop (it's UDP traffic anyway)
        }

        void SendLoop()
        {
            try
            {
                foreach (var (data, binary) in _out.GetConsumingEnumerable(_cts.Token))
                    _ws.SendAsync(new ArraySegment<byte>(data), binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, _cts.Token)
                       .GetAwaiter().GetResult();
            }
            catch { }
        }

        async Task ReceiveLoop()
        {
            var buf = new byte[16384];
            var acc = new List<byte>();
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    byte[] msg; int len;
                    if (r.EndOfMessage && acc.Count == 0) { msg = buf; len = r.Count; }
                    else
                    {
                        for (int i = 0; i < r.Count; i++) acc.Add(buf[i]);
                        if (!r.EndOfMessage) continue;
                        msg = acc.ToArray(); len = msg.Length; acc.Clear();
                    }
                    if (r.MessageType == WebSocketMessageType.Binary)
                    {
                        if (len >= 3) OnRelay?.Invoke((msg[0] << 8) | msg[1], msg, 2, len - 2);
                        continue;
                    }
                    var s = Encoding.UTF8.GetString(msg, 0, len);
                    if (s == "pong") continue;
                    try { using (var d = JsonDocument.Parse(s)) Messages.Enqueue(d.RootElement.Clone()); } catch { }
                }
            }
            catch (Exception e) { if (!_cts.IsCancellationRequested && CloseReason == null) CloseReason = "Lobby connection lost (" + e.Message + ")"; }
            try { if (_ws.CloseStatus.HasValue) CloseCode = (int)_ws.CloseStatus.Value; } catch { }
            Open = false;
        }

        public void Dispose()
        {
            Open = false;
            try { _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500); } catch { }
            _cts.Cancel();
            try { _ws.Dispose(); } catch { }
        }
    }
}
