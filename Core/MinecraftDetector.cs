using System;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Core
{
    public delegate void ServerStatusHandler(MinecraftServerInfo info, int port);

    public class MinecraftDetector : IDisposable
    {
        private Thread _pollThread;
        private volatile bool _isRunning;
        private int _activePort = MinecraftProtocol.DefaultServerPort;
        private LanMulticastBridge _lanBridge;

        public event ServerStatusHandler OnServerStatusUpdated;

        public int ActivePort { get { return _activePort; } set { _activePort = value; } }

        public MinecraftDetector(LanMulticastBridge lanBridge)
        {
            _lanBridge = lanBridge;
            if (_lanBridge != null)
            {
                _lanBridge.OnLanGameDetected += HandleLanGameDetected;
            }
        }

        private void HandleLanGameDetected(string motd, int originalPort)
        {
            _activePort = originalPort;
            CheckServerStatus();
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _pollThread = new Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Name = "MinecraftDetectorPoll";
            _pollThread.Start();
        }

        private void PollLoop()
        {
            while (_isRunning)
            {
                CheckServerStatus();
                // Wait 2.5 seconds before next poll
                for (int i = 0; i < 25 && _isRunning; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        public void CheckServerStatus()
        {
            try
            {
                // 1. First test currently selected active port
                MinecraftServerInfo info = MinecraftProtocol.PingServer("127.0.0.1", _activePort, 800);

                // 2. If not online, scan all common Java Minecraft ports
                if (!info.IsOnline)
                {
                    foreach (int candidatePort in MinecraftProtocol.CommonJavaPorts)
                    {
                        if (candidatePort == _activePort) continue;
                        MinecraftServerInfo candInfo = MinecraftProtocol.PingServer("127.0.0.1", candidatePort, 600);
                        if (candInfo.IsOnline)
                        {
                            _activePort = candidatePort;
                            info = candInfo;
                            break;
                        }
                    }
                }

                ServerStatusHandler handler = OnServerStatusUpdated;
                if (handler != null)
                {
                    handler(info, _activePort);
                }
            }
            catch
            {
                // Ignore transient network errors
            }
        }

        public void Stop()
        {
            _isRunning = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
