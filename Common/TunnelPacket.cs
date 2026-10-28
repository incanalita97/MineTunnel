using System;
using System.IO;
using System.Text;

namespace MineTunnel.Common
{
    public enum PacketType : byte
    {
        RoomAuthRequest = 0x01,
        RoomAuthResponse = 0x02,
        LanBroadcast = 0x03,
        ChannelOpen = 0x04,
        ChannelData = 0x05,
        ChannelClose = 0x06,
        Ping = 0x07,
        Pong = 0x08,
        MemberList = 0x09,
        RelayRegisterHost = 0x20,
        RelayRegisterHostResponse = 0x21,
        RelayJoinClient = 0x22,
        RelayJoinClientResponse = 0x23
    }

    public class TunnelPacket
    {
        public const ushort Magic = 0x4D43; // 'M' 'C'

        public PacketType Type { get; set; }
        public byte[] Payload { get; set; }

        public TunnelPacket(PacketType type, byte[] payload)
        {
            Type = type;
            Payload = payload ?? new byte[0];
        }

        public static void WritePacket(Stream stream, PacketType type, byte[] payload)
        {
            payload = payload ?? new byte[0];
            byte[] header = new byte[7];
            
            // Magic 2 bytes (Big Endian: 0x4D, 0x43)
            header[0] = 0x4D;
            header[1] = 0x43;
            // Type 1 byte
            header[2] = (byte)type;
            // Length 4 bytes (Big Endian)
            int len = payload.Length;
            header[3] = (byte)((len >> 24) & 0xFF);
            header[4] = (byte)((len >> 16) & 0xFF);
            header[5] = (byte)((len >> 8) & 0xFF);
            header[6] = (byte)(len & 0xFF);

            stream.Write(header, 0, 7);
            if (len > 0)
            {
                stream.Write(payload, 0, len);
            }
            stream.Flush();
        }

        public static TunnelPacket ReadPacket(Stream stream)
        {
            byte[] header = new byte[7];
            int totalRead = 0;
            while (totalRead < 7)
            {
                int r = stream.Read(header, totalRead, 7 - totalRead);
                if (r <= 0) return null; // Disconnected
                totalRead += r;
            }

            // Verify Magic
            if (header[0] != 0x4D || header[1] != 0x43)
            {
                throw new InvalidDataException("Invalid packet magic header.");
            }

            PacketType type = (PacketType)header[2];
            int length = ((int)header[3] << 24) | ((int)header[4] << 16) | ((int)header[5] << 8) | (int)header[6];

            if (length < 0 || length > 16 * 1024 * 1024) // 16MB max safety limit
            {
                throw new InvalidDataException("Packet payload length exceeds safety limit: " + length);
            }

            byte[] payload = new byte[length];
            int payloadRead = 0;
            while (payloadRead < length)
            {
                int r = stream.Read(payload, payloadRead, length - payloadRead);
                if (r <= 0) return null;
                payloadRead += r;
            }

            return new TunnelPacket(type, payload);
        }

        #region Payload Builders and Parsers

        public static byte[] BuildAuthRequest(string roomName, string password, string clientName)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(roomName ?? "");
                bw.Write(password ?? "");
                bw.Write(clientName ?? "Player");
                return ms.ToArray();
            }
        }

        public static void ParseAuthRequest(byte[] payload, out string roomName, out string password, out string clientName)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                roomName = br.ReadString();
                password = br.ReadString();
                clientName = br.ReadString();
            }
        }

        public static byte[] BuildAuthResponse(bool success, int clientId, string errorMessage)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(success);
                bw.Write(clientId);
                bw.Write(errorMessage ?? "");
                return ms.ToArray();
            }
        }

        public static void ParseAuthResponse(byte[] payload, out bool success, out int clientId, out string errorMessage)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                success = br.ReadBoolean();
                clientId = br.ReadInt32();
                errorMessage = br.ReadString();
            }
        }

        public static byte[] BuildLanBroadcast(string motd, int port)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(motd ?? "");
                bw.Write(port);
                return ms.ToArray();
            }
        }

        public static void ParseLanBroadcast(byte[] payload, out string motd, out int port)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                motd = br.ReadString();
                port = br.ReadInt32();
            }
        }

        public static byte[] BuildChannelData(int channelId, byte[] data, int offset, int count)
        {
            byte[] payload = new byte[4 + count];
            payload[0] = (byte)((channelId >> 24) & 0xFF);
            payload[1] = (byte)((channelId >> 16) & 0xFF);
            payload[2] = (byte)((channelId >> 8) & 0xFF);
            payload[3] = (byte)(channelId & 0xFF);
            Buffer.BlockCopy(data, offset, payload, 4, count);
            return payload;
        }

        public static void ParseChannelData(byte[] payload, out int channelId, out byte[] data)
        {
            channelId = ((int)payload[0] << 24) | ((int)payload[1] << 16) | ((int)payload[2] << 8) | (int)payload[3];
            int count = payload.Length - 4;
            data = new byte[count];
            Buffer.BlockCopy(payload, 4, data, 0, count);
        }

        public static byte[] BuildChannelOpen(int channelId)
        {
            return new byte[]
            {
                (byte)((channelId >> 24) & 0xFF),
                (byte)((channelId >> 16) & 0xFF),
                (byte)((channelId >> 8) & 0xFF),
                (byte)(channelId & 0xFF)
            };
        }

        public static int ParseChannelId(byte[] payload)
        {
            return ((int)payload[0] << 24) | ((int)payload[1] << 16) | ((int)payload[2] << 8) | (int)payload[3];
        }

        public static byte[] BuildPing(long timestamp)
        {
            return BitConverter.GetBytes(timestamp);
        }

        public static long ParsePing(byte[] payload)
        {
            return BitConverter.ToInt64(payload, 0);
        }

        public static byte[] BuildRelayRegisterHost(string roomName, string password)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(roomName ?? "");
                bw.Write(password ?? "");
                return ms.ToArray();
            }
        }

        public static void ParseRelayRegisterHost(byte[] payload, out string roomName, out string password)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                roomName = br.ReadString();
                password = br.ReadString();
            }
        }

        public static byte[] BuildRelayRegisterResponse(bool success, string message)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(success);
                bw.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static void ParseRelayRegisterResponse(byte[] payload, out bool success, out string message)
        {
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
            {
                success = br.ReadBoolean();
                message = br.ReadString();
            }
        }

        #endregion
    }
}
