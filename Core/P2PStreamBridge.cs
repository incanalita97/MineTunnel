using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public class PeerSession : IDisposable
    {
        public IPEndPoint PeerEndpoint { get; set; }
        public TcpClient ServerConn { get; set; }
        public NetworkStream ServerStream { get; set; }
        public ReliableUdpTransport RudpTransport { get; set; }
        public Thread ReadThread { get; set; }
        public volatile bool IsActive;

        public void Dispose()
        {
            IsActive = false;
            if (RudpTransport != null)
            {
                try { RudpTransport.Dispose(); } catch { }
                RudpTransport = null;
            }
            if (ServerStream != null)
            {
                try { ServerStream.Dispose(); } catch { }
                ServerStream = null;
            }
            if (ServerConn != null)
            {
                try { ServerConn.Close(); } catch { }
                ServerConn = null;
            }
            if (ReadThread != null && ReadThread.IsAlive)
            {
                try { ReadThread.Join(200); } catch { }
                ReadThread = null;
            }
        }
    }

    public class P2PStreamBridge : IDisposable
    {
        private readonly P2PHolePuncher _puncher;
        private readonly LanMulticastBridge _lanBridge;
        private TcpListener _clientProxyListener;
        private TcpClient _activeGameClient;
        private NetworkStream _gameStream;
        private Thread _clientListenThread;
        private Thread _clientReadThread;
        private Thread _clientLanBroadcastThread;

        private ReliableUdpTransport _clientRudpTransport;

        private readonly Dictionary<string, PeerSession> _peerSessions = new Dictionary<string, PeerSession>();
        private volatile bool _isRunning;
        private bool _isHostMode;
        private int _localProxyPort;
        private int _targetServerPort;
        private readonly object _lock = new object();

        // Event handler references for clean unsubscription on Dispose
        private readonly Action<IPEndPoint, byte[]> _dataReceivedHandler;
        private readonly Action<IPEndPoint, long> _connectedHandler;
        private readonly Action<IPEndPoint, string> _peerDisconnectedHandler;
        private readonly Action _disconnectedHandler;

        public event Action<string> OnLog;
        public event Action<string> OnStatusChanged;
        public event Action<int> OnLocalProxyBound;

        public bool IsRunning { get { return _isRunning; } }
        public int LocalProxyPort { get { return _localProxyPort; } }
        public int TargetServerPort { get { return _targetServerPort; } }

        public P2PStreamBridge(P2PHolePuncher puncher, LanMulticastBridge lanBridge = null)
        {
            _puncher = puncher;
            _lanBridge = lanBridge;
            _localProxyPort = 25565;
            _targetServerPort = 25565;

            // Single unified event handler
            _dataReceivedHandler = HandleUnifiedDataReceived;
            _connectedHandler = delegate(IPEndPoint peer, long ping)
            {
                if (OnStatusChanged != null)
                {
                    OnStatusChanged(string.Format("🟢 Direct P2P Connected ({0}ms)", Math.Max(1, ping)));
                }
            };
            _disconnectedHandler = delegate
            {
                if (OnStatusChanged != null)
                {
                    OnStatusChanged("P2P Disconnected");
                }
            };
            _peerDisconnectedHandler = delegate(IPEndPoint peer, string name)
            {
                ClosePeerSession(peer);
            };

            _puncher.OnDataReceived += _dataReceivedHandler;
            _puncher.OnConnected += _connectedHandler;
            _puncher.OnDisconnected += _disconnectedHandler;
            _puncher.OnPeerDisconnected += _peerDisconnectedHandler;
        }

        public void UpdateTargetServerPort(int port)
        {
            lock (_lock)
            {
                if (port > 0 && _targetServerPort != port)
                {
                    _targetServerPort = port;
                    Log(string.Format("P2P Host target Minecraft port dynamically updated to {0}", _targetServerPort));
                }
            }
        }

        public bool StartHostBridge(int targetServerPort = 25565)
        {
            lock (_lock)
            {
                Stop();
                _isHostMode = true;
                _targetServerPort = targetServerPort > 0 ? targetServerPort : 25565;
                _isRunning = true;

                Log("P2P Host Stream Bridge initialized for local Minecraft port " + _targetServerPort);
                return true;
            }
        }

        public bool StartClientProxy(int preferredPort = 25565)
        {
            lock (_lock)
            {
                Stop();
                _isHostMode = false;
                _isRunning = true;

                // Initialize Reliable UDP Transport for Client
                _clientRudpTransport = new ReliableUdpTransport(delegate(byte[] frame)
                {
                    _puncher.SendData(frame);
                });
                _clientRudpTransport.OnStreamDataReceived += delegate(byte[] streamChunk)
                {
                    // Pipe in-order contiguous Minecraft bytes to game client
                    lock (_lock)
                    {
                        if (_gameStream != null && _gameStream.CanWrite)
                        {
                            try
                            {
                                _gameStream.Write(streamChunk, 0, streamChunk.Length);
                                _gameStream.Flush();
                            }
                            catch { }
                        }
                    }
                };

                // Try binding to preferred port (25565) first
                int bindPort = preferredPort;
                bool bound = false;

                try
                {
                    _clientProxyListener = new TcpListener(IPAddress.Loopback, bindPort);
                    _clientProxyListener.Start();
                    _localProxyPort = ((IPEndPoint)_clientProxyListener.LocalEndpoint).Port;
                    bound = true;
                }
                catch
                {
                    // If preferred port is occupied, bind OS dynamic port
                    try
                    {
                        _clientProxyListener = new TcpListener(IPAddress.Loopback, 0);
                        _clientProxyListener.Start();
                        _localProxyPort = ((IPEndPoint)_clientProxyListener.LocalEndpoint).Port;
                        bound = true;
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to bind local Minecraft proxy listener: " + ex.Message);
                        return false;
                    }
                }

                if (bound)
                {
                    Log("Minecraft local proxy listening on 127.0.0.1:" + _localProxyPort);
                    if (OnLocalProxyBound != null) OnLocalProxyBound(_localProxyPort);

                    _clientListenThread = new Thread(ClientProxyListenLoop) { IsBackground = true, Name = "P2P_ClientProxy" };
                    _clientListenThread.Start();

                    if (_lanBridge != null)
                    {
                        _clientLanBroadcastThread = new Thread(ClientLanBroadcastLoop) { IsBackground = true, Name = "P2P_LanBroadcast" };
                        _clientLanBroadcastThread.Start();
                    }

                    return true;
                }

                return false;
            }
        }

        private void ClientProxyListenLoop()
        {
            while (_isRunning && _clientProxyListener != null)
            {
                try
                {
                    TcpClient client = _clientProxyListener.AcceptTcpClient();
                    client.NoDelay = true; // Disable Nagle's algorithm for instant game packets

                    lock (_lock)
                    {
                        if (_activeGameClient != null)
                        {
                            try { _activeGameClient.Close(); } catch { }
                        }
                        _activeGameClient = client;
                        _gameStream = client.GetStream();
                    }

                    Log("Minecraft game client connected to local proxy!");
                    _clientReadThread = new Thread(ClientGameStreamReadLoop) { IsBackground = true, Name = "P2P_GameStreamRead" };
                    _clientReadThread.Start();
                }
                catch (SocketException) { if (!_isRunning) break; }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Log("Proxy accept error: " + ex.Message);
                }
            }
        }

        private void ClientGameStreamReadLoop()
        {
            byte[] buffer = new byte[16 * 1024];

            while (_isRunning && _gameStream != null)
            {
                try
                {
                    int read = _gameStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    // Deliver through RUDP (automatically sliced into safe sub-MTU frames with SACK reliability)
                    if (_clientRudpTransport != null)
                    {
                        _clientRudpTransport.SendStreamBytes(buffer, 0, read);
                    }
                }
                catch { break; }
            }

            Log("Minecraft client disconnected from local proxy.");
        }

        // Single unified data dispatch entry point
        private void HandleUnifiedDataReceived(IPEndPoint remotePeer, byte[] payload)
        {
            if (!_isRunning || payload == null || payload.Length == 0) return;

            if (_isHostMode && remotePeer != null)
            {
                // Route through session identified by authenticated peer
                string key = remotePeer.ToString();
                PeerSession session = GetOrCreateHostPeerSession(remotePeer, key);
                if (session != null && session.RudpTransport != null)
                {
                    session.RudpTransport.ProcessIncomingDatagram(payload);
                }
            }
            else if (!_isHostMode)
            {
                // Client routes through client RUDP transport
                if (_clientRudpTransport != null)
                {
                    _clientRudpTransport.ProcessIncomingDatagram(payload);
                }
            }
        }

        private PeerSession GetOrCreateHostPeerSession(IPEndPoint peer, string key)
        {
            lock (_lock)
            {
                PeerSession session;
                if (_peerSessions.TryGetValue(key, out session) && session != null && session.IsActive && session.ServerConn != null && session.ServerConn.Connected)
                {
                    return session;
                }

                try
                {
                    TcpClient client = new TcpClient();
                    client.NoDelay = true;
                    client.Connect("127.0.0.1", _targetServerPort);

                    session = new PeerSession
                    {
                        PeerEndpoint = peer,
                        ServerConn = client,
                        ServerStream = client.GetStream(),
                        IsActive = true
                    };

                    // Initialize RUDP transport for this peer session
                    session.RudpTransport = new ReliableUdpTransport(delegate(byte[] frame)
                    {
                        _puncher.SendDataTo(frame, peer);
                    });

                    // When contiguous in-order bytes arrive from player, pipe to local Minecraft server
                    session.RudpTransport.OnStreamDataReceived += delegate(byte[] streamBytes)
                    {
                        try
                        {
                            if (session.ServerStream != null && session.ServerStream.CanWrite)
                            {
                                session.ServerStream.Write(streamBytes, 0, streamBytes.Length);
                                session.ServerStream.Flush();
                            }
                        }
                        catch
                        {
                            ClosePeerSession(key);
                        }
                    };

                    _peerSessions[key] = session;
                    Log(string.Format("Opened dedicated Minecraft session on port {0} for peer {1}", _targetServerPort, peer));

                    session.ReadThread = new Thread(delegate() { HostPeerServerReadLoop(session); })
                    {
                        IsBackground = true,
                        Name = "P2P_HostPeerRead_" + peer.Port
                    };
                    session.ReadThread.Start();

                    return session;
                }
                catch (Exception ex)
                {
                    Log(string.Format("Failed to connect to Minecraft server for peer {0}: {1}", peer, ex.Message));
                    return null;
                }
            }
        }

        private void HostPeerServerReadLoop(PeerSession session)
        {
            byte[] buffer = new byte[16 * 1024];

            while (_isRunning && session.IsActive && session.ServerStream != null)
            {
                try
                {
                    int read = session.ServerStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    // Send Minecraft server response back to this specific peer over RUDP
                    if (session.RudpTransport != null)
                    {
                        session.RudpTransport.SendStreamBytes(buffer, 0, read);
                    }
                }
                catch { break; }
            }

            ClosePeerSession(session.PeerEndpoint.ToString());
        }

        public void ClosePeerSession(IPEndPoint peer)
        {
            if (peer != null)
            {
                ClosePeerSession(peer.ToString());
            }
        }

        private void ClosePeerSession(string key)
        {
            lock (_lock)
            {
                PeerSession s;
                if (_peerSessions.TryGetValue(key, out s))
                {
                    _peerSessions.Remove(key);
                    if (s != null) s.Dispose();
                }
            }
        }

        private void ClientLanBroadcastLoop()
        {
            while (_isRunning && !_isHostMode)
            {
                try
                {
                    if (_lanBridge != null && _localProxyPort > 0)
                    {
                        _lanBridge.RebroadcastLocally("MineTunnel P2P World", _localProxyPort);
                    }
                    Thread.Sleep(1500);
                }
                catch (ThreadAbortException) { break; }
                catch { }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _isRunning = false;

                if (_clientProxyListener != null)
                {
                    try { _clientProxyListener.Stop(); } catch { }
                    _clientProxyListener = null;
                }

                if (_clientRudpTransport != null)
                {
                    try { _clientRudpTransport.Dispose(); } catch { }
                    _clientRudpTransport = null;
                }

                if (_gameStream != null)
                {
                    try { _gameStream.Dispose(); } catch { }
                    _gameStream = null;
                }

                if (_activeGameClient != null)
                {
                    try { _activeGameClient.Close(); } catch { }
                    _activeGameClient = null;
                }

                if (_clientListenThread != null && _clientListenThread.IsAlive)
                {
                    try { _clientListenThread.Join(200); } catch { }
                    _clientListenThread = null;
                }

                if (_clientReadThread != null && _clientReadThread.IsAlive)
                {
                    try { _clientReadThread.Join(200); } catch { }
                    _clientReadThread = null;
                }

                if (_clientLanBroadcastThread != null && _clientLanBroadcastThread.IsAlive)
                {
                    try { _clientLanBroadcastThread.Join(200); } catch { }
                    _clientLanBroadcastThread = null;
                }

                foreach (KeyValuePair<string, PeerSession> kv in _peerSessions)
                {
                    try { if (kv.Value != null) kv.Value.Dispose(); } catch { }
                }
                _peerSessions.Clear();
            }
        }

        public void Dispose()
        {
            Stop();

            // Unsubscribe exclusively our own handlers from the puncher on disposal
            if (_puncher != null)
            {
                _puncher.OnDataReceived -= _dataReceivedHandler;
                _puncher.OnConnected -= _connectedHandler;
                _puncher.OnDisconnected -= _disconnectedHandler;
                _puncher.OnPeerDisconnected -= _peerDisconnectedHandler;
            }
        }

        private void Log(string msg)
        {
            if (OnLog != null) OnLog(msg);
        }
    }
}
