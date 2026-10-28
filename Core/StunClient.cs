using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MineTunnel.Core
{
    public class P2PStunResult
    {
        public bool Success { get; set; }
        public IPEndPoint PublicEndpoint { get; set; }
        public IPEndPoint LocalEndpoint { get; set; }
        public string ServerUsed { get; set; }
        public string ErrorMessage { get; set; }

        public P2PStunResult()
        {
            Success = false;
            ErrorMessage = "";
        }
    }

    public static class StunClient
    {
        private static readonly string[] DefaultStunServers = new string[]
        {
            "stun.l.google.com:19302",
            "stun1.l.google.com:19302",
            "stun2.l.google.com:19302",
            "stun.cloudflare.com:3478"
        };

        private static readonly byte[] MagicCookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
        private static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        public static P2PStunResult Query(UdpClient existingClient = null, int timeoutMs = 3000)
        {
            P2PStunResult result = new P2PStunResult();
            bool disposeClient = false;
            UdpClient udp = existingClient;

            try
            {
                if (udp == null)
                {
                    udp = new UdpClient(0);
                    disposeClient = true;
                }

                udp.Client.ReceiveTimeout = timeoutMs;

                try
                {
                    result.LocalEndpoint = (IPEndPoint)udp.Client.LocalEndPoint;
                }
                catch { }

                foreach (string serverEntry in DefaultStunServers)
                {
                    string host = serverEntry;
                    int port = 19302;
                    if (serverEntry.Contains(":"))
                    {
                        string[] parts = serverEntry.Split(':');
                        host = parts[0];
                        int.TryParse(parts[1], out port);
                    }

                    try
                    {
                        // Resolve server host to IP for source validation
                        IPAddress[] addresses = Dns.GetHostAddresses(host);
                        if (addresses == null || addresses.Length == 0) continue;

                        IPEndPoint targetServerEp = new IPEndPoint(addresses[0], port);

                        byte[] transId;
                        byte[] req = BuildBindingRequest(out transId);
                        udp.Send(req, req.Length, targetServerEp);

                        IPEndPoint actualSourceEp = new IPEndPoint(IPAddress.Any, 0);
                        byte[] resp = udp.Receive(ref actualSourceEp);

                        IPEndPoint mapped = ParseBindingResponse(resp, transId, targetServerEp, actualSourceEp);
                        if (mapped != null)
                        {
                            result.Success = true;
                            result.PublicEndpoint = mapped;
                            result.ServerUsed = serverEntry;
                            return result;
                        }
                    }
                    catch
                    {
                        // Try next STUN server on error or timeout
                    }
                }

                result.ErrorMessage = "No validated response from public STUN servers.";
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
            finally
            {
                if (disposeClient && udp != null)
                {
                    try { udp.Close(); } catch { }
                }
            }
        }

        public static byte[] BuildBindingRequest()
        {
            byte[] transId;
            return BuildBindingRequest(out transId);
        }

        public static byte[] BuildBindingRequest(out byte[] transId)
        {
            byte[] packet = new byte[20];
            // Message Type: 0x0001 (Binding Request)
            packet[0] = 0x00;
            packet[1] = 0x01;
            // Message Length: 0x0000
            packet[2] = 0x00;
            packet[3] = 0x00;
            // Magic Cookie: 0x2112A442
            packet[4] = MagicCookie[0];
            packet[5] = MagicCookie[1];
            packet[6] = MagicCookie[2];
            packet[7] = MagicCookie[3];

            // Transaction ID: 12 cryptographically random bytes via RNGCryptoServiceProvider
            transId = new byte[12];
            Rng.GetBytes(transId);
            Array.Copy(transId, 0, packet, 8, 12);

            return packet;
        }

        public static IPEndPoint ParseBindingResponse(byte[] resp)
        {
            return ParseBindingResponse(resp, null, null, null);
        }

        public static IPEndPoint ParseBindingResponse(byte[] resp, byte[] expectedTransId, IPEndPoint expectedServerEndpoint, IPEndPoint actualRemoteEndpoint)
        {
            if (resp == null || resp.Length < 20) return null;

            // 1. Validate Binding Response Type: 0x0101
            int msgType = (resp[0] << 8) | resp[1];
            if (msgType != 0x0101) return null;

            // 2. Validate Magic Cookie (RFC 5389): 0x2112A442
            if (resp[4] != MagicCookie[0] || resp[5] != MagicCookie[1] ||
                resp[6] != MagicCookie[2] || resp[7] != MagicCookie[3])
            {
                return null;
            }

            // 3. Validate Transaction ID match if expectedTransId provided
            if (expectedTransId != null && expectedTransId.Length == 12)
            {
                byte[] respTransId = new byte[12];
                Array.Copy(resp, 8, respTransId, 0, 12);
                if (!CryptoSecurity.FixedTimeEquals(expectedTransId, respTransId))
                {
                    // Forged or unrelated STUN response: discard
                    return null;
                }
            }

            // 4. Validate source endpoint if expected endpoint provided
            if (expectedServerEndpoint != null && actualRemoteEndpoint != null)
            {
                if (!expectedServerEndpoint.Address.Equals(actualRemoteEndpoint.Address))
                {
                    // Response arrived from unexpected IP: discard
                    return null;
                }
            }

            // 5. Parse Attributes with strict boundary checks
            int msgLength = (resp[2] << 8) | resp[3];
            int pos = 20;
            int limit = Math.Min(resp.Length, 20 + msgLength);

            IPEndPoint xorMapped = null;
            IPEndPoint standardMapped = null;

            while (pos + 4 <= limit)
            {
                int attrType = (resp[pos] << 8) | resp[pos + 1];
                int attrLen = (resp[pos + 2] << 8) | resp[pos + 3];
                pos += 4;

                if (pos + attrLen > limit) break;

                if (attrType == 0x0020 && attrLen >= 8) // XOR-MAPPED-ADDRESS (RFC 5389)
                {
                    byte family = resp[pos + 1];
                    if (family == 0x01) // IPv4
                    {
                        int rawXPort = (resp[pos + 2] << 8) | resp[pos + 3];
                        int port = rawXPort ^ 0x2112;

                        byte[] ipBytes = new byte[4];
                        ipBytes[0] = (byte)(resp[pos + 4] ^ MagicCookie[0]);
                        ipBytes[1] = (byte)(resp[pos + 5] ^ MagicCookie[1]);
                        ipBytes[2] = (byte)(resp[pos + 6] ^ MagicCookie[2]);
                        ipBytes[3] = (byte)(resp[pos + 7] ^ MagicCookie[3]);

                        xorMapped = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
                else if (attrType == 0x0001 && attrLen >= 8) // MAPPED-ADDRESS (RFC 3489)
                {
                    byte family = resp[pos + 1];
                    if (family == 0x01) // IPv4
                    {
                        int port = (resp[pos + 2] << 8) | resp[pos + 3];
                        byte[] ipBytes = new byte[4];
                        Array.Copy(resp, pos + 4, ipBytes, 0, 4);

                        standardMapped = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }

                // Attributes are padded to 4-byte boundaries
                int remainder = attrLen % 4;
                int padding = (remainder == 0) ? 0 : (4 - remainder);
                pos += attrLen + padding;
            }

            return xorMapped != null ? xorMapped : standardMapped;
        }
    }
}
