using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using LiteNetLib;

namespace CanEmAllMP
{
    /// "Steam" connection type: game traffic goes peer-to-peer through Steam (Valve's relay network),
    /// so there is no lobby server, no port forwarding and no Cloudflare. Both players need the game on Steam
    /// and to be Steam friends. The host shows up in its friends' JOIN tab (Steam rich presence).
    ///
    /// Like TURN, each remote player is a LoopProxy: LiteNetLib talks to 127.0.0.1 and never knows the difference.
    public sealed unsafe class SteamLink : IDisposable
    {
        public readonly bool IsHost;
        public readonly ulong HostId;
        public string Status { get; private set; } = "";
        public string FailReason { get; private set; }
        public Action<IPEndPoint> Connect;   // joiner: LiteNetLib connects to this (the proxy)

        readonly LiteNetManager _net;
        readonly string _version;
        uint _listen;
        volatile bool _closed;
        Thread _pump;

        sealed class Route { public uint Conn; public LoopProxy Proxy; public ulong Peer; }
        readonly ConcurrentDictionary<uint, Route> _routes = new ConcurrentDictionary<uint, Route>();
        readonly ConcurrentQueue<(uint conn, uint listen, ulong peer, int state, string why)> _events = new ConcurrentQueue<(uint, uint, ulong, int, string)>();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StatusFn(IntPtr info);
        static StatusFn _statusFn;       // kept alive for the native side
        static IntPtr _statusPtr;
        static SteamLink _current;

        public SteamLink(LiteNetManager net, bool host, ulong hostId, string version)
        {
            _net = net; IsHost = host; HostId = hostId; _version = version;
        }

        public bool Start()
        {
            if (!SteamApi.Available) { FailReason = SteamApi.Error ?? "Steam isn't running"; return false; }
            _current = this;
            if (_statusFn == null)
            {
                _statusFn = OnStatus;
                _statusPtr = Marshal.GetFunctionPointerForDelegate(_statusFn);
            }
            var opt = new SteamApi.ConfigValue { Value = 201 /* Callback_ConnectionStatusChanged */, DataType = 5 /* Ptr */, Val = (long)_statusPtr };
            if (IsHost)
            {
                _listen = SteamApi.SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(SteamApi.Sockets, 0, 1, &opt);
                if (_listen == 0) { FailReason = "Steam wouldn't open a connection for friends"; return false; }
                SteamApi.SetHosting(_version);
                Status = "Hosting through Steam - friends can join from their JOIN tab";
                Plugin.Log.LogInfo($"[Steam] hosting (listen socket {_listen})");
            }
            else
            {
                byte* id = stackalloc byte[136];
                for (int i = 0; i < 136; i++) id[i] = 0;
                SteamApi.SteamAPI_SteamNetworkingIdentity_SetSteamID64(id, HostId);
                uint conn = SteamApi.SteamAPI_ISteamNetworkingSockets_ConnectP2P(SteamApi.Sockets, id, 0, 1, &opt);
                if (conn == 0) { FailReason = "Steam couldn't start a connection to that friend"; return false; }
                var r = NewRoute(conn, HostId);
                Status = "Connecting through Steam...";
                Plugin.Log.LogInfo($"[Steam] connecting to {HostId} (conn {conn}), game sees {r.Proxy.Endpoint}");
                Connect?.Invoke(r.Proxy.Endpoint);
            }
            _pump = new Thread(Pump) { IsBackground = true, Name = "MP steam" };
            _pump.Start();
            return true;
        }

        Route NewRoute(uint conn, ulong peer)
        {
            var r = new Route { Conn = conn, Peer = peer, Proxy = new LoopProxy(_net.LocalPort) };
            r.Proxy.OnFromGame = (b, n) => Send(conn, b, n);
            _routes[conn] = r;
            return r;
        }

        void Send(uint conn, byte[] b, int n)
        {
            if (_closed) return;
            fixed (byte* p = b)
                SteamApi.SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(SteamApi.Sockets, conn, p, (uint)n, 1 /* unreliable, no Nagle */, null);
        }

