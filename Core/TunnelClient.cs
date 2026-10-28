using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public delegate void ClientLogHandler(string message);
    public delegate void ClientPingHandler(long pingMs);
    public delegate void ClientStatusHandler(bool isConnected, string statusText);

    public class TunnelClient : IDisposable
    {
        private TcpClient _tunnelTcp;
        private NetworkStream _tunnelStream;
        private Thread _readThread;
        private Thread _pingThread;
        private Thread _localListenerThread;
        private TcpListener _localListener;
        private volatile bool _isConnected;
        private int _localProxyPort = MinecraftProtocol.DefaultServerPort;
        private int _nextChannelId = 1;

        private readonly Dictionary<int, TcpClient> _channels = new Dictionary<int, TcpClient>();
        private readonly object _channelLock = new object();

        private LanMulticastBridge _lanBridge;

        public event ClientLogHandler OnLog;
        public event ClientPingHandler OnPingUpdated;
        public event ClientStatusHandler OnStatusChanged;
        public event LanGameDetectedHandler OnLanGameReceived;

        public bool IsConnected { get { return _isConnected; } }
        public int LocalProxyPort { get { return _localProxyPort; } }

        public TunnelClient() : this(null)
        {
        }

        public TunnelClient(LanMulticastBridge lanBridge)
        {
            _lanBridge = lanBridge;
        }

        public bool Connect(string host, int port, string roomName, string password, string playerName)
        {
            if (_isConnected) Disconnect();

            try
            {
                Log(string.Format("Connecting to {0}:{1}...", host, port));
                _tunnelTcp = new TcpClient();
                _tunnelTcp.NoDelay = true;
                _tunnelTcp.ReceiveBufferSize = 1048576;
                _tunnelTcp.SendBufferSize = 1048576;

                IAsyncResult ar = _tunnelTcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000))
                {
                    Log("Connection timed out.");
                    return false;
                }
                _tunnelTcp.EndConnect(ar);

                _tunnelStream = _tunnelTcp.GetStream();

                // Send Auth Request
                byte[] authPayload = TunnelPacket.BuildAuthRequest(roomName, password, playerName);
                TunnelPacket.WritePacket(_tunnelStream, PacketType.RoomAuthRequest, authPayload);

                // Read Auth Response
                TunnelPacket authResponse = TunnelPacket.ReadPacket(_tunnelStream);
                if (authResponse == null || authResponse.Type != PacketType.RoomAuthResponse)
                {
                    Log("Invalid authentication response.");
                    Disconnect();
                    return false;
                }

                bool success;
                int clientId;
                string errorMsg;
                TunnelPacket.ParseAuthResponse(authResponse.Payload, out success, out clientId, out errorMsg);

                if (!success)
                {
                    Log("Authentication failed: " + errorMsg);
                    Disconnect();
                    return false;
                }

                _isConnected = true;
                Log(string.Format("Successfully joined Room '{0}' as Client #{1}!", roomName, clientId));

                // Start Local Minecraft Proxy Listener
                StartLocalProxyListener();

                // Start Reader Thread
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Name = "TunnelClientRead";
                _readThread.Start();

                // Start Ping / Keepalive Thread
                _pingThread = new Thread(PingLoop);
                _pingThread.IsBackground = true;
                _pingThread.Name = "TunnelClientPing";
                _pingThread.Start();

                NotifyStatus(true, string.Format("Connected to '{0}'", roomName));
                return true;
            }
            catch (Exception ex)
            {
                Log("Connection error: " + ex.Message);
                Disconnect();
                return false;
            }
        }

        private void StartLocalProxyListener()
        {
            int preferredPort = MinecraftProtocol.DefaultServerPort;
            bool bound = false;

            for (int p = preferredPort; p < preferredPort + 10; p++)
            {
                try
                {
                    _localListener = new TcpListener(IPAddress.Loopback, p);
                    _localListener.Start();
                    _localProxyPort = p;
                    bound = true;
                    Log(string.Format("Local Minecraft proxy listening on 127.0.0.1:{0}", _localProxyPort));
                    break;
                }
                catch
                {
                    // Port might be in use, try next
                }
            }

            if (!bound)
            {
                _localListener = new TcpListener(IPAddress.Loopback, 0);
                _localListener.Start();
                _localProxyPort = ((IPEndPoint)_localListener.LocalEndpoint).Port;
                Log(string.Format("Local Minecraft proxy listening on dynamic port 127.0.0.1:{0}", _localProxyPort));
            }

            _localListenerThread = new Thread(LocalAcceptLoop);
            _localListenerThread.IsBackground = true;
            _localListenerThread.Name = "LocalProxyAccept";
            _localListenerThread.Start();
        }

        private void LocalAcceptLoop()
        {
            while (_isConnected && _localListener != null)
            {
                try
                {
                    TcpClient mcClient = _localListener.AcceptTcpClient();
                    mcClient.NoDelay = true;
                    mcClient.ReceiveBufferSize = 1048576;
                    mcClient.SendBufferSize = 1048576;

                    int channelId = Interlocked.Increment(ref _nextChannelId);
                    lock (_channelLock)
                    {
                        _channels[channelId] = mcClient;
                    }

                    // Notify host to open channel to host's Minecraft server
                    byte[] openPayload = TunnelPacket.BuildChannelOpen(channelId);
                    lock (_tunnelStream)
                    {
                        TunnelPacket.WritePacket(_tunnelStream, PacketType.ChannelOpen, openPayload);
                    }

                    // Start forwarding thread from local Minecraft to tunnel
                    Thread fwdThread = new Thread(new ParameterizedThreadStart(delegate (object state)
                    {
                        int chId = (int)state;
                        byte[] buffer = new byte[65536];
                        NetworkStream mcStream = mcClient.GetStream();

                        try
                        {
                            while (_isConnected && mcClient.Connected && _tunnelTcp.Connected)
                            {
                                int r = mcStream.Read(buffer, 0, buffer.Length);
                                if (r <= 0) break;

                                byte[] chPacket = TunnelPacket.BuildChannelData(chId, buffer, 0, r);
                                lock (_tunnelStream)
                                {
                                    TunnelPacket.WritePacket(_tunnelStream, PacketType.ChannelData, chPacket);
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            CloseChannel(chId);
                            try
                            {
                                byte[] closeBytes = TunnelPacket.BuildChannelOpen(chId);
                                lock (_tunnelStream)
                                {
                                    TunnelPacket.WritePacket(_tunnelStream, PacketType.ChannelClose, closeBytes);
                                }
                            }
                            catch { }
                        }
                    }));

                    fwdThread.IsBackground = true;
                    fwdThread.Start(channelId);
                }
                catch (SocketException)
                {
                    if (!_isConnected) break;
                }
                catch (Exception ex)
                {
                    Log("Local proxy error: " + ex.Message);
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (_isConnected && _tunnelTcp != null && _tunnelTcp.Connected)
                {
                    TunnelPacket packet = TunnelPacket.ReadPacket(_tunnelStream);
                    if (packet == null) break; // Disconnected

                    switch (packet.Type)
                    {
                        case PacketType.ChannelData:
                            {
                                int chId;
                                byte[] data;
                                TunnelPacket.ParseChannelData(packet.Payload, out chId, out data);
                                ForwardToLocalMinecraft(chId, data);
                            }
                            break;

                        case PacketType.ChannelClose:
                            {
                                int chId = TunnelPacket.ParseChannelId(packet.Payload);
                                CloseChannel(chId);
                            }
                            break;

                        case PacketType.LanBroadcast:
                            {
                                string motd;
                                int remotePort;
                                TunnelPacket.ParseLanBroadcast(packet.Payload, out motd, out remotePort);
                                Log(string.Format("Received LAN game from host: '{0}'", motd));

                                // Rebroadcast to local Minecraft client with our local proxy port!
                                if (_lanBridge != null)
                                {
                                    _lanBridge.RebroadcastLocally(motd, _localProxyPort);
                                }

                                LanGameDetectedHandler lanHandler = OnLanGameReceived;
                                if (lanHandler != null)
                                {
                                    lanHandler(motd, _localProxyPort);
                                }
                            }
                            break;

                        case PacketType.Pong:
                            {
                                long orig = TunnelPacket.ParsePing(packet.Payload);
                                long ping = (long)(DateTime.UtcNow - new DateTime(orig, DateTimeKind.Utc)).TotalMilliseconds;
                                ClientPingHandler pingHandler = OnPingUpdated;
                                if (pingHandler != null) pingHandler(ping);
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Tunnel read error: " + ex.Message);
            }
            finally
            {
                Disconnect();
            }
        }

        private void ForwardToLocalMinecraft(int channelId, byte[] data)
        {
            TcpClient mcClient;
            lock (_channelLock)
            {
                if (!_channels.TryGetValue(channelId, out mcClient)) return;
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
                CloseChannel(channelId);
            }
        }

        private void CloseChannel(int channelId)
        {
            lock (_channelLock)
            {
                TcpClient mcClient;
                if (_channels.TryGetValue(channelId, out mcClient))
                {
                    try { mcClient.Close(); } catch { }
                    _channels.Remove(channelId);
                }
            }
        }

        private void PingLoop()
        {
            while (_isConnected)
            {
                try
                {
                    byte[] pingPayload = TunnelPacket.BuildPing(DateTime.UtcNow.Ticks);
                    lock (_tunnelStream)
                    {
                        TunnelPacket.WritePacket(_tunnelStream, PacketType.Ping, pingPayload);
                    }
                }
                catch { }

                // Ping every 3 seconds
                for (int i = 0; i < 30 && _isConnected; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void Log(string message)
        {
            ClientLogHandler handler = OnLog;
            if (handler != null) handler(message);
        }

        private void NotifyStatus(bool connected, string text)
        {
            ClientStatusHandler handler = OnStatusChanged;
            if (handler != null) handler(connected, text);
        }

        public void Disconnect()
        {
            if (!_isConnected && _tunnelTcp == null) return;
            _isConnected = false;

            if (_localListener != null)
            {
                try { _localListener.Stop(); } catch { }
                _localListener = null;
            }

            lock (_channelLock)
            {
                foreach (TcpClient mcClient in _channels.Values)
                {
                    try { mcClient.Close(); } catch { }
                }
                _channels.Clear();
            }

            if (_tunnelTcp != null)
            {
                try { _tunnelTcp.Close(); } catch { }
                _tunnelTcp = null;
            }

            NotifyStatus(false, "Disconnected");
            Log("Disconnected from room.");
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
