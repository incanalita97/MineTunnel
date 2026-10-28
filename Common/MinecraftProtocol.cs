using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MineTunnel.Common
{
    public class MinecraftServerInfo
    {
        public bool IsOnline { get; set; }
        public string ServerType { get; set; } // Vanilla, Paper, Purpur, Spigot, Forge, Fabric, NeoForge, BungeeCord, LAN
        public string VersionName { get; set; }
        public int ProtocolVersion { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public string Motd { get; set; }
        public long PingMs { get; set; }
        public bool IsModded { get; set; }
        public int ModCount { get; set; }

        public MinecraftServerInfo()
        {
            ServerType = "Vanilla";
            VersionName = "Unknown";
            Motd = "";
        }

        public string GetDisplaySummary()
        {
            if (!IsOnline) return "Offline";

            string modStr = IsModded ? string.Format(" | {0} mods", ModCount > 0 ? ModCount.ToString() : "Modded") : "";
            return string.Format("{0} ({1}) | Players: {2}/{3}{4}",
                ServerType, VersionName, CurrentPlayers, MaxPlayers, modStr);
        }
    }

    public static class MinecraftProtocol
    {
        public const string MulticastAddress = "224.0.2.60";
        public const int MulticastPort = 4445;
        public const int DefaultServerPort = 25565;

        public static readonly int[] CommonJavaPorts = new int[] {
            25565, 25566, 25567, 25568, 25569, 25570, 25577
        };

        #region VarInt Protocol Helpers

        public static void WriteVarInt(Stream stream, int value)
        {
            while ((value & -128) != 0)
            {
                stream.WriteByte((byte)((value & 127) | 128));
                value = (int)((uint)value >> 7);
            }
            stream.WriteByte((byte)value);
        }

        public static int ReadVarInt(Stream stream)
        {
            int value = 0;
            int size = 0;
            int current;

            while (true)
            {
                current = stream.ReadByte();
                if (current == -1)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading VarInt.");
                }

                value |= (current & 127) << (size++ * 7);

                if (size > 5)
                {
                    throw new InvalidOperationException("VarInt is too large.");
                }

                if ((current & 128) != 128)
                {
                    break;
                }
            }

            return value;
        }

        public static void WriteString(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteVarInt(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        public static string ReadString(Stream stream)
        {
            int length = ReadVarInt(stream);
            byte[] buffer = new byte[length];
            int readTotal = 0;
            while (readTotal < length)
            {
                int r = stream.Read(buffer, readTotal, length - readTotal);
                if (r <= 0) throw new EndOfStreamException();
                readTotal += r;
            }
            return Encoding.UTF8.GetString(buffer);
        }

        #endregion

        #region LAN Discovery Packets

        public static byte[] BuildLanAnnouncement(string motd, int port)
        {
            string payload = string.Format("[MOTD]{0}[/MOTD][AD]{1}[/AD]", motd, port);
            return Encoding.UTF8.GetBytes(payload);
        }

        public static bool TryParseLanAnnouncement(string rawMessage, out string motd, out int port)
        {
            motd = "";
            port = 0;

            if (string.IsNullOrEmpty(rawMessage)) return false;

            Match motdMatch = Regex.Match(rawMessage, @"\[MOTD\](.*?)\[/MOTD\]");
            Match portMatch = Regex.Match(rawMessage, @"\[AD\](\d+)\[/AD\]");

            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out port))
            {
                motd = motdMatch.Success ? StripColorCodes(motdMatch.Groups[1].Value) : "Minecraft World";
                return true;
            }

            return false;
        }

        public static string StripColorCodes(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text, @"§[0-9a-fk-or]", "", RegexOptions.IgnoreCase);
        }

        #endregion

        #region Universal Server List Ping (Modern 1.7 - 1.21+ & Legacy Fallback)

        public static MinecraftServerInfo PingServer(string host, int port, int timeoutMs = 2000)
        {
            // First attempt Modern Netty VarInt Ping (1.7 - 1.21.x+)
            MinecraftServerInfo info = PingModernServer(host, port, timeoutMs);
            if (info != null && info.IsOnline) return info;

            // Fallback to Legacy Ping (1.4 - 1.6)
            info = PingLegacyServer(host, port, timeoutMs);
            if (info != null && info.IsOnline) return info;

            return new MinecraftServerInfo { IsOnline = false };
        }

        private static MinecraftServerInfo PingModernServer(string host, int port, int timeoutMs)
        {
            MinecraftServerInfo info = new MinecraftServerInfo();
            DateTime start = DateTime.UtcNow;

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    client.NoDelay = true;
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                    client.EndConnect(ar);
                    client.ReceiveTimeout = timeoutMs;
                    client.SendTimeout = timeoutMs;

                    using (NetworkStream stream = client.GetStream())
                    {
                        // 1. Handshake Packet (ID 0x00)
                        using (MemoryStream handshakeMs = new MemoryStream())
                        {
                            WriteVarInt(handshakeMs, 0x00);
                            WriteVarInt(handshakeMs, 47);   // Protocol version
                            WriteString(handshakeMs, host);
                            byte[] portBytes = BitConverter.GetBytes((ushort)port);
                            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);
                            handshakeMs.Write(portBytes, 0, 2);
                            WriteVarInt(handshakeMs, 1);    // State: 1 (status)

                            byte[] handshakeBytes = handshakeMs.ToArray();
                            WriteVarInt(stream, handshakeBytes.Length);
                            stream.Write(handshakeBytes, 0, handshakeBytes.Length);
                        }

                        // 2. Status Request Packet (ID 0x00)
                        WriteVarInt(stream, 1);
                        stream.WriteByte(0x00);

                        // 3. Status Response Packet
                        int packetLength = ReadVarInt(stream);
                        int packetId = ReadVarInt(stream);
                        if (packetId != 0x00) return null;

                        string jsonResponse = ReadString(stream);
                        info.PingMs = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                        info.IsOnline = true;
                        ParseStatusJson(jsonResponse, info);
                        return info;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static MinecraftServerInfo PingLegacyServer(string host, int port, int timeoutMs)
        {
            MinecraftServerInfo info = new MinecraftServerInfo();
            DateTime start = DateTime.UtcNow;

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    client.NoDelay = true;
                    IAsyncResult ar = client.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                    client.EndConnect(ar);
                    client.ReceiveTimeout = timeoutMs;
                    client.SendTimeout = timeoutMs;

                    using (NetworkStream stream = client.GetStream())
                    {
                        // Send 0xFE 0x01 (Legacy Server List Ping for 1.4-1.6)
                        stream.WriteByte(0xFE);
                        stream.WriteByte(0x01);

                        int header = stream.ReadByte();
                        if (header != 0xFF) return null; // Kick/Disconnect packet

                        // Read string length (short big endian)
                        int b1 = stream.ReadByte();
                        int b2 = stream.ReadByte();
                        if (b1 == -1 || b2 == -1) return null;
                        int charCount = (b1 << 8) | b2;

                        byte[] strBytes = new byte[charCount * 2];
                        int readTotal = 0;
                        while (readTotal < strBytes.Length)
                        {
                            int r = stream.Read(strBytes, readTotal, strBytes.Length - readTotal);
                            if (r <= 0) break;
                            readTotal += r;
                        }

                        string response = Encoding.BigEndianUnicode.GetString(strBytes);
                        info.PingMs = (long)(DateTime.UtcNow - start).TotalMilliseconds;

                        // Legacy response format: §1\0<protocol>\0<version>\0<motd>\0<online>\0<max>
                        string[] parts = response.Split('\0');
                        if (parts.Length >= 6)
                        {
                            info.IsOnline = true;
                            info.ProtocolVersion = int.Parse(parts[1]);
                            info.VersionName = parts[2];
                            info.Motd = StripColorCodes(parts[3]);
                            info.CurrentPlayers = int.Parse(parts[4]);
                            info.MaxPlayers = int.Parse(parts[5]);
                            info.ServerType = DetectServerType(info.VersionName, response);
                            return info;
                        }
                    }
                }
            }
            catch
            {
                // Legacy ping failed
            }

            return null;
        }

        private static void ParseStatusJson(string json, MinecraftServerInfo info)
        {
            try
            {
                // 1. Version & Protocol
                Match versionMatch = Regex.Match(json, @"""name""\s*:\s*""([^""]+)""");
                if (versionMatch.Success) info.VersionName = versionMatch.Groups[1].Value;

                Match protocolMatch = Regex.Match(json, @"""protocol""\s*:\s*(\d+)");
                if (protocolMatch.Success) info.ProtocolVersion = int.Parse(protocolMatch.Groups[1].Value);

                // 2. Players
                Match maxMatch = Regex.Match(json, @"""max""\s*:\s*(\d+)");
                if (maxMatch.Success) info.MaxPlayers = int.Parse(maxMatch.Groups[1].Value);

                Match onlineMatch = Regex.Match(json, @"""online""\s*:\s*(\d+)");
                if (onlineMatch.Success) info.CurrentPlayers = int.Parse(onlineMatch.Groups[1].Value);

                // 3. Modded detection (Forge / Fabric / NeoForge)
                if (json.Contains("\"modinfo\"") || json.Contains("\"forgeData\"") || json.Contains("\"modpackData\""))
                {
                    info.IsModded = true;
                    Match modCountMatch = Regex.Match(json, @"""modList""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                    if (modCountMatch.Success)
                    {
                        MatchCollection matches = Regex.Matches(modCountMatch.Groups[1].Value, @"\{[^\}]+\}");
                        info.ModCount = matches.Count;
                    }
                }

                // 4. Server Type Detection
                info.ServerType = DetectServerType(info.VersionName, json);

                // 5. MOTD extraction (handles string or JSON chat components)
                info.Motd = ExtractMotd(json);
            }
            catch
            {
                info.Motd = "Minecraft Java Server";
                info.ServerType = "Java Server";
            }
        }

        public static string DetectServerType(string versionName, string context)
        {
            string lowerName = (versionName ?? "").ToLowerInvariant();
            string lowerContext = (context ?? "").ToLowerInvariant();

            if (lowerName.Contains("purpur") || lowerContext.Contains("purpur")) return "Purpur";
            if (lowerName.Contains("paper") || lowerContext.Contains("paper")) return "Paper";
            if (lowerName.Contains("spigot") || lowerContext.Contains("spigot")) return "Spigot";
            if (lowerName.Contains("bukkit") || lowerContext.Contains("bukkit")) return "Bukkit";
            if (lowerName.Contains("neoforge") || lowerContext.Contains("neoforge")) return "NeoForge";
            if (lowerName.Contains("forge") || lowerContext.Contains("forge") || lowerContext.Contains("fml")) return "Forge";
            if (lowerName.Contains("fabric") || lowerContext.Contains("fabric")) return "Fabric";
            if (lowerName.Contains("quilt") || lowerContext.Contains("quilt")) return "Quilt";
            if (lowerName.Contains("velocity") || lowerContext.Contains("velocity")) return "Velocity";
            if (lowerName.Contains("bungeecord") || lowerContext.Contains("bungeecord")) return "BungeeCord";
            if (lowerName.Contains("mohist") || lowerContext.Contains("mohist")) return "Mohist";
            if (lowerName.Contains("arclight") || lowerContext.Contains("arclight")) return "Arclight";

            return "Vanilla";
        }

        private static string ExtractMotd(string json)
        {
            try
            {
                // Check if description is a direct string: "description": "Welcome"
                Match directMatch = Regex.Match(json, @"""description""\s*:\s*""([^""]*)""");
                if (directMatch.Success)
                {
                    return StripColorCodes(directMatch.Groups[1].Value);
                }

                // If description is an object with "text" and optional "extra"
                StringBuilder sb = new StringBuilder();
                Match descObj = Regex.Match(json, @"""description""\s*:\s*\{([^\{\}]+(?:\{[^\}]+\}[^\{\}]*)*)\}");
                if (descObj.Success)
                {
                    string descContent = descObj.Groups[1].Value;
                    Match mainText = Regex.Match(descContent, @"""text""\s*:\s*""([^""]*)""");
                    if (mainText.Success) sb.Append(mainText.Groups[1].Value);

                    // Extract all "text" values in extra array
                    Match extraMatch = Regex.Match(descContent, @"""extra""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                    if (extraMatch.Success)
                    {
                        MatchCollection subTexts = Regex.Matches(extraMatch.Groups[1].Value, @"""text""\s*:\s*""([^""]*)""");
                        foreach (Match m in subTexts)
                        {
                            sb.Append(m.Groups[1].Value);
                        }
                    }

                    string result = sb.ToString();
                    if (!string.IsNullOrEmpty(result)) return StripColorCodes(result);
                }
            }
            catch { }

            return "Minecraft Java Server";
        }

        #endregion
    }
}
