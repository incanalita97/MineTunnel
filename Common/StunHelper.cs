using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MineTunnel.Common
{
    public class StunResult
    {
        public bool Success { get; set; }
        public IPAddress PublicIp { get; set; }
        public int PublicPort { get; set; }
        public string NatTypeDescription { get; set; }

        public override string ToString()
        {
            if (!Success) return "STUN Failed";
            return string.Format("{0}:{1} ({2})", PublicIp, PublicPort, NatTypeDescription);
        }
    }

    public static class StunHelper
    {
        private static readonly string[] DefaultStunServers = new string[]
        {
            "stun.l.google.com",
            "stun1.l.google.com",
            "stun2.l.google.com",
            "stun.cloudflare.com"
        };

        public static StunResult QueryStun(int localPort = 0, int timeoutMs = 2500)
        {
            foreach (string stunHost in DefaultStunServers)
            {
                try
                {
                    StunResult res = QuerySingleStunServer(stunHost, 19302, localPort, timeoutMs);
                    if (res != null && res.Success) return res;
                }
                catch { }
            }

            return new StunResult { Success = false, NatTypeDescription = "Unable to reach STUN servers" };
        }

        private static StunResult QuerySingleStunServer(string serverHost, int serverPort, int localPort, int timeoutMs)
        {
            IPAddress[] addresses = Dns.GetHostAddresses(serverHost);
            if (addresses.Length == 0) return null;

            IPEndPoint stunServerEp = new IPEndPoint(addresses[0], serverPort);

            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;
                socket.Bind(new IPEndPoint(IPAddress.Any, localPort));

                // STUN Binding Request (RFC 5389)
                byte[] request = new byte[20];
                request[0] = 0x00;
                request[1] = 0x01; // Message Type: Binding Request
                request[2] = 0x00;
                request[3] = 0x00; // Message Length: 0

                // Magic Cookie: 0x2112A442
                request[4] = 0x21;
                request[5] = 0x12;
                request[6] = 0xA4;
                request[7] = 0x42;

                // Transaction ID: 12 random bytes
                byte[] transactionId = new byte[12];
                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                {
                    rng.GetBytes(transactionId);
                }
                Buffer.BlockCopy(transactionId, 0, request, 8, 12);

                socket.SendTo(request, stunServerEp);

                byte[] response = new byte[512];
                EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
                int received = socket.ReceiveFrom(response, ref senderEp);

                if (received < 20) return null;

                // Validate Binding Response (0x0101) and Magic Cookie
                if (response[0] != 0x01 || response[1] != 0x01) return null;
                if (response[4] != 0x21 || response[5] != 0x12 || response[6] != 0xA4 || response[7] != 0x42) return null;

                // Parse attributes
                int offset = 20;
                while (offset + 4 <= received)
                {
                    int attrType = (response[offset] << 8) | response[offset + 1];
                    int attrLen = (response[offset + 2] << 8) | response[offset + 3];
                    offset += 4;

                    if (offset + attrLen > received) break;

                    // XOR-MAPPED-ADDRESS (0x0020)
                    if (attrType == 0x0020 && attrLen >= 8)
                    {
                        int family = response[offset + 1];
                        if (family == 0x01) // IPv4
                        {
                            int xorPort = (response[offset + 2] << 8) | response[offset + 3];
                            int port = xorPort ^ 0x2112;

                            byte[] ipBytes = new byte[4];
                            ipBytes[0] = (byte)(response[offset + 4] ^ 0x21);
                            ipBytes[1] = (byte)(response[offset + 5] ^ 0x12);
                            ipBytes[2] = (byte)(response[offset + 6] ^ 0xA4);
                            ipBytes[3] = (byte)(response[offset + 7] ^ 0x42);

                            IPAddress pubIp = new IPAddress(ipBytes);
                            string natType = (port == localPort && localPort != 0) ? "Full-Cone NAT (Port Preserved)" : "NAT / CGNAT (Port Remapped)";

                            return new StunResult
                            {
                                Success = true,
                                PublicIp = pubIp,
                                PublicPort = port,
                                NatTypeDescription = natType
                            };
                        }
                    }
                    // MAPPED-ADDRESS (0x0001) fallback
                    else if (attrType == 0x0001 && attrLen >= 8)
                    {
                        int family = response[offset + 1];
                        if (family == 0x01)
                        {
                            int port = (response[offset + 2] << 8) | response[offset + 3];
                            byte[] ipBytes = new byte[4];
                            Buffer.BlockCopy(response, offset + 4, ipBytes, 0, 4);

                            return new StunResult
                            {
                                Success = true,
                                PublicIp = new IPAddress(ipBytes),
                                PublicPort = port,
                                NatTypeDescription = "MAPPED-ADDRESS"
                            };
                        }
                    }

                    // Attributes are padded to multiple of 4
                    offset += (attrLen + 3) & ~3;
                }
            }

            return null;
        }
    }
}
