using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public class ConnectedPeer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string EndPoint { get; set; }
        public long PingMs { get; set; }
        public DateTime ConnectedAt { get; set; }
        internal TcpClient TcpClient { get; set; }
        internal NetworkStream Stream { get; set; }
        internal Dictionary<int, TcpClient> ActiveChannels { get; set; }

        public ConnectedPeer()
        {
            ActiveChannels = new Dictionary<int, TcpClient>();
            ConnectedAt = DateTime.UtcNow;
        }
    }

    public delegate void PeerListChangedHandler(List<ConnectedPeer> peers);
    public delegate void HostLogHandler(string message);

    public class TunnelHost : IDisposable
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _isRunning;
        private int _port;
        private string _roomName;
        private string _password;
        private int _targetMinecraftPort;
        private int _nextClientId = 1;

        private readonly List<ConnectedPeer> _peers = new List<ConnectedPeer>();
        private readonly object _peerLock = new object();

        private LanMulticastBridge _lanBridge;
        private UpnpHelper _upnp;

        public event PeerListChangedHandler OnPeerListChanged;
        public event HostLogHandler OnLog;

        public bool IsRunning { get { return _isRunning; } }
        public int Port { get { return _port; } }
        public string RoomName { get { return _roomName; } }
        public int TargetMinecraftPort { get { return _targetMinecraftPort; } set { _targetMinecraftPort = value; } }

        public TunnelHost() : this(null, null)
        {
        }

        public TunnelHost(LanMulticastBridge lanBridge, UpnpHelper upnp)
        {
            _lanBridge = lanBridge;
            _upnp = upnp;

            if (_lanBridge != null)
            {
                _lanBridge.OnLanGameDetected += HandleLanGameDetected;
            }
        }

        public bool Start(int listenPort, string roomName, string password, int targetMinecraftPort)
        {
            if (_isRunning) Stop();

            _port = listenPort;
            _roomName = roomName;
            _password = password;
            _targetMinecraftPort = targetMinecraftPort;

            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(50);
                _isRunning = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "TunnelHostAccept";
                _acceptThread.Start();

                Log(string.Format("Host listening on port {0} for Room '{1}'", _port, _roomName));
                return true;
            }
            catch (Exception ex)
            {
                Log("Failed to start host: " + ex.Message);
                return false;
            }
        }

        private void AcceptLoop()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 1048576;
                    client.SendBufferSize = 1048576;

                    Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClient));
                    clientThread.IsBackground = true;
                    clientThread.Start(client);
                }
                catch (SocketException)
                {
                    if (!_isRunning) break;
                }
                catch (Exception ex)
                {
                    Log("Accept error: " + ex.Message);
                }
            }
        }

        private void HandleClient(object obj)
        {
            TcpClient tcp = (TcpClient)obj;
            ConnectedPeer peer = null;

            try
            {
                NetworkStream stream = tcp.GetStream();

                // 1. Handshake / Auth
                TunnelPacket authPacket = TunnelPacket.ReadPacket(stream);
                if (authPacket == null || authPacket.Type != PacketType.RoomAuthRequest)
                {
                    tcp.Close();
                    return;
                }

                string clientRoom, clientPass, clientName;
                TunnelPacket.ParseAuthRequest(authPacket.Payload, out clientRoom, out clientPass, out clientName);

                bool authOk = (string.Equals(clientRoom, _roomName, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(clientPass, _password, StringComparison.Ordinal));

                if (!authOk)
                {
                    byte[] failBytes = TunnelPacket.BuildAuthResponse(false, 0, "Invalid Room Name or Password.");
                    TunnelPacket.WritePacket(stream, PacketType.RoomAuthResponse, failBytes);
                    tcp.Close();
                    Log(string.Format("Rejected unauthorized connection from {0}", tcp.Client.RemoteEndPoint));
                    return;
                }

                int clientId = Interlocked.Increment(ref _nextClientId);
                byte[] okBytes = TunnelPacket.BuildAuthResponse(true, clientId, "");
                TunnelPacket.WritePacket(stream, PacketType.RoomAuthResponse, okBytes);

                peer = new ConnectedPeer
                {
                    Id = clientId,
                    Name = clientName,
                    EndPoint = tcp.Client.RemoteEndPoint.ToString(),
                    TcpClient = tcp,
                    Stream = stream
                };

                lock (_peerLock)
                {
                    _peers.Add(peer);
                }
                NotifyPeerListChanged();
                Log(string.Format("Player '{0}' connected ({1})", peer.Name, peer.EndPoint));

                // 2. Main packet dispatch loop
                while (_isRunning && tcp.Connected)
                {
                    TunnelPacket packet = TunnelPacket.ReadPacket(stream);
                    if (packet == null) break; // Client disconnected

                    switch (packet.Type)
                    {
                        case PacketType.ChannelOpen:
                            {
                                int chId = TunnelPacket.ParseChannelId(packet.Payload);
                                OpenMinecraftChannel(peer, chId);
                            }
                            break;

                        case PacketType.ChannelData:
                            {
                                int chId;
                                byte[] data;
                                TunnelPacket.ParseChannelData(packet.Payload, out chId, out data);
                                ForwardToMinecraft(peer, chId, data);
                            }
                            break;

                        case PacketType.ChannelClose:
                            {
                                int chId = TunnelPacket.ParseChannelId(packet.Payload);
                                CloseMinecraftChannel(peer, chId);
                            }
                            break;

                        case PacketType.Ping:
                            {
                                long timestamp = TunnelPacket.ParsePing(packet.Payload);
                                byte[] pong = TunnelPacket.BuildPing(timestamp);
                                lock (peer.Stream)
                                {
                                    TunnelPacket.WritePacket(peer.Stream, PacketType.Pong, pong);
                                }
                            }
                            break;

                        case PacketType.Pong:
                            {
                                long orig = TunnelPacket.ParsePing(packet.Payload);
                                peer.PingMs = (long)(DateTime.UtcNow - new DateTime(orig, DateTimeKind.Utc)).TotalMilliseconds;
                                NotifyPeerListChanged();
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Peer communication error: " + ex.Message);
            }
            finally
            {
                if (peer != null)
                {
                    lock (_peerLock)
                    {
                        _peers.Remove(peer);
                    }
                    // Close all active proxy channels for this peer
                    lock (peer.ActiveChannels)
                    {
                        foreach (TcpClient mcClient in peer.ActiveChannels.Values)
                        {
                            try { mcClient.Close(); } catch { }
                        }
                        peer.ActiveChannels.Clear();
                    }
                    NotifyPeerListChanged();
                    Log(string.Format("Player '{0}' disconnected.", peer.Name));
                }

                try { tcp.Close(); } catch { }
            }
        }

        private void OpenMinecraftChannel(ConnectedPeer peer, int channelId)
        {
            try
            {
                TcpClient mcClient = new TcpClient();
                mcClient.NoDelay = true;
                mcClient.ReceiveBufferSize = 1048576;
                mcClient.SendBufferSize = 1048576;
                mcClient.Connect("127.0.0.1", _targetMinecraftPort);

                lock (peer.ActiveChannels)
                {
                    peer.ActiveChannels[channelId] = mcClient;
                }

                // Start reader thread from local Minecraft to tunnel
                Thread readerThread = new Thread(new ParameterizedThreadStart(delegate (object state)
                {
                    int chId = (int)state;
                    byte[] buffer = new byte[65536];
                    NetworkStream mcStream = mcClient.GetStream();

                    try
                    {
                        while (_isRunning && mcClient.Connected && peer.TcpClient.Connected)
                        {
                            int r = mcStream.Read(buffer, 0, buffer.Length);
                            if (r <= 0) break;

                            byte[] chPacket = TunnelPacket.BuildChannelData(chId, buffer, 0, r);
                            lock (peer.Stream)
                            {
                                TunnelPacket.WritePacket(peer.Stream, PacketType.ChannelData, chPacket);
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        CloseMinecraftChannel(peer, chId);
                        try
                        {
                            byte[] closeBytes = TunnelPacket.BuildChannelOpen(chId);
                            lock (peer.Stream)
                            {
                                TunnelPacket.WritePacket(peer.Stream, PacketType.ChannelClose, closeBytes);
                            }
                        }
                        catch { }
                    }
                }));

                readerThread.IsBackground = true;
                readerThread.Start(channelId);
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to connect channel #{0} to local Minecraft ({1}): {2}",
                    channelId, _targetMinecraftPort, ex.Message));
                try
                {
                    byte[] closeBytes = TunnelPacket.BuildChannelOpen(channelId);
                    lock (peer.Stream)
                    {
                        TunnelPacket.WritePacket(peer.Stream, PacketType.ChannelClose, closeBytes);
                    }
                }
                catch { }
            }
        }

        private void ForwardToMinecraft(ConnectedPeer peer, int channelId, byte[] data)
        {
            TcpClient mcClient;
            lock (peer.ActiveChannels)
            {
                if (!peer.ActiveChannels.TryGetValue(channelId, out mcClient)) return;
            }

            try
            {
                if (mcClient != null && mcClient.Connected)
                {
                    NetworkStream mcStream = mcClient.GetStream();
                    mcStream.Write(data, 0, data.Length);
                }
            }
            catch
            {
                CloseMinecraftChannel(peer, channelId);
            }
        }

        private void CloseMinecraftChannel(ConnectedPeer peer, int channelId)
        {
            lock (peer.ActiveChannels)
            {
                TcpClient mcClient;
                if (peer.ActiveChannels.TryGetValue(channelId, out mcClient))
                {
                    try { mcClient.Close(); } catch { }
                    peer.ActiveChannels.Remove(channelId);
                }
            }
        }

        private void HandleLanGameDetected(string motd, int originalPort)
        {
            _targetMinecraftPort = originalPort;
            BroadcastLanAnnouncement(motd, _port);
        }

        public void BroadcastLanAnnouncement(string motd, int publicPort)
        {
            byte[] packetData = TunnelPacket.BuildLanBroadcast(motd, publicPort);
            lock (_peerLock)
            {
                foreach (ConnectedPeer peer in _peers)
                {
                    try
                    {
                        lock (peer.Stream)
                        {
                            TunnelPacket.WritePacket(peer.Stream, PacketType.LanBroadcast, packetData);
                        }
                    }
                    catch { }
                }
            }
        }

        private void NotifyPeerListChanged()
        {
            PeerListChangedHandler handler = OnPeerListChanged;
            if (handler != null)
            {
                List<ConnectedPeer> copy;
                lock (_peerLock)
                {
                    copy = new List<ConnectedPeer>(_peers);
                }
                handler(copy);
            }
        }

        private void Log(string message)
        {
            HostLogHandler handler = OnLog;
            if (handler != null) handler(message);
        }

        public void Stop()
        {
            _isRunning = false;

            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                _listener = null;
            }

            lock (_peerLock)
            {
                foreach (ConnectedPeer peer in _peers)
                {
                    try { peer.TcpClient.Close(); } catch { }
                }
                _peers.Clear();
            }

            NotifyPeerListChanged();
            Log("Host stopped.");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
