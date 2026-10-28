using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MineTunnel.Core
{
    public class AuthenticatedPeerSession
    {
        public string SessionIdHex { get; set; }
        public byte[] SessionIdBytes { get; set; }
        public byte[] SharedSecret { get; set; }
        public byte[] EncKey { get; set; }
        public byte[] MacKey { get; set; }
        public IPEndPoint RemoteEndpoint { get; set; }
        public bool IsMutualAuthenticated { get; set; }
        public byte[] ClientNonce { get; set; }
        public byte[] HostNonce { get; set; }
        public string PeerName { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    public class PingProbe
    {
        public long SendTicks { get; set; }
        public IPEndPoint RemotePeer { get; set; }
    }

    public class P2PHolePuncher : IDisposable
    {
        public static readonly byte[] MagicHeader = new byte[] { 0x50, 0x32, 0x50, 0x48 }; // "P2PH"

        public const byte TypeHelloReq = 0x01;
        public const byte TypeHelloResp = 0x02;
        public const byte TypeAuthConfirm = 0x05;
        public const byte TypePing = 0x03;
        public const byte TypePong = 0x04;
        public const byte TypeData = 0x10;

        private UdpClient _udp;
        private IPEndPoint _localEndpoint;
        private IPEndPoint _publicEndpoint;
        private IPEndPoint _targetPeerEndpoint;
        private string _clientPlayerName = "Player";
        private volatile bool _isRunning;
        private volatile bool _isConnected;
        private Thread _recvThread;
        private Thread _punchThread;
        private long _currentLatencyMs;
        private readonly object _lock = new object();

        // Monotonic RTT tracking with peer probe mapping
        private long _pingIdCounter = 0;
        private readonly Dictionary<long, PingProbe> _pendingPings = new Dictionary<long, PingProbe>();

        // Replay Protection: nonces seen within active window (Hex -> ExpiryUtc)
        private readonly Dictionary<string, DateTime> _seenNonces = new Dictionary<string, DateTime>();

        // Authenticated sessions table: RemoteEndpoint.ToString() -> AuthenticatedPeerSession
        private readonly Dictionary<string, AuthenticatedPeerSession> _sessionsByEndpoint = new Dictionary<string, AuthenticatedPeerSession>();
        private AuthenticatedPeerSession _currentSession;

        // Unified Event Dispatch & Peer Tracking
        public event Action<string> OnLog;
        public event Action<IPEndPoint, long> OnConnected;
        public event Action<IPEndPoint, string, long> OnPeerConnected;
        public event Action<IPEndPoint, string> OnPeerDisconnected;
        public event Action<long> OnPingUpdated;
        public event Action<IPEndPoint, long> OnPeerPingUpdated;
        public event Action OnDisconnected;
        public event Action<IPEndPoint, byte[]> OnDataReceived;

        public bool IsConnected { get { return _isConnected; } }
        public long CurrentLatencyMs { get { return _currentLatencyMs; } }
        public IPEndPoint PublicEndpoint { get { return _publicEndpoint; } }
        public IPEndPoint LocalEndpoint { get { return _localEndpoint; } }
        public IPEndPoint TargetPeerEndpoint { get { return _targetPeerEndpoint; } }
        public UdpClient Socket { get { return _udp; } }
        public AuthenticatedPeerSession CurrentSession { get { return _currentSession; } }

        public P2PHolePuncher()
        {
            _currentLatencyMs = -1;
        }

        public bool InitializeSocket(int bindPort = 0)
        {
            lock (_lock)
            {
                Stop();

                try
                {
                    _udp = new UdpClient(bindPort);
                    _udp.Client.ReceiveBufferSize = 256 * 1024;
                    _udp.Client.SendBufferSize = 256 * 1024;
                    _localEndpoint = (IPEndPoint)_udp.Client.LocalEndPoint;

                    // Discover public endpoint via hardened STUN
                    Log("Querying STUN to discover public endpoint...");
                    P2PStunResult stun = StunClient.Query(_udp, 3000);
                    if (stun.Success)
                    {
                        _publicEndpoint = stun.PublicEndpoint;
                        Log("Public P2P Endpoint: " + _publicEndpoint.ToString() + " (via " + stun.ServerUsed + ")");
                        return true;
                    }
                    else
                    {
                        Log("STUN Warning: " + stun.ErrorMessage + ". Using local fallback.");
                        _publicEndpoint = _localEndpoint;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log("Failed to bind UDP socket: " + ex.Message);
                    return false;
                }
            }
        }

        // Host creates invite code containing public endpoint, room name, session ID and shared secret
        public string GenerateInviteCode(string roomName)
        {
            if (_publicEndpoint == null) return "";
            string cleanRoom = string.IsNullOrEmpty(roomName) ? "World" : roomName.Trim();

            lock (_lock)
            {
                if (_currentSession == null)
                {
                    byte[] sessId = CryptoSecurity.GenerateRandomBytes(16);
                    byte[] secret = CryptoSecurity.GenerateRandomBytes(16);
                    byte[] encKey, macKey;
                    CryptoSecurity.DeriveKeys(secret, out encKey, out macKey);

                    _currentSession = new AuthenticatedPeerSession
                    {
                        SessionIdBytes = sessId,
                        SessionIdHex = ToHex(sessId),
                        SharedSecret = secret,
                        EncKey = encKey,
                        MacKey = macKey,
                        IsMutualAuthenticated = false
                    };
                }

                // Format: MINE-P2P#IP:PORT#ROOM#TOKEN
                // Token = 32 hex chars (16B SessionId) + 32 hex chars (16B Secret) = 64 hex chars
                string token = _currentSession.SessionIdHex + ToHex(_currentSession.SharedSecret);
                return string.Format("MINE-P2P#{0}:{1}#{2}#{3}", _publicEndpoint.Address, _publicEndpoint.Port, cleanRoom, token);
            }
        }

        public static bool TryParseInviteCode(string code, out IPEndPoint endpoint, out string roomName, out byte[] sessionId, out byte[] sharedSecret)
        {
            endpoint = null;
            roomName = "";
            sessionId = null;
            sharedSecret = null;

            if (string.IsNullOrEmpty(code)) return false;
            string clean = code.Trim();

            if (!clean.StartsWith("MINE-P2P#", StringComparison.OrdinalIgnoreCase)) return false;

            string[] parts = clean.Split('#');
            if (parts.Length < 4) return false;

            string rawAddr = parts[1];
            roomName = parts[2];
            string token = parts[3];

            if (!rawAddr.Contains(":")) return false;
            string[] addrParts = rawAddr.Split(':');

            IPAddress ip;
            int port;
            if (!IPAddress.TryParse(addrParts[0], out ip) || !int.TryParse(addrParts[1], out port))
            {
                return false;
            }

            endpoint = new IPEndPoint(ip, port);

            // Token must be exactly 64 hex characters (32B total: 16B SessionId + 16B Secret)
            if (token.Length != 64) return false;

            try
            {
                sessionId = FromHex(token.Substring(0, 32));
                sharedSecret = FromHex(token.Substring(32, 32));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseInviteCode(string code, out IPEndPoint endpoint, out string roomName)
        {
            byte[] sId, sSec;
            return TryParseInviteCode(code, out endpoint, out roomName, out sId, out sSec);
        }

        // Host starts listening for peer punches
        public void StartHostListening()
        {
            lock (_lock)
            {
                _isRunning = true;
                _isConnected = false;

                if (_recvThread == null || !_recvThread.IsAlive)
                {
                    _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "P2P_Recv" };
                    _recvThread.Start();
                }

                if (_punchThread == null || !_punchThread.IsAlive)
                {
                    _punchThread = new Thread(HostKeepaliveLoop) { IsBackground = true, Name = "P2P_HostKeepalive" };
                    _punchThread.Start();
                }

                Log("P2P Host listening for incoming peer punches on " + _localEndpoint.ToString());
            }
        }

        // Client initiates hole punching towards Host
        public void StartHolePunching(IPEndPoint peerEndpoint, byte[] sessionId, byte[] sharedSecret, string playerName)
        {
            lock (_lock)
            {
                _targetPeerEndpoint = peerEndpoint;
                _clientPlayerName = string.IsNullOrEmpty(playerName) ? "Player" : playerName.Trim();
                _isRunning = true;
                _isConnected = false;

                byte[] encKey, macKey;
                CryptoSecurity.DeriveKeys(sharedSecret, out encKey, out macKey);

                _currentSession = new AuthenticatedPeerSession
                {
                    SessionIdBytes = sessionId,
                    SessionIdHex = ToHex(sessionId),
                    SharedSecret = sharedSecret,
                    EncKey = encKey,
                    MacKey = macKey,
                    RemoteEndpoint = peerEndpoint,
                    ClientNonce = CryptoSecurity.GenerateRandomBytes(16),
                    PeerName = _clientPlayerName,
                    LastSeenUtc = DateTime.UtcNow,
                    IsMutualAuthenticated = false
                };

                _sessionsByEndpoint[peerEndpoint.ToString()] = _currentSession;

                if (_recvThread == null || !_recvThread.IsAlive)
                {
                    _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "P2P_Recv" };
                    _recvThread.Start();
                }

                if (_punchThread == null || !_punchThread.IsAlive)
                {
                    _punchThread = new Thread(ClientPunchLoop) { IsBackground = true, Name = "P2P_ClientPunch" };
                    _punchThread.Start();
                }

                Log("Starting P2P punch & mutual authentication towards " + peerEndpoint.ToString() + " (" + _clientPlayerName + ")...");
            }
        }

        public void StartHolePunching(IPEndPoint peerEndpoint, byte[] sessionId, byte[] sharedSecret)
        {
            StartHolePunching(peerEndpoint, sessionId, sharedSecret, "Player");
        }

        // Client punch loop: sends TypeHelloReq repeatedly until mutually authenticated
        private void ClientPunchLoop()
        {
            int attempts = 0;

            while (_isRunning)
            {
                try
                {
                    if (!_isConnected && _currentSession != null)
                    {
                        attempts++;
                        SendHelloReq(_targetPeerEndpoint, _currentSession);

                        if (attempts % 10 == 0)
                        {
                            Log(string.Format("Punching towards host... attempt {0} -> {1}", attempts, _targetPeerEndpoint));
                        }

                        Thread.Sleep(100);
                    }
                    else if (_isConnected && _targetPeerEndpoint != null)
                    {
                        // Check client inactivity timeout from host (>15s)
                        if (_currentSession != null && (DateTime.UtcNow - _currentSession.LastSeenUtc).TotalSeconds > 15)
                        {
                            Log("Host inactivity timeout (>15s). Connection lost.");
                            _isConnected = false;
                            if (OnDisconnected != null) OnDisconnected();
                            break;
                        }

                        // Once mutually authenticated, send PING every 2 seconds with monotonic timestamp
                        SendMonotonicPing(_targetPeerEndpoint);
                        CleanExpiredPings();
                        Thread.Sleep(2000);
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Log("Punch error: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        // Host keepalive loop: sends keepalives and monotonic pings to all authenticated peers
        private void HostKeepaliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(2000);
                    if (!_isRunning) break;

                    List<IPEndPoint> targetPeers = new List<IPEndPoint>();
                    List<IPEndPoint> timedOutPeers = new List<IPEndPoint>();
                    DateTime now = DateTime.UtcNow;

                    lock (_lock)
                    {
                        foreach (AuthenticatedPeerSession s in _sessionsByEndpoint.Values)
                        {
                            if (s.IsMutualAuthenticated && s.RemoteEndpoint != null)
                            {
                                if ((now - s.LastSeenUtc).TotalSeconds > 15)
                                {
                                    timedOutPeers.Add(s.RemoteEndpoint);
                                }
                                else
                                {
                                    targetPeers.Add(s.RemoteEndpoint);
                                }
                            }
                        }
                    }

                    // Clean up timed out peers
                    for (int i = 0; i < timedOutPeers.Count; i++)
                    {
                        IPEndPoint p = timedOutPeers[i];
                        string pName = "Player";
                        lock (_lock)
                        {
                            AuthenticatedPeerSession s;
                            if (_sessionsByEndpoint.TryGetValue(p.ToString(), out s) && s != null)
                            {
                                pName = s.PeerName;
                                _sessionsByEndpoint.Remove(p.ToString());
                            }
                        }
                        Log(string.Format("Peer {0} ({1}) timed out (inactivity > 15s).", pName, p));
                        if (OnPeerDisconnected != null) OnPeerDisconnected(p, pName);
                    }

                    for (int i = 0; i < targetPeers.Count; i++)
                    {
                        SendMonotonicPing(targetPeers[i]);
                    }

                    CleanExpiredPings();
                }
                catch (ThreadAbortException) { break; }
                catch { }
            }
        }

        // Main UDP Receive Loop: dispatches each datagram ONCE
        private void ReceiveLoop()
        {
            IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning && _udp != null)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remoteEp);
                    if (data == null || data.Length < 5) continue;

                    // Verify magic "P2PH"
                    if (data[0] != MagicHeader[0] || data[1] != MagicHeader[1] ||
                        data[2] != MagicHeader[2] || data[3] != MagicHeader[3])
                    {
                        continue;
                    }

                    byte packetType = data[4];

                    switch (packetType)
                    {
                        case TypeHelloReq:
                            HandleIncomingHelloReq(data, remoteEp);
                            break;

                        case TypeHelloResp:
                            HandleIncomingHelloResp(data, remoteEp);
                            break;

                        case TypeAuthConfirm:
                            HandleIncomingAuthConfirm(data, remoteEp);
                            break;

                        case TypePing:
                            HandleIncomingPing(data, remoteEp);
                            break;

                        case TypePong:
                            HandleIncomingPong(data);
                            break;

                        case TypeData:
                            HandleIncomingData(data, remoteEp);
                            break;
                    }
                }
                catch (SocketException) { if (!_isRunning) break; }
                catch (ThreadAbortException) { break; }
                catch (Exception ex) { if (!_isRunning) break; Log("Recv error: " + ex.Message); }
            }
        }

        #region Handshake Protocol (Transcript-based Mutual Authentication)

        // Step 1: Client -> Host (HELLO_REQ)
        private void SendHelloReq(IPEndPoint target, AuthenticatedPeerSession session)
        {
            if (session == null || session.MacKey == null) return;

            long timestamp = DateTime.UtcNow.Ticks;
            byte[] tsBytes = BitConverter.GetBytes(timestamp);

            byte[] nameBytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(_clientPlayerName) ? "Player" : _clientPlayerName);
            if (nameBytes.Length > 24)
            {
                byte[] trimmed = new byte[24];
                Array.Copy(nameBytes, 0, trimmed, 0, 24);
                nameBytes = trimmed;
            }

            // Transcript1: SessionId (16B) | Role: 0x01 (1B) | ClientNonce (16B) | Timestamp (8B) | NameLen (1B) | NameBytes (N B)
            byte[] transcript = new byte[16 + 1 + 16 + 8 + 1 + nameBytes.Length];
            Array.Copy(session.SessionIdBytes, 0, transcript, 0, 16);
            transcript[16] = 0x01; // Role: CLIENT
            Array.Copy(session.ClientNonce, 0, transcript, 17, 16);
            Array.Copy(tsBytes, 0, transcript, 33, 8);
            transcript[41] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, transcript, 42, nameBytes.Length);

            byte[] hmac;
            using (HMACSHA256 h = new HMACSHA256(session.MacKey))
            {
                hmac = h.ComputeHash(transcript);
            }

            // Packet: Magic (4B) | Type (1B) | Transcript | HMAC (32B)
            byte[] packet = new byte[5 + transcript.Length + hmac.Length];
            Array.Copy(MagicHeader, 0, packet, 0, 4);
            packet[4] = TypeHelloReq;
            Array.Copy(transcript, 0, packet, 5, transcript.Length);
            Array.Copy(hmac, 0, packet, 5 + transcript.Length, hmac.Length);

            RawSend(packet, target);
        }

        private void HandleIncomingHelloReq(byte[] data, IPEndPoint remoteEp)
        {
            // Expected minimum length: 5 + 41 (transcript) + 32 (hmac) = 78 bytes
            if (data.Length < 78 || _currentSession == null) return;

            byte[] sessId = new byte[16];
            Array.Copy(data, 5, sessId, 0, 16);

            // Validate session ID matches
            if (!CryptoSecurity.FixedTimeEquals(sessId, _currentSession.SessionIdBytes)) return;

            byte role = data[21];
            if (role != 0x01) return; // Must be role CLIENT

            byte[] cNonce = new byte[16];
            Array.Copy(data, 22, cNonce, 0, 16);

            long timestamp = BitConverter.ToInt64(data, 38);
            TimeSpan delta = DateTime.UtcNow - new DateTime(timestamp);
            if (Math.Abs(delta.TotalSeconds) > 60) return; // Expired timestamp

            // Replay protection: check client nonce
            string nonceHex = ToHex(cNonce);
            lock (_seenNonces)
            {
                if (_seenNonces.ContainsKey(nonceHex)) return; // Replayed nonce!
                _seenNonces[nonceHex] = DateTime.UtcNow.AddMinutes(5);
            }

            string playerName = "Player";
            int transcriptLen = 41;
            if (data.Length >= 79)
            {
                byte nameLen = data[46];
                if (data.Length >= 5 + 41 + 1 + nameLen + 32)
                {
                    transcriptLen = 41 + 1 + nameLen;
                    try
                    {
                        playerName = Encoding.UTF8.GetString(data, 47, nameLen);
                    }
                    catch { }
                }
            }

            // Verify HMAC
            byte[] transcript = new byte[transcriptLen];
            Array.Copy(data, 5, transcript, 0, transcriptLen);

            byte[] receivedHmac = new byte[32];
            Array.Copy(data, 5 + transcriptLen, receivedHmac, 0, 32);

            byte[] expectedHmac;
            using (HMACSHA256 h = new HMACSHA256(_currentSession.MacKey))
            {
                expectedHmac = h.ComputeHash(transcript);
            }

            if (!CryptoSecurity.FixedTimeEquals(expectedHmac, receivedHmac)) return;

            // Valid client verified! Record session for this peer
            AuthenticatedPeerSession peerSession;
            lock (_lock)
            {
                peerSession = new AuthenticatedPeerSession
                {
                    SessionIdBytes = _currentSession.SessionIdBytes,
                    SessionIdHex = _currentSession.SessionIdHex,
                    SharedSecret = _currentSession.SharedSecret,
                    EncKey = _currentSession.EncKey,
                    MacKey = _currentSession.MacKey,
                    RemoteEndpoint = remoteEp,
                    ClientNonce = cNonce,
                    HostNonce = CryptoSecurity.GenerateRandomBytes(16),
                    PeerName = playerName,
                    LastSeenUtc = DateTime.UtcNow,
                    IsMutualAuthenticated = false
                };
                _sessionsByEndpoint[remoteEp.ToString()] = peerSession;
            }

            // Step 2: Send HELLO_RESP
            SendHelloResp(remoteEp, peerSession);
        }

        // Step 2: Host -> Client (HELLO_RESP)
        private void SendHelloResp(IPEndPoint target, AuthenticatedPeerSession session)
        {
            long timestamp = DateTime.UtcNow.Ticks;
            byte[] tsBytes = BitConverter.GetBytes(timestamp);

            // Transcript2: SessionId (16B) | Role: 0x02 (1B) | ClientNonce (16B) | HostNonce (16B) | Timestamp (8B)
            byte[] transcript = new byte[16 + 1 + 16 + 16 + 8];
            Array.Copy(session.SessionIdBytes, 0, transcript, 0, 16);
            transcript[16] = 0x02; // Role: HOST
            Array.Copy(session.ClientNonce, 0, transcript, 17, 16);
            Array.Copy(session.HostNonce, 0, transcript, 33, 16);
            Array.Copy(tsBytes, 0, transcript, 49, 8);

            byte[] hmac;
            using (HMACSHA256 h = new HMACSHA256(session.MacKey))
            {
                hmac = h.ComputeHash(transcript);
            }

            // Packet: Magic (4B) | Type (1B) | Transcript (57B) | HMAC (32B) = 94B
            byte[] packet = new byte[5 + transcript.Length + hmac.Length];
            Array.Copy(MagicHeader, 0, packet, 0, 4);
            packet[4] = TypeHelloResp;
            Array.Copy(transcript, 0, packet, 5, transcript.Length);
            Array.Copy(hmac, 0, packet, 5 + transcript.Length, hmac.Length);

            RawSend(packet, target);
        }

        private void HandleIncomingHelloResp(byte[] data, IPEndPoint remoteEp)
        {
            // Expected length: 5 + 57 + 32 = 94 bytes
            if (data.Length < 94 || _currentSession == null) return;

            byte[] sessId = new byte[16];
            Array.Copy(data, 5, sessId, 0, 16);
            if (!CryptoSecurity.FixedTimeEquals(sessId, _currentSession.SessionIdBytes)) return;

            byte role = data[21];
            if (role != 0x02) return; // Must be role HOST

            byte[] echoedCNonce = new byte[16];
            Array.Copy(data, 22, echoedCNonce, 0, 16);
            if (!CryptoSecurity.FixedTimeEquals(echoedCNonce, _currentSession.ClientNonce)) return; // Mismatched nonce!

            byte[] hNonce = new byte[16];
            Array.Copy(data, 38, hNonce, 0, 16);

            long timestamp = BitConverter.ToInt64(data, 54);
            TimeSpan delta = DateTime.UtcNow - new DateTime(timestamp);
            if (Math.Abs(delta.TotalSeconds) > 60) return;

            // Verify HMAC
            byte[] transcript = new byte[57];
            Array.Copy(data, 5, transcript, 0, 57);

            byte[] receivedHmac = new byte[32];
            Array.Copy(data, 62, receivedHmac, 0, 32);

            byte[] expectedHmac;
            using (HMACSHA256 h = new HMACSHA256(_currentSession.MacKey))
            {
                expectedHmac = h.ComputeHash(transcript);
            }

            if (!CryptoSecurity.FixedTimeEquals(expectedHmac, receivedHmac)) return;

            // Host authenticity verified!
            _currentSession.HostNonce = hNonce;
            _currentSession.IsMutualAuthenticated = true;
            _currentSession.RemoteEndpoint = remoteEp;
            _currentSession.LastSeenUtc = DateTime.UtcNow;
            _isConnected = true;

            // Step 3: Send AUTH_CONFIRM to finalize
            SendAuthConfirm(remoteEp, _currentSession);

            // Derive per-session independent keys from mutual nonces
            DerivePerSessionKeys(_currentSession);

            Log(string.Format("Mutual peer authentication SUCCESSFUL with {0}!", remoteEp));
            if (OnPeerConnected != null) OnPeerConnected(remoteEp, _currentSession.PeerName ?? "Host", Math.Max(1, _currentLatencyMs));
            if (OnConnected != null) OnConnected(remoteEp, Math.Max(1, _currentLatencyMs));
        }

        // Step 3: Client -> Host (AUTH_CONFIRM)
        private void SendAuthConfirm(IPEndPoint target, AuthenticatedPeerSession session)
        {
            // Transcript3: SessionId (16B) | Role: 0x03 (1B) | ClientNonce (16B) | HostNonce (16B)
            byte[] transcript = new byte[16 + 1 + 16 + 16];
            Array.Copy(session.SessionIdBytes, 0, transcript, 0, 16);
            transcript[16] = 0x03; // Role: CONFIRM
            Array.Copy(session.ClientNonce, 0, transcript, 17, 16);
            Array.Copy(session.HostNonce, 0, transcript, 33, 16);

            byte[] hmac;
            using (HMACSHA256 h = new HMACSHA256(session.MacKey))
            {
                hmac = h.ComputeHash(transcript);
            }

            // Packet: Magic (4B) | Type (1B) | Transcript (49B) | HMAC (32B) = 86B
            byte[] packet = new byte[5 + transcript.Length + hmac.Length];
            Array.Copy(MagicHeader, 0, packet, 0, 4);
            packet[4] = TypeAuthConfirm;
            Array.Copy(transcript, 0, packet, 5, transcript.Length);
            Array.Copy(hmac, 0, packet, 5 + transcript.Length, hmac.Length);

            RawSend(packet, target);
        }

        private void HandleIncomingAuthConfirm(byte[] data, IPEndPoint remoteEp)
        {
            if (data.Length < 86) return;

            AuthenticatedPeerSession session;
            lock (_lock)
            {
                if (!_sessionsByEndpoint.TryGetValue(remoteEp.ToString(), out session) || session == null)
                {
                    return;
                }
            }

            byte[] sessId = new byte[16];
            Array.Copy(data, 5, sessId, 0, 16);
            if (!CryptoSecurity.FixedTimeEquals(sessId, session.SessionIdBytes)) return;

            byte role = data[21];
            if (role != 0x03) return;

            byte[] cNonce = new byte[16];
            Array.Copy(data, 22, cNonce, 0, 16);
            if (!CryptoSecurity.FixedTimeEquals(cNonce, session.ClientNonce)) return;

            byte[] echoedHNonce = new byte[16];
            Array.Copy(data, 38, echoedHNonce, 0, 16);
            if (!CryptoSecurity.FixedTimeEquals(echoedHNonce, session.HostNonce)) return;

            // Verify HMAC
            byte[] transcript = new byte[49];
            Array.Copy(data, 5, transcript, 0, 49);

            byte[] receivedHmac = new byte[32];
            Array.Copy(data, 54, receivedHmac, 0, 32);

            byte[] expectedHmac;
            using (HMACSHA256 h = new HMACSHA256(session.MacKey))
            {
                expectedHmac = h.ComputeHash(transcript);
            }

            if (!CryptoSecurity.FixedTimeEquals(expectedHmac, receivedHmac)) return;

            // Mutual authentication complete on Host!
            session.IsMutualAuthenticated = true;
            session.LastSeenUtc = DateTime.UtcNow;
            _isConnected = true;

            // Derive per-session independent keys from mutual nonces
            DerivePerSessionKeys(session);

            Log(string.Format("Mutual peer authentication confirmed for player {0} at {1}!", session.PeerName, remoteEp));
            if (OnPeerConnected != null) OnPeerConnected(remoteEp, session.PeerName, Math.Max(1, _currentLatencyMs));
            if (OnConnected != null) OnConnected(remoteEp, Math.Max(1, _currentLatencyMs));
        }

        private void DerivePerSessionKeys(AuthenticatedPeerSession session)
        {
            if (session == null || session.SharedSecret == null || session.ClientNonce == null || session.HostNonce == null) return;

            byte[] sessionSeed = new byte[32];
            Array.Copy(session.ClientNonce, 0, sessionSeed, 0, 16);
            Array.Copy(session.HostNonce, 0, sessionSeed, 16, 16);

            byte[] encKey, macKey;
            using (HMACSHA256 hmac = new HMACSHA256(session.SharedSecret))
            {
                byte[] encInfo = Encoding.UTF8.GetBytes("P2P_SESSION_ENC_" + ToHex(sessionSeed));
                encKey = hmac.ComputeHash(encInfo);

                byte[] macInfo = Encoding.UTF8.GetBytes("P2P_SESSION_MAC_" + ToHex(sessionSeed));
                macKey = hmac.ComputeHash(macInfo);
            }

            session.EncKey = encKey;
            session.MacKey = macKey;
        }

        #endregion

        #region Monotonic RTT Measurement

        private void SendMonotonicPing(IPEndPoint target)
        {
            long tokenId = Interlocked.Increment(ref _pingIdCounter);
            long sendTicks = Stopwatch.GetTimestamp();

            lock (_pendingPings)
            {
                _pendingPings[tokenId] = new PingProbe { SendTicks = sendTicks, RemotePeer = target };
            }

            byte[] packet = new byte[13];
            Array.Copy(MagicHeader, 0, packet, 0, 4);
            packet[4] = TypePing;
            byte[] tokenBytes = BitConverter.GetBytes(tokenId);
            Array.Copy(tokenBytes, 0, packet, 5, 8);

            RawSend(packet, target);
        }

        private void HandleIncomingPing(byte[] data, IPEndPoint remoteEp)
        {
            if (data.Length < 13) return;

            // Echo back exact token in PONG
            byte[] pong = new byte[13];
            Array.Copy(MagicHeader, 0, pong, 0, 4);
            pong[4] = TypePong;
            Array.Copy(data, 5, pong, 5, 8);

            RawSend(pong, remoteEp);
        }

        private void HandleIncomingPong(byte[] data)
        {
            if (data.Length < 13) return;

            long tokenId = BitConverter.ToInt64(data, 5);
            PingProbe probe;

            lock (_pendingPings)
            {
                if (!_pendingPings.TryGetValue(tokenId, out probe) || probe == null)
                {
                    // Unknown or expired probe
                    return;
                }
                _pendingPings.Remove(tokenId);
            }

            // Calculate true RTT from sender's monotonic clock
            long elapsedTicks = Stopwatch.GetTimestamp() - probe.SendTicks;
            if (elapsedTicks >= 0)
            {
                double rttMs = (elapsedTicks * 1000.0) / Stopwatch.Frequency;
                _currentLatencyMs = (long)Math.Round(rttMs);

                if (probe.RemotePeer != null)
                {
                    lock (_lock)
                    {
                        AuthenticatedPeerSession s;
                        if (_sessionsByEndpoint.TryGetValue(probe.RemotePeer.ToString(), out s) && s != null)
                        {
                            s.LastSeenUtc = DateTime.UtcNow;
                        }
                    }
                    if (OnPeerPingUpdated != null) OnPeerPingUpdated(probe.RemotePeer, _currentLatencyMs);
                }

                if (OnPingUpdated != null) OnPingUpdated(_currentLatencyMs);
            }
        }

        private void CleanExpiredPings()
        {
            long now = Stopwatch.GetTimestamp();
            long timeoutTicks = 5 * Stopwatch.Frequency; // 5 seconds timeout

            lock (_pendingPings)
            {
                List<long> expired = new List<long>();
                foreach (KeyValuePair<long, PingProbe> kv in _pendingPings)
                {
                    if (now - kv.Value.SendTicks > timeoutTicks)
                    {
                        expired.Add(kv.Key);
                    }
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    _pendingPings.Remove(expired[i]);
                }
            }
        }

        #endregion

        #region Data Transmission & Single Event Dispatch

        // Sends data payload to target peer (Encrypted RUDP frame)
        public void SendDataTo(byte[] payload, IPEndPoint target)
        {
            if (payload == null || payload.Length == 0) return;
            if (target == null) target = _targetPeerEndpoint;
            if (target == null) return;

            AuthenticatedPeerSession session;
            lock (_lock)
            {
                if (!_sessionsByEndpoint.TryGetValue(target.ToString(), out session) || session == null)
                {
                    session = _currentSession;
                }
            }

            byte[] dataToSend = payload;
            if (session != null && session.EncKey != null && session.MacKey != null)
            {
                dataToSend = CryptoSecurity.EncryptThenMac(payload, session.EncKey, session.MacKey);
            }

            byte[] packet = new byte[5 + dataToSend.Length];
            Array.Copy(MagicHeader, 0, packet, 0, 4);
            packet[4] = TypeData;
            Array.Copy(dataToSend, 0, packet, 5, dataToSend.Length);

            RawSend(packet, target);
        }

        public void SendData(byte[] payload)
        {
            SendDataTo(payload, _targetPeerEndpoint);
        }

        // Handles data and dispatches to ONE unified event
        private void HandleIncomingData(byte[] data, IPEndPoint remoteEp)
        {
            if (data.Length <= 5) return;

            // Only accept data from an authenticated peer session
            AuthenticatedPeerSession session;
            lock (_lock)
            {
                if (!_sessionsByEndpoint.TryGetValue(remoteEp.ToString(), out session) || session == null || !session.IsMutualAuthenticated)
                {
                    // Unauthenticated peer: discard
                    return;
                }
            }

            session.LastSeenUtc = DateTime.UtcNow;

            byte[] rawPayload = new byte[data.Length - 5];
            Array.Copy(data, 5, rawPayload, 0, rawPayload.Length);

            byte[] decrypted = null;
            if (session.EncKey != null && session.MacKey != null)
            {
                decrypted = CryptoSecurity.VerifyThenDecrypt(rawPayload, session.EncKey, session.MacKey);
            }
            else
            {
                decrypted = rawPayload;
            }

            if (decrypted == null || decrypted.Length == 0)
            {
                // Discard invalid / tampered payload
                return;
            }

            // Single unified event dispatch: exactly once per received datagram
            if (OnDataReceived != null)
            {
                OnDataReceived(remoteEp, decrypted);
            }
        }

        private void RawSend(byte[] data, IPEndPoint target)
        {
            if (_udp != null && target != null)
            {
                try { _udp.Send(data, data.Length, target); } catch { }
            }
        }

        #endregion

        #region Safe Lifecycle Management

        public void Stop()
        {
            lock (_lock)
            {
                _isRunning = false;
                _isConnected = false;

                // Close socket to cancel any blocking UdpClient.Receive() calls
                if (_udp != null)
                {
                    try { _udp.Close(); } catch { }
                    _udp = null;
                }

                // Join worker threads with reasonable timeout
                if (_recvThread != null && _recvThread.IsAlive)
                {
                    try { _recvThread.Join(500); } catch { }
                    _recvThread = null;
                }

                if (_punchThread != null && _punchThread.IsAlive)
                {
                    try { _punchThread.Join(500); } catch { }
                    _punchThread = null;
                }

                lock (_pendingPings) { _pendingPings.Clear(); }
                lock (_seenNonces) { _seenNonces.Clear(); }
                _sessionsByEndpoint.Clear();
                _currentSession = null;

                // Note: We deliberately DO NOT wipe UI event subscriptions (OnConnected, OnDataReceived, etc.)
                // so that the UI and Bridge can cleanly restart without losing their listeners.
                if (OnDisconnected != null)
                {
                    try { OnDisconnected(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        #endregion

        #region Helpers

        private void Log(string msg)
        {
            if (OnLog != null) OnLog(msg);
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null) return "";
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return new byte[0];
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        #endregion
    }
}
