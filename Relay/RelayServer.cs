using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MineTunnel.Common;

namespace MineTunnel.Relay
{
    public class RelayRoom
    {
        public string Name { get; set; }
        public string Password { get; set; }
        public TcpClient HostClient { get; set; }
        public NetworkStream HostStream { get; set; }
        public DateTime CreatedAt { get; set; }
        public Dictionary<int, RelayClientSession> Clients { get; set; }

        public RelayRoom()
        {
            CreatedAt = DateTime.UtcNow;
            Clients = new Dictionary<int, RelayClientSession>();
        }
    }

    public class RelayClientSession
    {
        public int Id { get; set; }
        public string PlayerName { get; set; }
        public TcpClient ClientTcp { get; set; }
        public NetworkStream ClientStream { get; set; }
        public RelayRoom Room { get; set; }
    }

    public class RelayServer
    {
        private TcpListener _listener;
        private volatile bool _isRunning;
        private int _port;
        private readonly Dictionary<string, RelayRoom> _rooms = new Dictionary<string, RelayRoom>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private int _nextClientId = 100;

        public bool IsRunning { get { return _isRunning; } }
        public int Port { get { return _port; } }

        public RelayServer(int port = 25560)
        {
            _port = port;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(100);

            Console.WriteLine("[MineTunnel Relay] Listening on 0.0.0.0:{0}", _port);
            Console.WriteLine("[MineTunnel Relay] Ready for zero-port-forwarding connections.\n");

            Thread acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "RelayAcceptLoop";
            acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient tcp = _listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    tcp.ReceiveBufferSize = 1048576;
                    tcp.SendBufferSize = 1048576;

                    Thread t = new Thread(new ParameterizedThreadStart(HandleInitialConnection));
                    t.IsBackground = true;
                    t.Start(tcp);
                }
                catch (SocketException)
                {
                    if (!_isRunning) break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Relay Error] Accept: {0}", ex.Message);
                }
            }
        }

        private void HandleInitialConnection(object state)
        {
            TcpClient tcp = (TcpClient)state;
            try
            {
                NetworkStream stream = tcp.GetStream();
                TunnelPacket packet = TunnelPacket.ReadPacket(stream);
                if (packet == null)
                {
                    tcp.Close();
                    return;
                }

                if (packet.Type == PacketType.RelayRegisterHost)
                {
                    HandleRegisterHost(tcp, stream, packet.Payload);
                }
                else if (packet.Type == PacketType.RelayJoinClient || packet.Type == PacketType.RoomAuthRequest)
                {
                    HandleJoinClient(tcp, stream, packet);
                }
                else
                {
                    tcp.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Relay Error] Handshake: {0}", ex.Message);
                try { tcp.Close(); } catch { }
            }
        }

        private void HandleRegisterHost(TcpClient tcp, NetworkStream stream, byte[] payload)
        {
            string roomName, password;
            TunnelPacket.ParseRelayRegisterHost(payload, out roomName, out password);

            if (string.IsNullOrEmpty(roomName))
            {
                byte[] resp = TunnelPacket.BuildRelayRegisterResponse(false, "Room name cannot be empty.");
                TunnelPacket.WritePacket(stream, PacketType.RelayRegisterHostResponse, resp);
                tcp.Close();
                return;
            }

            RelayRoom room;
            lock (_lock)
            {
                if (_rooms.TryGetValue(roomName, out room))
                {
                    // If room already registered and alive, reject
                    if (room.HostClient != null && room.HostClient.Connected)
                    {
                        byte[] fail = TunnelPacket.BuildRelayRegisterResponse(false, "Room name is already registered and active.");
                        TunnelPacket.WritePacket(stream, PacketType.RelayRegisterHostResponse, fail);
                        tcp.Close();
                        return;
                    }
                }

                room = new RelayRoom
                {
                    Name = roomName,
                    Password = password,
                    HostClient = tcp,
                    HostStream = stream
                };
                _rooms[roomName] = room;
            }

            byte[] okResp = TunnelPacket.BuildRelayRegisterResponse(true, "Room registered successfully.");
            TunnelPacket.WritePacket(stream, PacketType.RelayRegisterHostResponse, okResp);

            Console.WriteLine("[Relay] Host registered Room '{0}' from {1}", roomName, tcp.Client.RemoteEndPoint);

            // Host loop: read packets from host and route to connected clients
            try
            {
                while (_isRunning && tcp.Connected)
                {
                    TunnelPacket p = TunnelPacket.ReadPacket(stream);
                    if (p == null) break;

                    if (p.Type == PacketType.LanBroadcast)
                    {
                        // Broadcast to all clients in this room
                        lock (room.Clients)
                        {
                            foreach (RelayClientSession cs in room.Clients.Values)
                            {
                                try
                                {
                                    lock (cs.ClientStream)
                                    {
                                        TunnelPacket.WritePacket(cs.ClientStream, PacketType.LanBroadcast, p.Payload);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    else if (p.Type == PacketType.ChannelData || p.Type == PacketType.ChannelClose || p.Type == PacketType.Pong)
                    {
                        // Forward to appropriate client
                        lock (room.Clients)
                        {
                            foreach (RelayClientSession cs in room.Clients.Values)
                            {
                                try
                                {
                                    lock (cs.ClientStream)
                                    {
                                        TunnelPacket.WritePacket(cs.ClientStream, p.Type, p.Payload);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                lock (_lock)
                {
                    _rooms.Remove(roomName);
                }
                Console.WriteLine("[Relay] Host disconnected from Room '{0}'", roomName);
                try { tcp.Close(); } catch { }
            }
        }

        private void HandleJoinClient(TcpClient tcp, NetworkStream stream, TunnelPacket initialPacket)
        {
            string roomName, password, playerName;

            if (initialPacket.Type == PacketType.RoomAuthRequest)
            {
                TunnelPacket.ParseAuthRequest(initialPacket.Payload, out roomName, out password, out playerName);
            }
            else
            {
                TunnelPacket.ParseAuthRequest(initialPacket.Payload, out roomName, out password, out playerName);
            }

            RelayRoom room;
            lock (_lock)
            {
                _rooms.TryGetValue(roomName, out room);
            }

            if (room == null || room.HostClient == null || !room.HostClient.Connected)
            {
                byte[] fail = TunnelPacket.BuildAuthResponse(false, 0, "Room does not exist or Host is offline.");
                TunnelPacket.WritePacket(stream, PacketType.RoomAuthResponse, fail);
                tcp.Close();
                return;
            }

            if (!string.Equals(room.Password, password, StringComparison.Ordinal))
            {
                byte[] fail = TunnelPacket.BuildAuthResponse(false, 0, "Invalid Room Password.");
                TunnelPacket.WritePacket(stream, PacketType.RoomAuthResponse, fail);
                tcp.Close();
                return;
            }

            int clientId = Interlocked.Increment(ref _nextClientId);
            byte[] okResp = TunnelPacket.BuildAuthResponse(true, clientId, "");
            TunnelPacket.WritePacket(stream, PacketType.RoomAuthResponse, okResp);

            RelayClientSession session = new RelayClientSession
            {
                Id = clientId,
                PlayerName = playerName,
                ClientTcp = tcp,
                ClientStream = stream,
                Room = room
            };

            lock (room.Clients)
            {
                room.Clients[clientId] = session;
            }

            Console.WriteLine("[Relay] Player '{0}' joined Room '{1}' via Relay ({2})", playerName, roomName, tcp.Client.RemoteEndPoint);

            // Forward incoming client packets directly to host
            try
            {
                while (_isRunning && tcp.Connected && room.HostClient.Connected)
                {
                    TunnelPacket p = TunnelPacket.ReadPacket(stream);
                    if (p == null) break;

                    lock (room.HostStream)
                    {
                        TunnelPacket.WritePacket(room.HostStream, p.Type, p.Payload);
                    }
                }
            }
            catch { }
            finally
            {
                lock (room.Clients)
                {
                    room.Clients.Remove(clientId);
                }
                Console.WriteLine("[Relay] Player '{0}' disconnected from Room '{1}'", playerName, roomName);
                try { tcp.Close(); } catch { }
            }
        }

        public void Stop()
        {
            _isRunning = false;
            if (_listener != null)
            {
                try { _listener.Stop(); } catch { }
                _listener = null;
            }
        }
    }
}
