using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public class RelayHostConnector : IDisposable
    {
        private TcpClient _relayTcp;
        private NetworkStream _relayStream;
        private Thread _readThread;
        private volatile bool _isConnected;
        private string _relayHost;
        private int _relayPort;
        private string _roomName;
        private string _password;
        private int _targetMinecraftPort;

        private readonly Dictionary<int, TcpClient> _channels = new Dictionary<int, TcpClient>();
        private readonly object _channelLock = new object();

        private LanMulticastBridge _lanBridge;

        public event HostLogHandler OnLog;
        public event ClientStatusHandler OnStatusChanged;

        public bool IsConnected { get { return _isConnected; } }
        public string RelayAddress { get { return string.Format("{0}:{1}", _relayHost, _relayPort); } }
        public int TargetMinecraftPort { get { return _targetMinecraftPort; } set { _targetMinecraftPort = value; } }

        public RelayHostConnector() : this(null)
        {
        }

        public RelayHostConnector(LanMulticastBridge lanBridge)
        {
            _lanBridge = lanBridge;
            if (_lanBridge != null)
            {
                _lanBridge.OnLanGameDetected += HandleLanGameDetected;
            }
        }

        public bool ConnectAndRegister(string relayHost, int relayPort, string roomName, string password, int targetMinecraftPort)
        {
            if (_isConnected) Disconnect();

            _relayHost = relayHost;
            _relayPort = relayPort;
            _roomName = roomName;
            _password = password;
            _targetMinecraftPort = targetMinecraftPort;

            try
            {
                Log(string.Format("Connecting outbound to Relay {0}:{1}...", relayHost, relayPort));
                _relayTcp = new TcpClient();
                _relayTcp.NoDelay = true;
                _relayTcp.ReceiveBufferSize = 1048576;
                _relayTcp.SendBufferSize = 1048576;

                IAsyncResult ar = _relayTcp.BeginConnect(relayHost, relayPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000))
                {
                    Log("Relay connection timed out.");
                    return false;
                }
                _relayTcp.EndConnect(ar);
                _relayStream = _relayTcp.GetStream();

                // Send Relay Register Host Request
                byte[] regPayload = TunnelPacket.BuildRelayRegisterHost(roomName, password);
                TunnelPacket.WritePacket(_relayStream, PacketType.RelayRegisterHost, regPayload);

                // Read Response
                TunnelPacket resp = TunnelPacket.ReadPacket(_relayStream);
                if (resp == null || resp.Type != PacketType.RelayRegisterHostResponse)
                {
                    Log("Invalid relay registration response.");
                    Disconnect();
                    return false;
                }

                bool success;
                string message;
                TunnelPacket.ParseRelayRegisterResponse(resp.Payload, out success, out message);

                if (!success)
                {
                    Log("Relay registration rejected: " + message);
                    Disconnect();
                    return false;
                }

                _isConnected = true;
                Log(string.Format("Registered Room '{0}' on Relay! (No Port Forwarding Required)", roomName));

                // Start reader thread from relay to host
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Name = "RelayHostReader";
                _readThread.Start();

                NotifyStatus(true, string.Format("Hosting on Relay '{0}' (CGNAT Bypassed)", roomName));
                return true;
            }
            catch (Exception ex)
            {
                Log("Relay connection error: " + ex.Message);
                Disconnect();
                return false;
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (_isConnected && _relayTcp != null && _relayTcp.Connected)
                {
                    TunnelPacket packet = TunnelPacket.ReadPacket(_relayStream);
                    if (packet == null) break;

                    switch (packet.Type)
                    {
                        case PacketType.ChannelOpen:
                            {
                                int chId = TunnelPacket.ParseChannelId(packet.Payload);
                                OpenLocalMinecraftChannel(chId);
                            }
                            break;

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
                                CloseLocalMinecraftChannel(chId);
                            }
                            break;

                        case PacketType.Ping:
                            {
                                long timestamp = TunnelPacket.ParsePing(packet.Payload);
                                byte[] pong = TunnelPacket.BuildPing(timestamp);
                                lock (_relayStream)
                                {
                                    TunnelPacket.WritePacket(_relayStream, PacketType.Pong, pong);
                                }
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Relay stream error: " + ex.Message);
            }
            finally
            {
                Disconnect();
            }
        }

        private void OpenLocalMinecraftChannel(int channelId)
        {
            try
            {
                TcpClient mcClient = new TcpClient();
                mcClient.NoDelay = true;
                mcClient.ReceiveBufferSize = 1048576;
                mcClient.SendBufferSize = 1048576;
                mcClient.Connect("127.0.0.1", _targetMinecraftPort);

                lock (_channelLock)
                {
                    _channels[channelId] = mcClient;
                }

                // Forward data from local Minecraft back through the relay
                Thread fwd = new Thread(new ParameterizedThreadStart(delegate (object state)
                {
                    int chId = (int)state;
                    byte[] buffer = new byte[65536];
                    NetworkStream mcStream = mcClient.GetStream();

                    try
                    {
                        while (_isConnected && mcClient.Connected && _relayTcp.Connected)
                        {
                            int r = mcStream.Read(buffer, 0, buffer.Length);
                            if (r <= 0) break;

                            byte[] chPacket = TunnelPacket.BuildChannelData(chId, buffer, 0, r);
                            lock (_relayStream)
                            {
                                TunnelPacket.WritePacket(_relayStream, PacketType.ChannelData, chPacket);
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        CloseLocalMinecraftChannel(chId);
                        try
                        {
                            byte[] closeBytes = TunnelPacket.BuildChannelOpen(chId);
                            lock (_relayStream)
                            {
                                TunnelPacket.WritePacket(_relayStream, PacketType.ChannelClose, closeBytes);
                            }
                        }
                        catch { }
                    }
                }));

                fwd.IsBackground = true;
                fwd.Start(channelId);
            }
            catch (Exception ex)
            {
                Log(string.Format("Failed to open local channel #{0} to Minecraft ({1}): {2}",
                    channelId, _targetMinecraftPort, ex.Message));
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
                CloseLocalMinecraftChannel(channelId);
            }
        }

        private void CloseLocalMinecraftChannel(int channelId)
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

        private void HandleLanGameDetected(string motd, int originalPort)
        {
            _targetMinecraftPort = originalPort;
            if (_isConnected && _relayStream != null)
            {
                try
                {
                    byte[] lanPacket = TunnelPacket.BuildLanBroadcast(motd, _relayPort);
                    lock (_relayStream)
                    {
                        TunnelPacket.WritePacket(_relayStream, PacketType.LanBroadcast, lanPacket);
                    }
                }
                catch { }
            }
        }

        private void Log(string message)
        {
            HostLogHandler handler = OnLog;
            if (handler != null) handler(message);
        }

        private void NotifyStatus(bool isConnected, string statusText)
        {
            ClientStatusHandler handler = OnStatusChanged;
            if (handler != null) handler(isConnected, statusText);
        }

        public void Disconnect()
        {
            if (!_isConnected && _relayTcp == null) return;
            _isConnected = false;

            lock (_channelLock)
            {
                foreach (TcpClient mc in _channels.Values)
                {
                    try { mc.Close(); } catch { }
                }
                _channels.Clear();
            }

            if (_relayTcp != null)
            {
                try { _relayTcp.Close(); } catch { }
                _relayTcp = null;
            }

            NotifyStatus(false, "Disconnected from Relay.");
            Log("Disconnected from Relay.");
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
