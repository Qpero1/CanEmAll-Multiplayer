using System.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CanEmAllMP
{
    public enum NetMode { Offline, Host, Client }

    public class RemotePlayerInfo
    {
        public int Id;
        public string Name = "?";
        public int Color = -1;
        public LiteNetPeer Peer; // host side only
    }

    /// <summary>
    /// Thin wrapper around LiteNetLib. Host = player id 0, clients get ids from the host.
    /// All events are raised on the Unity main thread (we call PollEvents from Update).
    /// </summary>
    public class NetSession
    {
        public const string ConnectKey = "CanEmAllMP-v1";
        public const string ModVersion = "0.31.2";

        public NetMode Mode { get; private set; } = NetMode.Offline;
        public bool IsHost => Mode == NetMode.Host;
        public bool IsClient => Mode == NetMode.Client;
        public bool Active => Mode != NetMode.Offline;
        public bool Connected => IsHost || (IsClient && _server != null && _server.ConnectionState == ConnectionState.Connected && LocalId >= 0);
        public int LocalId { get; private set; } = -1;
        public string LocalName = "Player";
        public int LocalColor = -1;   // wanted colour (-1 = any)
        public event Action<int> OnPlayerColor;

        /// First colour nobody else is using (prefers "wanted" when it's free).
        public int FreeColor(int wanted, int forPlayer)
        {
            bool Taken(int c) { foreach (var p in Players.Values) if (p.Id != forPlayer && p.Color == c) return true; return false; }
            if (wanted >= 0 && wanted < Palette.Count && !Taken(wanted)) return wanted;
            for (int c = 0; c < Palette.Count; c++) if (!Taken(c)) return c;
            return Palette.Clamp(forPlayer);
        }
        public bool ColorTaken(int c)
        {
            foreach (var p in Players.Values) if (p.Id != LocalId && p.Color == c) return true;
            return false;
        }
        public int MyColor => Players.TryGetValue(LocalId, out var me) && me.Color >= 0 ? me.Color : Palette.Clamp(LocalColor < 0 ? 0 : LocalColor);

        /// Change our colour (any time). Host decides; a taken colour is refused.
        public void RequestColor(int c)
        {
            LocalColor = c;
            if (!Active) return;
            if (IsHost)
            {
                if (ColorTaken(c)) return;
                Players[0].Color = c;
                Begin(Msg.PlayerColor).Put(0); W.Put((byte)c); Send();
                OnPlayerColor?.Invoke(0);
            }
            else if (Connected) { Begin(Msg.PlayerColor).Put(LocalId); W.Put((byte)c); Send(); }
        }
        public string Status = "Offline";
        public int PingMs => _server?.RoundTripTime ?? 0; // round trip (LiteNetLib's Ping is half of it)

        /// Largest unreliable message (type byte included) every connection can take in one packet.
        /// LiteNetLib throws on anything bigger, so all streams are chunked to this.
        public int MaxUnreliablePayload
        {
            get
            {
                int max = 1000;
                try
                {
                    if (IsClient && _server != null) max = _server.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
                    else if (IsHost)
                        foreach (var p in Players.Values)
                            if (p.Peer != null) max = Math.Min(max, p.Peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable));
                }
                catch { }
                return Math.Max(400, Math.Min(max, 1400));
            }
        }

        public readonly Dictionary<int, RemotePlayerInfo> Players = new Dictionary<int, RemotePlayerInfo>();

        public Func<bool> HostAcceptsJoins = () => true;
        /// Host: most players allowed, including the host (0 = no limit).
        public int MaxPlayers = 8;
        public string LastRejectReason;
        public event Action<int, Msg, NetPacketReader> OnMessage;   // (senderPlayerId, type, reader)
        public event Action<int> OnPlayerJoined;
        public event Action<int, string> OnPlayerLeft; // (id, name)
        public event Action OnConnectedToHost;
        public event Action<DisconnectReason> OnDisconnected;

        private LiteNetManager _net;
        private EventBasedLiteNetListener _listener;
        private LiteNetPeer _server; // client side
        private readonly Dictionary<int, int> _peerToPlayer = new Dictionary<int, int>(); // host: peer.Id -> playerId
        private int _nextPlayerId = 1;
        public readonly NetDataWriter W = new NetDataWriter();

        private void CreateManager()
        {
            _listener = new EventBasedLiteNetListener();
            _listener.ConnectionRequestEvent += req =>
            {
                if (!IsHost) { req.Reject(); return; }
                string why = MaxPlayers > 0 && Players.Count >= MaxPlayers ? "Session is full" : !HostAcceptsJoins() ? "Host not in game" : null;
                if (why == null) { req.AcceptIfKey(ConnectKey); return; }
                var rw = new NetDataWriter(); rw.Put(why);
                req.Reject(rw);
            };
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += (ep, err) => Plugin.Log.LogWarning($"Socket error {err} from {ep}");
            _net = new LiteNetManager(_listener)
            {
                AutoRecycle = true,
                DisconnectTimeout = 10000,
                UpdateTime = 15,
                IPv6Enabled = false,
                UnsyncedEvents = false,
            };
        }

        public bool Host(int port)
        {
            Shutdown();
            CreateManager();
            if (!_net.Start(port))
            {
                Status = $"Could not open port {port} (already in use?)";
                _net = null;
                return false;
            }
            Mode = NetMode.Host;
            LocalId = 0;
            Players.Clear();
            Players[0] = new RemotePlayerInfo { Id = 0, Name = LocalName, Color = Palette.Clamp(LocalColor < 0 ? 0 : LocalColor) };
            Status = $"Hosting on UDP port {port}";
            Plugin.Log.LogInfo(Status);
            return true;
        }

        public bool Join(string address, int port)
        {
            Shutdown();
            CreateManager();
            _net.Start();
            try
            {
                // resolve hostnames (e.g. a DDNS name) to IPv4
                if (!IPAddress.TryParse(address, out var ip))
                {
                    foreach (var a in Dns.GetHostAddresses(address))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                }
                if (ip == null) { Status = "Could not resolve " + address; Shutdown(); return false; }
                _server = _net.Connect(new IPEndPoint(ip, port), ConnectKey);
            }
            catch (Exception e)
            {
                Status = "Connect failed: " + e.Message;
                Shutdown();
                return false;
            }
            Mode = NetMode.Client;
            LocalId = -1;
            Status = $"Connecting to {address}:{port}...";
            return true;
        }

        public void Shutdown()
        {
            if (_net != null)
            {
                try { _net.Stop(true); } catch { }
            }
            _net = null;
            _server = null;
            Mode = NetMode.Offline;
            LocalId = -1;
            Players.Clear();
            _peerToPlayer.Clear();
            _nextPlayerId = 1;
            Status = "Offline";
        }

        public void Poll()
        {
            _net?.PollEvents();
        }

        // ---------------- sending ----------------

        public NetDataWriter Begin(Msg type)
        {
            W.Reset();
            W.Put((byte)type);
            return W;
        }

        /// <summary>Client: send to host. Host: send to every client.</summary>
        public void Send(DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (IsClient) _server?.Send(W, method);
            else if (IsHost) _net?.SendToAll(W, method);
        }

        /// <summary>Host only: send to everyone except one player id.</summary>
        public void SendExcept(int playerId, DeliveryMethod method)
        {
            if (!IsHost) return;
            foreach (var p in Players.Values)
                if (p.Id != playerId && p.Peer != null) p.Peer.Send(W, method);
        }

        public void SendTo(int playerId, DeliveryMethod method)
        {
            if (IsHost && Players.TryGetValue(playerId, out var p) && p.Peer != null) p.Peer.Send(W, method);
        }

        // ---------------- events ----------------

        private void OnPeerConnected(LiteNetPeer peer)
        {
            if (IsClient)
            {
                Status = "Connected, handshaking...";
                Begin(Msg.Hello).Put(LocalName);
                W.Put(ModVersion);
                W.Put((sbyte)LocalColor);
                _server.Send(W, DeliveryMethod.ReliableOrdered);
            }
            // host waits for Hello before announcing the player
        }

        private void OnPeerDisconnected(LiteNetPeer peer, DisconnectInfo info)
        {
            if (IsClient)
            {
                var reason = info.Reason.ToString();
                LastRejectReason = null;
                try { if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0) LastRejectReason = info.AdditionalData.GetString(64); } catch { }
                Plugin.Log.LogInfo("Disconnected from host: " + reason);
                Shutdown();
                Status = "Disconnected: " + reason;
                OnDisconnected?.Invoke(info.Reason);
                return;
            }
            if (IsHost && _peerToPlayer.TryGetValue(peer.Id, out var pid))
            {
                _peerToPlayer.Remove(peer.Id);
                var leftName = NameOf(pid);
                Players.Remove(pid);
                Begin(Msg.PlayerLeft).Put(pid);
                Send();
                OnPlayerLeft?.Invoke(pid, leftName);
                Plugin.Log.LogInfo($"Player {pid} left ({info.Reason})");
            }
        }

        private void OnReceive(LiteNetPeer peer, NetPacketReader r, DeliveryMethod method)
        {
            if (r.AvailableBytes < 1) return;
            var type = (Msg)r.GetByte();
            try
            {
                if (IsHost) HostReceive(peer, type, r);
                else ClientReceive(type, r);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Error handling {type}: {e}");
            }
        }

        private void HostReceive(LiteNetPeer peer, Msg type, NetPacketReader r)
        {
            if (type == Msg.Hello)
            {
                var name = r.GetString(32);
                var ver = r.GetString(16);
                if (ver != ModVersion)
                {
                    // different versions talk different protocols: refuse instead of glitching
                    Plugin.Log.LogWarning($"{name} has mod version {ver}, host has {ModVersion}: refused");
                    var rw = new NetDataWriter();
                    rw.Put($"Mod version mismatch: host has v{ModVersion}, you have v{ver}");
                    peer.Disconnect(rw);
                    return;
                }
                int wantColor = r.AvailableBytes >= 1 ? r.GetSByte() : -1;
                int id = _nextPlayerId++;
                _peerToPlayer[peer.Id] = id;
                name = UniqueName(name);
                var info = new RemotePlayerInfo { Id = id, Name = name, Peer = peer };
                info.Color = FreeColor(wantColor, id);

                // tell the new player who they are and who is already here
                Begin(Msg.Welcome).Put(id);
                W.Put((byte)info.Color);
                W.Put(Players.Count);
                foreach (var p in Players.Values) { W.Put(p.Id); W.Put(p.Name); W.Put((byte)Math.Max(0, p.Color)); }
                W.Put(name); // the name you'll go by (John2 if there's already a John)
                peer.Send(W, DeliveryMethod.ReliableOrdered);

                Players[id] = info;
                // tell everyone else
                Begin(Msg.PlayerJoined).Put(id);
                W.Put(name);
                W.Put((byte)info.Color);
                SendExcept(id, DeliveryMethod.ReliableOrdered);

                Plugin.Log.LogInfo($"{name} joined as player {id}");
                OnPlayerJoined?.Invoke(id);
                return;
            }

            if (!_peerToPlayer.TryGetValue(peer.Id, out var sender)) return; // no Hello yet
            if (type == Msg.PlayerColor)
            {
                r.GetInt(); int c = r.GetByte();
                if (c < Palette.Count && Players.TryGetValue(sender, out var sp) && !Players.Values.Any(o => o.Id != sender && o.Color == c))
                    sp.Color = c;
                if (Players.TryGetValue(sender, out var sp2))
                {
                    Begin(Msg.PlayerColor).Put(sender); W.Put((byte)Math.Max(0, sp2.Color)); Send();  // everyone (incl. sender: confirms or reverts)
                    OnPlayerColor?.Invoke(sender);
                }
                return;
            }
            OnMessage?.Invoke(sender, type, r);
        }

        private void ClientReceive(Msg type, NetPacketReader r)
        {
            switch (type)
            {
                case Msg.Welcome:
                {
                    LocalId = r.GetInt();
                    int myColor = r.GetByte();
                    int n = r.GetInt();
                    Players.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var id = r.GetInt();
                        var nm = r.GetString(32);
                        Players[id] = new RemotePlayerInfo { Id = id, Name = nm, Color = r.GetByte() };
                    }
                    string myName = r.AvailableBytes > 0 ? r.GetString(32) : LocalName;
                    Players[LocalId] = new RemotePlayerInfo { Id = LocalId, Name = myName, Color = myColor };
                    Status = $"Connected as player {LocalId}";
                    Plugin.Log.LogInfo(Status);
                    OnConnectedToHost?.Invoke();
                    foreach (var id in Players.Keys) if (id != LocalId) OnPlayerJoined?.Invoke(id);
                    return;
                }
                case Msg.PlayerJoined:
                {
                    var id = r.GetInt();
                    var nm = r.GetString(32);
                    int col = r.AvailableBytes >= 1 ? r.GetByte() : -1;
                    Players[id] = new RemotePlayerInfo { Id = id, Name = nm, Color = col };
                    OnPlayerJoined?.Invoke(id);
                    return;
                }
                case Msg.PlayerColor:
                {
                    var id = r.GetInt(); int col = r.GetByte();
                    if (Players.TryGetValue(id, out var pc)) pc.Color = col;
                    OnPlayerColor?.Invoke(id);
                    return;
                }
                case Msg.PlayerLeft:
                {
                    var id = r.GetInt();
                    var leftName = NameOf(id);
                    Players.Remove(id);
                    OnPlayerLeft?.Invoke(id, leftName);
                    return;
                }
            }
            if (LocalId < 0) return;
            OnMessage?.Invoke(0, type, r); // everything else on a client comes from the host (id 0)
        }

        /// Host: drop a player with a message they'll see.
        public void Kick(int playerId)
        {
            if (!IsHost || playerId == LocalId || !Players.TryGetValue(playerId, out var p) || p.Peer == null) return;
            var w = new NetDataWriter();
            w.Put("You were kicked by the host");
            Plugin.Log.LogInfo($"Kicking {p.Name}");
            p.Peer.Disconnect(w);
        }

        /// Host: "John" -> "John2", "John3"... if the name is already in the session (case doesn't matter).
        private string UniqueName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
            bool Taken(string n) { foreach (var p in Players.Values) if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return true; return false; }
            if (!Taken(name)) return name;
            for (int i = 2; i < 1000; i++)
            {
                string n = name + i;
                if (n.Length > 32) n = name.Substring(0, Math.Max(1, 32 - i.ToString().Length)) + i;
                if (!Taken(n)) return n;
            }
            return name;
        }

        public int PingOf(int playerId) => Players.TryGetValue(playerId, out var p) && p.Peer != null ? p.Peer.RoundTripTime : -1;

        public string NameOf(int id) => Players.TryGetValue(id, out var p) ? p.Name : "Player " + id;
    }
}
