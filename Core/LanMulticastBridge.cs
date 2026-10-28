using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public delegate void LanGameDetectedHandler(string motd, int originalPort);

    public class LanMulticastBridge : IDisposable
    {
        private Socket _multicastListener;
        private Thread _listenThread;
        private volatile bool _isRunning;
        private UdpClient _broadcaster;

        public event LanGameDetectedHandler OnLanGameDetected;

        public bool IsRunning { get { return _isRunning; } }

        public void StartListening()
        {
            if (_isRunning) return;
            _isRunning = true;

            _listenThread = new Thread(ListenLoop);
            _listenThread.IsBackground = true;
            _listenThread.Name = "LanMulticastListener";
            _listenThread.Start();
        }

        private void ListenLoop()
        {
            try
            {
                _multicastListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _multicastListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                IPEndPoint localEp = new IPEndPoint(IPAddress.Any, MinecraftProtocol.MulticastPort);
                _multicastListener.Bind(localEp);

                MulticastOption mcastOption = new MulticastOption(
                    IPAddress.Parse(MinecraftProtocol.MulticastAddress),
                    IPAddress.Any);
                _multicastListener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption);

                byte[] buffer = new byte[1024];

                while (_isRunning)
                {
                    try
                    {
                        int received = _multicastListener.Receive(buffer);
                        if (received > 0)
                        {
                            string raw = Encoding.UTF8.GetString(buffer, 0, received);
                            string motd;
                            int port;
                            if (MinecraftProtocol.TryParseLanAnnouncement(raw, out motd, out port))
                            {
                                LanGameDetectedHandler handler = OnLanGameDetected;
                                if (handler != null)
                                {
                                    handler(motd, port);
                                }
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        if (!_isRunning) break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Multicast listener error: " + ex.Message);
            }
        }

        /// <summary>
        /// Rebroadcasts a LAN game announcement to the local network and loopback
        /// so Minecraft on this machine sees it in the "LAN Games" list.
        /// </summary>
        public void RebroadcastLocally(string motd, int localProxyPort)
        {
            try
            {
                if (_broadcaster == null)
                {
                    _broadcaster = new UdpClient();
                    _broadcaster.EnableBroadcast = true;
                }

                byte[] data = MinecraftProtocol.BuildLanAnnouncement(motd, localProxyPort);

                // 1. Send to Multicast group 224.0.2.60:4445
                IPEndPoint multicastEp = new IPEndPoint(
                    IPAddress.Parse(MinecraftProtocol.MulticastAddress),
                    MinecraftProtocol.MulticastPort);
                _broadcaster.Send(data, data.Length, multicastEp);

                // 2. Also send to standard broadcast 255.255.255.255:4445
                IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, MinecraftProtocol.MulticastPort);
                _broadcaster.Send(data, data.Length, broadcastEp);

                // 3. Send to localhost
                IPEndPoint loopbackEp = new IPEndPoint(IPAddress.Loopback, MinecraftProtocol.MulticastPort);
                _broadcaster.Send(data, data.Length, loopbackEp);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Rebroadcast error: " + ex.Message);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            if (_multicastListener != null)
            {
                try { _multicastListener.Close(); } catch { }
                _multicastListener = null;
            }
            if (_broadcaster != null)
            {
                try { _broadcaster.Close(); } catch { }
                _broadcaster = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