        /// Background loop: Steam callbacks and incoming messages, handed to LiteNetLib right away (low latency).
        void Pump()
        {
            var msgs = new IntPtr[64];
            var buf = new byte[65536];
            while (!_closed)
            {
                try
                {
                    SteamApi.SteamAPI_ISteamNetworkingSockets_RunCallbacks(SteamApi.Sockets);
                    bool any = false;
                    foreach (var r in _routes.Values)
                    {
                        int n;
                        fixed (IntPtr* pm = msgs) n = SteamApi.SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(SteamApi.Sockets, r.Conn, pm, msgs.Length);
                        if (n < 0) { _events.Enqueue((r.Conn, 0, r.Peer, -1, "connection gone")); continue; }
                        for (int i = 0; i < n; i++)
                        {
                            any = true;
                            var m = msgs[i];
                            var data = *(IntPtr*)m;               // m_pData
                            int len = *(int*)((byte*)m + 8);      // m_cbSize
                            if (len > 0 && len <= buf.Length)
                            {
                                Marshal.Copy(data, buf, 0, len);
                                r.Proxy.Inject(buf, 0, len);
                            }
                            SteamApi.SteamAPI_SteamNetworkingMessage_t_Release(m);
                        }
                    }
                    if (!any) Thread.Sleep(1);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[Steam] pump: " + e.Message); Thread.Sleep(50); }
            }
        }

        /// Native callback (SteamNetConnectionStatusChangedCallback_t*): may run on the pump thread.
        static void OnStatus(IntPtr p)
        {
            try
            {
                var link = _current;
                if (link == null || p == IntPtr.Zero) return;
                byte* b = (byte*)p;
                uint conn = *(uint*)b;                 // m_hConn
                ulong peer = *(ulong*)(b + 16);        // m_info.m_identityRemote (SteamID)
                uint listen = *(uint*)(b + 152);       // m_info.m_hListenSocket
                int state = *(int*)(b + 184);          // m_info.m_eState
                string why = Marshal.PtrToStringUTF8((IntPtr)(b + 192)); // m_info.m_szEndDebug
                link._events.Enqueue((conn, listen, peer, state, why));
            }
            catch { }
        }

        /// Main thread, every frame.
        public void Tick()
        {
            while (_events.TryDequeue(out var e))
            {
                var (conn, listen, peer, state, why) = e;
                bool known = _routes.ContainsKey(conn);
                if (state != -1) Plugin.Log.LogInfo($"[Steam] conn {conn} state {state} peer {peer} listen {listen}" + (string.IsNullOrEmpty(why) ? "" : " - " + why));
                if (IsHost && !known && state != 4 && state != 5 && state != -1)   // a friend knocking (state 1 = connecting)
                {
                    int res = SteamApi.SteamAPI_ISteamNetworkingSockets_AcceptConnection(SteamApi.Sockets, conn);
                    if (res == 1) { NewRoute(conn, peer); Plugin.Log.LogInfo($"[Steam] accepted {SteamApi.NameOf(peer) ?? peer.ToString()}"); }
                    else Plugin.Log.LogWarning($"[Steam] accept failed ({res})");
                    continue;
                }
                if (state == 3 && !IsHost) Status = "Connected through Steam";
                if (state == 4 || state == 5 || state == -1)   // closed by peer / problem / handle gone
                {
                    if (_routes.TryRemove(conn, out var r))
                    {
                        try { r.Proxy.Dispose(); } catch { }
                        try { SteamApi.SteamAPI_ISteamNetworkingSockets_CloseConnection(SteamApi.Sockets, conn, 0, "closed", false); } catch { }
                    }
                    if (!IsHost && FailReason == null && conn != 0 && !_wasConnected)
                        FailReason = "Couldn't connect through Steam" + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")");
                }
            }
        }

        bool _wasConnected;
        public void OnConnected() { _wasConnected = true; Status = "Connected through Steam"; }

        /// Host: is this LiteNetLib peer one of our Steam proxies?
        public bool Owns(IPEndPoint ep)
        {
            if (ep == null) return false;
            foreach (var r in _routes.Values) if (r.Proxy.Endpoint.Port == ep.Port && IPAddress.IsLoopback(ep.Address)) return true;
            return false;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _pump?.Join(200); } catch { }
            foreach (var r in _routes.Values)
            {
                try { r.Proxy.Dispose(); } catch { }
                try { SteamApi.SteamAPI_ISteamNetworkingSockets_CloseConnection(SteamApi.Sockets, r.Conn, 0, "bye", true); } catch { }
            }
            _routes.Clear();
            if (_listen != 0) { try { SteamApi.SteamAPI_ISteamNetworkingSockets_CloseListenSocket(SteamApi.Sockets, _listen); } catch { } }
            if (IsHost) SteamApi.ClearHosting();
            if (_current == this) _current = null;
        }
    }
}
