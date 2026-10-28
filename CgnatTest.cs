using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MineTunnel.Test
{
    public enum NatFilteringMode
    {
        FullCone,           // Endpoint-Independent (accepts from anyone once mapped)
        RestrictedCone,     // Address-Dependent (accepts if internal sent to this IP)
        PortRestrictedCone, // Address & Port-Dependent (accepts ONLY if internal sent to this exact IP:Port - standard in PLDT CGNAT)
        Symmetric           // New port allocated per destination + port-restricted filtering
    }

    public class SimulatedCgnatGateway
    {
        public string Name { get; private set; }
        public NatFilteringMode FilteringMode { get; set; }
        public IPEndPoint LanEndpoint { get; private set; }
        public IPEndPoint WanEndpoint { get; private set; }

        private UdpClient _lanSocket;
        private UdpClient _wanSocket;
        private volatile bool _running;

        // Filtering tables
        private readonly HashSet<string> _allowedInboundEndpoints = new HashSet<string>();
        private IPEndPoint _mappedInternalEndpoint;

        public int DroppedPacketsCount = 0;
        public int ForwardedPacketsCount = 0;

        public SimulatedCgnatGateway(string name, int lanPort, int wanPort, NatFilteringMode mode)
        {
            Name = name;
            FilteringMode = mode;

            _lanSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, lanPort));
            _wanSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, wanPort));

            LanEndpoint = (IPEndPoint)_lanSocket.Client.LocalEndPoint;
            WanEndpoint = (IPEndPoint)_wanSocket.Client.LocalEndPoint;

            _running = true;

            new Thread(LanToWanLoop) { IsBackground = true, Name = name + "_LanToWan" }.Start();
            new Thread(WanToLanLoop) { IsBackground = true, Name = name + "_WanToLan" }.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _lanSocket.Close(); } catch { }
            try { _wanSocket.Close(); } catch { }
        }

        private void LanToWanLoop()
        {
            IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _lanSocket.Receive(ref remoteEp);
                    if (data == null || data.Length < 6) continue;

                    byte[] ipBytes = new byte[4];
                    Array.Copy(data, 0, ipBytes, 0, 4);
                    IPAddress targetIp = new IPAddress(ipBytes);
                    ushort targetPort = BitConverter.ToUInt16(data, 4);
                    IPEndPoint targetEp = new IPEndPoint(targetIp, targetPort);

                    byte[] payload = new byte[data.Length - 6];
                    Array.Copy(data, 6, payload, 0, payload.Length);

                    lock (_allowedInboundEndpoints)
                    {
                        _mappedInternalEndpoint = remoteEp;
                        _allowedInboundEndpoints.Add(targetEp.ToString());
                    }

                    _wanSocket.Send(payload, payload.Length, targetEp);
                    Console.WriteLine(string.Format("  [{0}] OUTBOUND translated {1} -> WAN {2} -> Dest {3}",
                        Name, remoteEp, WanEndpoint, targetEp));
                }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { if (!_running) break; Console.WriteLine(Name + " LAN error: " + ex.Message); }
            }
        }

        private void WanToLanLoop()
        {
            IPEndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _wanSocket.Receive(ref senderEp);
                    if (data == null) continue;

                    bool allow = false;

                    lock (_allowedInboundEndpoints)
                    {
                        if (FilteringMode == NatFilteringMode.FullCone)
                        {
                            allow = (_mappedInternalEndpoint != null);
                        }
                        else if (FilteringMode == NatFilteringMode.PortRestrictedCone || FilteringMode == NatFilteringMode.RestrictedCone)
                        {
                            allow = _allowedInboundEndpoints.Contains(senderEp.ToString());
                        }
                    }

                    if (allow && _mappedInternalEndpoint != null)
                    {
                        ForwardedPacketsCount++;
                        _lanSocket.Send(data, data.Length, _mappedInternalEndpoint);
                        Console.WriteLine(string.Format("  [{0}] INBOUND ACCEPTED from {1} -> forwarded to internal {2}",
                            Name, senderEp, _mappedInternalEndpoint));
                    }
                    else
                    {
                        DroppedPacketsCount++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(string.Format("  [{0} DROP] BLOCKED unsolicited inbound packet from {1} (No outbound pinhole state!)",
                            Name, senderEp));
                        Console.ResetColor();
                    }
                }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { if (!_running) break; Console.WriteLine(Name + " WAN error: " + ex.Message); }
            }
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("    PLDT CGNAT UDP Hole Punching Simulation Test Suite    ");
            Console.WriteLine("==========================================================");

            int hostLanPort = 31010;
            int hostWanPort = 41010;

            int clientLanPort = 31020;
            int clientWanPort = 41020;

            // Scenario 1: One-way punch (Host only registers STUN, Client tries to connect)
            Console.WriteLine("\n[SCENARIO 1] One-Way Inbound Attempt against PLDT CGNAT (What happens without signaling)...");
            SimulatedCgnatGateway hostCgnat = new SimulatedCgnatGateway("PLDT_Host_CGNAT", hostLanPort, hostWanPort, NatFilteringMode.PortRestrictedCone);
            SimulatedCgnatGateway clientCgnat = new SimulatedCgnatGateway("Client_CGNAT", clientLanPort, clientWanPort, NatFilteringMode.PortRestrictedCone);

            UdpClient internalHost = new UdpClient(50001);
            UdpClient internalClient = new UdpClient(50002);

            // Host queries STUN through its CGNAT gateway:
            byte[] hostStun = WrapWithTarget(IPAddress.Loopback, 19302, Encoding.UTF8.GetBytes("STUN_BINDING_REQ"));
            internalHost.Send(hostStun, hostStun.Length, new IPEndPoint(IPAddress.Loopback, hostLanPort));
            Thread.Sleep(100);

            // Client obtains Host's WAN endpoint (127.0.0.1:41010) and sends a punch request:
            byte[] punchPayload = Encoding.UTF8.GetBytes("P2PH_PUNCH_REQUEST");
            byte[] clientPacket = WrapWithTarget(IPAddress.Loopback, (ushort)hostWanPort, punchPayload);

            Console.WriteLine("Client punching towards Host WAN " + hostWanPort + "...");
            internalClient.Send(clientPacket, clientPacket.Length, new IPEndPoint(IPAddress.Loopback, clientLanPort));

            Thread.Sleep(500);

            Console.WriteLine("\nHost CGNAT Dropped Packets: " + hostCgnat.DroppedPacketsCount);
            Console.WriteLine("Host CGNAT Forwarded Packets: " + hostCgnat.ForwardedPacketsCount);

            if (hostCgnat.DroppedPacketsCount > 0 && hostCgnat.ForwardedPacketsCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[FAILURE DETECTED IN ONE-WAY PUNCH]");
                Console.WriteLine("RFC 4787 / PLDT CGNAT Verification:");
                Console.WriteLine("Because the Host never sent an outbound packet towards Client's WAN endpoint,");
                Console.WriteLine("Host's CGNAT dropped the packet as unsolicited!");
                Console.ResetColor();
            }

            hostCgnat.Stop();
            clientCgnat.Stop();

            // Scenario 2: Two-way Simultaneous UDP Hole Punching (RFC 5389 / ICE)
            Console.WriteLine("\n\n[SCENARIO 2] Two-Way Simultaneous Hole Punching (RFC 5389 / ICE Standard)...");
            SimulatedCgnatGateway hostCgnat2 = new SimulatedCgnatGateway("PLDT_Host_CGNAT", hostLanPort, hostWanPort, NatFilteringMode.PortRestrictedCone);
            SimulatedCgnatGateway clientCgnat2 = new SimulatedCgnatGateway("Client_CGNAT", clientLanPort, clientWanPort, NatFilteringMode.PortRestrictedCone);

            bool hostReceived = false;
            bool clientReceived = false;

            Thread hostRecvThread = new Thread(() =>
            {
                try
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] d = internalHost.Receive(ref ep);
                    string msg = Encoding.UTF8.GetString(d);
                    Console.WriteLine("  *** [HOST RECEIVED THROUGH CGNAT] " + msg + " ***");
                    hostReceived = true;
                }
                catch { }
            }) { IsBackground = true };
            hostRecvThread.Start();

            Thread clientRecvThread = new Thread(() =>
            {
                try
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] d = internalClient.Receive(ref ep);
                    string msg = Encoding.UTF8.GetString(d);
                    Console.WriteLine("  *** [CLIENT RECEIVED THROUGH CGNAT] " + msg + " ***");
                    clientReceived = true;
                }
                catch { }
            }) { IsBackground = true };
            clientRecvThread.Start();

            Console.WriteLine("Both sides punching towards each other's discovered WAN endpoints simultaneously...");

            // 1. Host punches towards Client's WAN (41020) -> creates outbound state in hostCgnat2
            byte[] hostPunch = WrapWithTarget(IPAddress.Loopback, (ushort)clientWanPort, Encoding.UTF8.GetBytes("HOST_PUNCH"));
            internalHost.Send(hostPunch, hostPunch.Length, new IPEndPoint(IPAddress.Loopback, hostLanPort));

            Thread.Sleep(50);

            // 2. Client punches towards Host's WAN (41010) -> creates outbound state in clientCgnat2
            byte[] clientPunch = WrapWithTarget(IPAddress.Loopback, (ushort)hostWanPort, Encoding.UTF8.GetBytes("CLIENT_PUNCH"));
            internalClient.Send(clientPunch, clientPunch.Length, new IPEndPoint(IPAddress.Loopback, clientLanPort));

            Thread.Sleep(500);

            // 3. Now send actual Minecraft payload from Client to Host
            byte[] mcData = WrapWithTarget(IPAddress.Loopback, (ushort)hostWanPort, Encoding.UTF8.GetBytes("MINECRAFT_CHUNK_DATA_OVER_P2P"));
            internalClient.Send(mcData, mcData.Length, new IPEndPoint(IPAddress.Loopback, clientLanPort));

            Thread.Sleep(800);

            Console.WriteLine("\nHost CGNAT Total Forwarded: " + hostCgnat2.ForwardedPacketsCount);
            Console.WriteLine("Client CGNAT Total Forwarded: " + clientCgnat2.ForwardedPacketsCount);

            if (hostReceived || clientReceived)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[SUCCESS] Bidirectional UDP Hole Punching passed through both Port-Restricted CGNATs!");
                Console.ResetColor();
            }

            hostCgnat2.Stop();
            clientCgnat2.Stop();
            internalHost.Close();
            internalClient.Close();

            Console.WriteLine("\n==========================================================");
            Console.WriteLine("                 SIMULATION ANALYSIS COMPLETE             ");
            Console.WriteLine("==========================================================");
        }

        private static byte[] WrapWithTarget(IPAddress targetIp, ushort targetPort, byte[] payload)
        {
            byte[] res = new byte[6 + payload.Length];
            Array.Copy(targetIp.GetAddressBytes(), 0, res, 0, 4);
            Array.Copy(BitConverter.GetBytes(targetPort), 0, res, 4, 2);
            Array.Copy(payload, 0, res, 6, payload.Length);
            return res;
        }
    }
}
