using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace MineTunnel.Core
{
    public class ReliableUdpTransport : IDisposable
    {
        public static readonly byte[] RudpMagic = new byte[] { 0x52, 0x55, 0x44, 0x50 }; // "RUDP"

        public const byte TypeData = 0x01;
        public const byte TypeAck = 0x02;
        public const byte TypeHeartbeat = 0x03;

        public const int MaxPayloadSize = 1080; // Safe sub-MTU to prevent IP fragmentation
        public const int MaxInFlight = 32;      // Flow control limit

        private readonly object _lock = new object();
        private readonly Action<byte[]> _rawSendFunc;

        // Sender State
        private uint _nextSeqNum = 0;
        private readonly Dictionary<uint, SentPacket> _sentWindow = new Dictionary<uint, SentPacket>();
        private long _smoothedRttTicks = (long)(0.05 * Stopwatch.Frequency); // 50ms default
        private long _rttVarTicks = (long)(0.02 * Stopwatch.Frequency);

        // Receiver State
        private uint _expectedSeqNum = 0;
        private readonly Dictionary<uint, byte[]> _outOfOrderBuffer = new Dictionary<uint, byte[]>();

        private volatile bool _isRunning;
        private Thread _retransmitThread;

        public event Action<byte[]> OnStreamDataReceived;

        private class SentPacket
        {
            public uint SeqNum;
            public byte[] PacketData;
            public long SentTimestampTicks;
            public int RetransmitCount;
        }

        public ReliableUdpTransport(Action<byte[]> rawSendFunc)
        {
            _rawSendFunc = rawSendFunc;
            _isRunning = true;

            _retransmitThread = new Thread(RetransmitLoop)
            {
                IsBackground = true,
                Name = "RUDP_Retransmit"
            };
            _retransmitThread.Start();
        }

        // Slices TCP byte streams into safe <= MaxPayloadSize RUDP frames and delivers reliably
        public void SendStreamBytes(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) return;

            int sent = 0;
            while (sent < count && _isRunning)
            {
                int chunkLen = Math.Min(MaxPayloadSize, count - sent);
                byte[] chunk = new byte[chunkLen];
                Array.Copy(buffer, offset + sent, chunk, 0, chunkLen);

                SendDatagramFrame(chunk);
                sent += chunkLen;
            }
        }

        private void SendDatagramFrame(byte[] payload)
        {
            uint seq;
            byte[] packet;

            lock (_lock)
            {
                while (_sentWindow.Count >= MaxInFlight && _isRunning)
                {
                    Monitor.Wait(_lock, 50);
                }

                if (!_isRunning) return;

                seq = _nextSeqNum++;
                uint cumAck, sackMask;
                ComputeAckState(out cumAck, out sackMask);

                packet = BuildFrame(TypeData, seq, cumAck, sackMask, payload);

                _sentWindow[seq] = new SentPacket
                {
                    SeqNum = seq,
                    PacketData = packet,
                    SentTimestampTicks = Stopwatch.GetTimestamp(),
                    RetransmitCount = 0
                };
            }

            try
            {
                if (_rawSendFunc != null) _rawSendFunc(packet);
            }
            catch { }
        }

        public void SendAckOnly()
        {
            byte[] ackPacket;
            lock (_lock)
            {
                uint cumAck, sackMask;
                ComputeAckState(out cumAck, out sackMask);
                ackPacket = BuildFrame(TypeAck, 0, cumAck, sackMask, null);
            }

            try
            {
                if (_rawSendFunc != null) _rawSendFunc(ackPacket);
            }
            catch { }
        }

        // Called when a raw UDP datagram is received from the peer
        public void ProcessIncomingDatagram(byte[] data)
        {
            if (data == null || data.Length < 19) return;

            // 1. Verify Magic
            if (data[0] != RudpMagic[0] || data[1] != RudpMagic[1] ||
                data[2] != RudpMagic[2] || data[3] != RudpMagic[3])
            {
                return;
            }

            byte type = data[4];
            uint seqNum = ReadUInt32(data, 5);
            uint cumAck = ReadUInt32(data, 9);
            uint sackMask = ReadUInt32(data, 13);
            ushort payloadLen = ReadUInt16(data, 17);

            if (19 + payloadLen > data.Length) return;

            // 2. Process Acknowledgments first (advances sender window)
            ProcessAcks(cumAck, sackMask);

            // 3. Process Data if present
            if (type == TypeData && payloadLen > 0)
            {
                byte[] payload = new byte[payloadLen];
                Array.Copy(data, 19, payload, 0, payloadLen);

                List<byte[]> deliverList = new List<byte[]>();
                bool shouldSendAck = false;

                lock (_lock)
                {
                    if (seqNum < _expectedSeqNum)
                    {
                        // Duplicate packet: already delivered
                        shouldSendAck = true;
                    }
                    else if (seqNum == _expectedSeqNum)
                    {
                        // In-order contiguous packet
                        deliverList.Add(payload);
                        _expectedSeqNum++;

                        // Check if buffered out-of-order packets can now be released
                        while (_outOfOrderBuffer.ContainsKey(_expectedSeqNum))
                        {
                            deliverList.Add(_outOfOrderBuffer[_expectedSeqNum]);
                            _outOfOrderBuffer.Remove(_expectedSeqNum);
                            _expectedSeqNum++;
                        }

                        shouldSendAck = true;
                    }
                    else
                    {
                        // Out-of-order packet: buffer if within reasonable window
                        if (seqNum - _expectedSeqNum <= 64)
                        {
                            if (!_outOfOrderBuffer.ContainsKey(seqNum))
                            {
                                _outOfOrderBuffer[seqNum] = payload;
                            }
                        }
                        shouldSendAck = true;
                    }
                }

                // Deliver contiguous packets to stream listener
                if (deliverList.Count > 0 && OnStreamDataReceived != null)
                {
                    for (int i = 0; i < deliverList.Count; i++)
                    {
                        try { OnStreamDataReceived(deliverList[i]); } catch { }
                    }
                }

                if (shouldSendAck)
                {
                    SendAckOnly();
                }
            }
        }

        private void ProcessAcks(uint cumAck, uint sackMask)
        {
            lock (_lock)
            {
                bool windowAdvanced = false;

                // 1. Process cumulative ACK (removes all packets <= cumAck)
                List<uint> removed = new List<uint>();
                foreach (KeyValuePair<uint, SentPacket> kv in _sentWindow)
                {
                    if (kv.Key <= cumAck)
                    {
                        removed.Add(kv.Key);
                        UpdateRttEstimate(kv.Value.SentTimestampTicks);
                    }
                }

                for (int i = 0; i < removed.Count; i++)
                {
                    _sentWindow.Remove(removed[i]);
                    windowAdvanced = true;
                }

                // 2. Process SACK bitmask (removes selectively received packets beyond cumAck)
                if (sackMask != 0)
                {
                    for (int bit = 0; bit < 32; bit++)
                    {
                        if ((sackMask & (1u << bit)) != 0)
                        {
                            uint sackSeq = cumAck + 1 + (uint)bit;
                            SentPacket p;
                            if (_sentWindow.TryGetValue(sackSeq, out p))
                            {
                                UpdateRttEstimate(p.SentTimestampTicks);
                                _sentWindow.Remove(sackSeq);
                                windowAdvanced = true;
                            }
                        }
                    }
                }

                if (windowAdvanced)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }

        private void UpdateRttEstimate(long sendTicks)
        {
            long sampleTicks = Stopwatch.GetTimestamp() - sendTicks;
            if (sampleTicks <= 0) return;

            // Jacobson / Karn algorithm for RTO estimation
            long err = sampleTicks - _smoothedRttTicks;
            _smoothedRttTicks += err / 8;
            _rttVarTicks += (Math.Abs(err) - _rttVarTicks) / 4;
        }

        private long CalculateRtoTicks()
        {
            long rto = _smoothedRttTicks + 4 * _rttVarTicks;
            long minRto = (long)(0.10 * Stopwatch.Frequency); // 100ms
            long maxRto = (long)(0.60 * Stopwatch.Frequency); // 600ms

            if (rto < minRto) return minRto;
            if (rto > maxRto) return maxRto;
            return rto;
        }

        private void ComputeAckState(out uint cumAck, out uint sackMask)
        {
            cumAck = (_expectedSeqNum == 0) ? 0 : (_expectedSeqNum - 1);
            sackMask = 0;

            for (int bit = 0; bit < 32; bit++)
            {
                uint targetSeq = cumAck + 1 + (uint)bit;
                if (_outOfOrderBuffer.ContainsKey(targetSeq))
                {
                    sackMask |= (1u << bit);
                }
            }
        }

        private void RetransmitLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(30);
                    if (!_isRunning) break;

                    List<byte[]> retransmitList = new List<byte[]>();
                    long now = Stopwatch.GetTimestamp();
                    long rto = CalculateRtoTicks();

                    lock (_lock)
                    {
                        foreach (KeyValuePair<uint, SentPacket> kv in _sentWindow)
                        {
                            SentPacket p = kv.Value;
                            if (now - p.SentTimestampTicks >= rto)
                            {
                                retransmitList.Add(p.PacketData);
                                p.SentTimestampTicks = now;
                                p.RetransmitCount++;
                            }
                        }
                    }

                    for (int i = 0; i < retransmitList.Count; i++)
                    {
                        try { if (_rawSendFunc != null) _rawSendFunc(retransmitList[i]); } catch { }
                    }
                }
                catch (ThreadAbortException) { break; }
                catch { }
            }
        }

        private static byte[] BuildFrame(byte type, uint seq, uint cumAck, uint sackMask, byte[] payload)
        {
            ushort payloadLen = (payload != null) ? (ushort)payload.Length : (ushort)0;
            byte[] frame = new byte[19 + payloadLen];

            frame[0] = RudpMagic[0];
            frame[1] = RudpMagic[1];
            frame[2] = RudpMagic[2];
            frame[3] = RudpMagic[3];
            frame[4] = type;

            WriteUInt32(frame, 5, seq);
            WriteUInt32(frame, 9, cumAck);
            WriteUInt32(frame, 13, sackMask);
            WriteUInt16(frame, 17, payloadLen);

            if (payloadLen > 0)
            {
                Array.Copy(payload, 0, frame, 19, payloadLen);
            }

            return frame;
        }

        private static void WriteUInt32(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)(val >> 24);
            buf[offset + 1] = (byte)(val >> 16);
            buf[offset + 2] = (byte)(val >> 8);
            buf[offset + 3] = (byte)val;
        }

        private static uint ReadUInt32(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) |
                   ((uint)buf[offset + 1] << 16) |
                   ((uint)buf[offset + 2] << 8) |
                   (uint)buf[offset + 3];
        }

        private static void WriteUInt16(byte[] buf, int offset, ushort val)
        {
            buf[offset] = (byte)(val >> 8);
            buf[offset + 1] = (byte)val;
        }

        private static ushort ReadUInt16(byte[] buf, int offset)
        {
            return (ushort)(((int)buf[offset] << 8) | (int)buf[offset + 1]);
        }

        public void Dispose()
        {
            _isRunning = false;
            lock (_lock)
            {
                Monitor.PulseAll(_lock);
            }

            if (_retransmitThread != null && _retransmitThread.IsAlive)
            {
                try { _retransmitThread.Join(200); } catch { }
            }
        }
    }
}
