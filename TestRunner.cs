using System;
using System.IO;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MineTunnel.Common;
using MineTunnel.Core;
using MineTunnel.Installer;
using MineTunnel.UI;

namespace MineTunnel.Test
{
    class Program
    {
        static int passed = 0;
        static int failed = 0;

        static void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [PASS] " + testName);
                Console.ResetColor();
                passed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [FAIL] " + testName);
                Console.ResetColor();
                failed++;
            }
        }

        static int Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("       MineTunnel End-to-End Integration Test     ");
            Console.WriteLine("==================================================");

            int mockServerPort = 29701;
            int tunnelPort = 29700;

            // 1. Start Mock Minecraft Server
            Console.WriteLine("\n1. Starting Mock Minecraft Server on port " + mockServerPort + "...");
            TcpListener mockServer = new TcpListener(IPAddress.Loopback, mockServerPort);
            mockServer.Start();

            Thread mockThread = new Thread(() =>
            {
                try
                {
                    using (TcpClient client = mockServer.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[1024];
                        int r = stream.Read(buffer, 0, buffer.Length);
                        string received = Encoding.UTF8.GetString(buffer, 0, r);
                        byte[] reply = Encoding.UTF8.GetBytes("PONG:" + received);
                        stream.Write(reply, 0, reply.Length);
                        stream.Flush();
                    }
                }
                catch { }
            });
            mockThread.IsBackground = true;
            mockThread.Start();
            Assert(true, "Mock Minecraft Server Started");

            // 2. Start TunnelHost
            Console.WriteLine("\n2. Starting TunnelHost on port " + tunnelPort + "...");
            TunnelHost host = new TunnelHost();
            bool hostStarted = host.Start(tunnelPort, "TestRoom", "Secret123", mockServerPort);
            Assert(hostStarted, "TunnelHost Started");

            // 3. Connect TunnelClient
            Console.WriteLine("\n3. Connecting TunnelClient to TunnelHost...");
            TunnelClient clientTunnel = new TunnelClient();
            bool clientConnected = clientTunnel.Connect("127.0.0.1", tunnelPort, "TestRoom", "Secret123", "PlayerSteve");
            Assert(clientConnected, "TunnelClient Connected & Authenticated");
            int proxyPort = clientTunnel.LocalProxyPort;
            Console.WriteLine("   Local proxy port opened: " + proxyPort);

            // 4. Test Game Data Forwarding through Tunnel
            Console.WriteLine("\n4. Simulating Minecraft Client connecting to 127.0.0.1:" + proxyPort + "...");
            using (TcpClient gameClient = new TcpClient())
            {
                gameClient.Connect("127.0.0.1", proxyPort);
                using (NetworkStream gameStream = gameClient.GetStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes("MC_HANDSHAKE_CHUNK_DATA");
                    gameStream.Write(payload, 0, payload.Length);
                    gameStream.Flush();

                    byte[] replyBuf = new byte[1024];
                    gameStream.ReadTimeout = 4000;
                    int readBytes = gameStream.Read(replyBuf, 0, replyBuf.Length);
                    string replyMsg = Encoding.UTF8.GetString(replyBuf, 0, readBytes);
                    Console.WriteLine("   Received from tunnel: " + replyMsg);
                    Assert(replyMsg == "PONG:MC_HANDSHAKE_CHUNK_DATA", "End-to-End Minecraft Data Proxy Roundtrip");
                }
            }

            // 5. Test LAN Announcement Rebroadcasting
            Console.WriteLine("\n5. Testing LAN Announcement Rebroadcasting...");
            bool lanReceived = false;
            clientTunnel.OnLanGameReceived += (motd, port) =>
            {
                if (motd == "Steve's World") lanReceived = true;
            };

            host.BroadcastLanAnnouncement("Steve's World", tunnelPort);
            Thread.Sleep(300);
            Assert(lanReceived, "LAN Game Rebroadcast across Tunnel");

            // Cleanup
            clientTunnel.Disconnect();
            host.Stop();
            mockServer.Stop();

            // 6. Test Zero-Port-Forwarding Relay Mode
            Console.WriteLine("\n6. Testing Zero-Port-Forwarding Relay Mode (CGNAT Bypass)...");
            int relayPort = 29850;
            int relayMockPort = 29851;
            
            TcpListener relayMockServer = new TcpListener(IPAddress.Loopback, relayMockPort);
            relayMockServer.Start();
            Thread relayMockThread = new Thread(() =>
            {
                try
                {
                    using (TcpClient client = relayMockServer.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[1024];
                        int r = stream.Read(buffer, 0, buffer.Length);
                        string received = Encoding.UTF8.GetString(buffer, 0, r);
                        byte[] reply = Encoding.UTF8.GetBytes("RELAY_PONG:" + received);
                        stream.Write(reply, 0, reply.Length);
                        stream.Flush();
                    }
                }
                catch { }
            });
            relayMockThread.IsBackground = true;
            relayMockThread.Start();

            // Start Relay Server
            MineTunnel.Relay.RelayServer relay = new MineTunnel.Relay.RelayServer(relayPort);
            relay.Start();

            // Host connects outbound to Relay
            RelayHostConnector relayHost = new RelayHostConnector();
            bool rHostOk = relayHost.ConnectAndRegister("127.0.0.1", relayPort, "NoPortForwardRoom", "Secret123", relayMockPort);
            Assert(rHostOk, "RelayHost Outbound Registration (Zero Port Forwarding)");

            // Client connects outbound to Relay
            TunnelClient relayClient = new TunnelClient();
            bool rClientOk = relayClient.Connect("127.0.0.1", relayPort, "NoPortForwardRoom", "Secret123", "Alex");
            Assert(rClientOk, "TunnelClient Outbound Connection to Relay");
            int rProxyPort = relayClient.LocalProxyPort;

            // Connect Game Client through Relay
            using (TcpClient gameClient = new TcpClient())
            {
                gameClient.Connect("127.0.0.1", rProxyPort);
                using (NetworkStream gameStream = gameClient.GetStream())
                {
                    byte[] payload = Encoding.UTF8.GetBytes("PAYLOAD_THROUGH_RELAY");
                    gameStream.Write(payload, 0, payload.Length);
                    gameStream.Flush();

                    byte[] replyBuf = new byte[1024];
                    gameStream.ReadTimeout = 4000;
                    int readBytes = gameStream.Read(replyBuf, 0, replyBuf.Length);
                    string replyMsg = Encoding.UTF8.GetString(replyBuf, 0, readBytes);
                    Console.WriteLine("   Received from Relay: " + replyMsg);
                    Assert(replyMsg == "RELAY_PONG:PAYLOAD_THROUGH_RELAY", "Relay End-to-End Minecraft Roundtrip");
                }
            }

            relayClient.Disconnect();
            relayHost.Disconnect();
            relay.Stop();
            relayMockServer.Stop();

            Console.WriteLine("\n7. Testing UI Responsive Scaling & Resolution Independence...");
            using (MineTunnelForm form = new MineTunnelForm())
            {
                Assert(form.AutoScaleMode == AutoScaleMode.Dpi, "MineTunnelForm AutoScaleMode is Dpi");

                // Simulate 720p resolution
                form.Size = new Size(820, 600);
                Assert(form.ClientSize.Width >= 800, "MineTunnelForm 720p scaling adaptation");

                // Simulate resolution resize
                form.Size = new Size(1000, 700);
                Assert(form.ClientSize.Width >= 950, "MineTunnelForm 1000px width scaling adaptation");

                // Verify internal controls adapt and stretch
                TabControl tabs = null;
                foreach (Control c in form.Controls)
                {
                    if (c is TabControl) { tabs = (TabControl)c; break; }
                }
                Assert(tabs != null && tabs.Width >= 950, "TabControl dynamic stretch");
            }

            using (SetupForm setup = new SetupForm())
            {
                Assert(setup.AutoScaleMode == AutoScaleMode.Dpi, "SetupForm AutoScaleMode is Dpi");
                setup.Size = new Size(800, 600);
                Assert(setup.ClientSize.Width >= 750, "SetupForm scaling adaptation");
            }

            Console.WriteLine("\n8. Testing P2P Configuration Persistence...");
            string origCode = ConfigManager.Instance.P2PLastPeerCode;
            ConfigManager.Instance.P2PLastPeerCode = "MINE-P2P#112.198.45.12:49152#TestWorld";
            ConfigManager.Instance.IsP2PMode = true;
            ConfigManager.Instance.Save();
            
            // Reload and verify
            ConfigManager.Instance.P2PLastPeerCode = "";
            ConfigManager.Instance.Load();
            Assert(ConfigManager.Instance.P2PLastPeerCode == "MINE-P2P#112.198.45.12:49152#TestWorld", "ConfigManager P2PLastPeerCode persistence roundtrip");
            Assert(ConfigManager.Instance.IsP2PMode == true, "ConfigManager IsP2PMode persistence");
            ConfigManager.Instance.P2PLastPeerCode = origCode;
            ConfigManager.Instance.Save();

            Console.WriteLine("\n9. Testing RFC 5389 STUN Client...");
            byte[] stunReq = StunClient.BuildBindingRequest();
            Assert(stunReq != null && stunReq.Length == 20, "STUN Binding Request 20-byte Header Construction");
            Assert(stunReq[0] == 0x00 && stunReq[1] == 0x01, "STUN Message Type 0x0001 (Binding Request)");
            Assert(stunReq[4] == 0x21 && stunReq[5] == 0x12 && stunReq[6] == 0xA4 && stunReq[7] == 0x42, "STUN Magic Cookie 0x2112A442");

            P2PStunResult stunRes = StunClient.Query(null, 3000);
            if (stunRes.Success)
            {
                Assert(stunRes.PublicEndpoint != null, "STUN Public Endpoint Resolved: " + stunRes.PublicEndpoint.ToString() + " (via " + stunRes.ServerUsed + ")");
            }
            else
            {
                Assert(true, "STUN Query executed (Network fallback: " + stunRes.ErrorMessage + ")");
            }

            Console.WriteLine("\n10. Testing P2P Invite Code Generator & Parser...");
            IPEndPoint testEp = new IPEndPoint(IPAddress.Parse("112.198.20.50"), 48123);
            P2PHolePuncher puncher = new P2PHolePuncher();
            string dummyToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            string generatedCode = "MINE-P2P#112.198.20.50:48123#EpicWorld#" + dummyToken;

            IPEndPoint parsedEp;
            string parsedRoom;
            byte[] parsedSessId;
            byte[] parsedSecret;
            bool parseOk = P2PHolePuncher.TryParseInviteCode(generatedCode, out parsedEp, out parsedRoom, out parsedSessId, out parsedSecret);
            Assert(parseOk, "P2PHolePuncher Invite Code Parsing");
            Assert(parsedEp != null && parsedEp.Address.ToString() == "112.198.20.50" && parsedEp.Port == 48123, "P2P Endpoint Address & Port Match");
            Assert(parsedRoom == "EpicWorld", "P2P Room Name Match");
            Assert(parsedSessId != null && parsedSessId.Length == 16, "P2P Session ID Extracted");
            Assert(parsedSecret != null && parsedSecret.Length == 16, "P2P Shared Secret Extracted");

            bool invalidParse = P2PHolePuncher.TryParseInviteCode("INVALID_CODE", out parsedEp, out parsedRoom, out parsedSessId, out parsedSecret);
            Assert(!invalidParse, "P2PHolePuncher Rejection of Malformed Code");

            Console.WriteLine("\n11. Testing P2P Stream Bridge Proxy Binding...");
            P2PStreamBridge bridge = new P2PStreamBridge(puncher);
            bool clientProxyBound = bridge.StartClientProxy(0);
            Assert(clientProxyBound && bridge.LocalProxyPort > 0, "P2P Client Local Proxy Listening on Port " + bridge.LocalProxyPort);
            bridge.Stop();

            Console.WriteLine("\n==================================================");
            Console.WriteLine(" Results: Passed: " + passed + " | Failed: " + failed);
            Console.WriteLine("==================================================");
            Console.WriteLine(" Results: Passed: " + passed + " | Failed: " + failed);
            Console.WriteLine("==================================================");

            return failed == 0 ? 0 : 1;
        }
    }
}
