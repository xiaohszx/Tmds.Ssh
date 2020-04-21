// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Tmds.Ssh
{
    struct SftpPacket : IDisposable
    {
        private const int HeaderOffset = 5; // MessageId + ChannelId
        private const int DataOffset = 9; // HeaderOffset + DataLength
        private uint _payloadLength;  // not sure if I'll need this
        private Sequence _sequence;
        public PacketId Type { get; }
        public uint RequestId { get; }

        // I am unable to get Packets internal sequence because its private, 
        // so I just take the sequence with stripped header and padding from MovePayload()
        // TODO deal with CHANNEL_DATA packet that has multiple of SftpPackets
        public SftpPacket(Sequence packetPayload)
        {
            /*
                byte      SSH_MSG_CHANNEL_DATA
                uint32    recipient channel
                uint32    dataLength
                uint32    firstSftpPacketLength
           */
            _sequence = packetPayload;

            var reader = new SequenceReader(_sequence);
            reader.Skip(HeaderOffset);
            _payloadLength = reader.ReadUInt32();
            _payloadLength = reader.ReadUInt32(); // TODO fix the assumption that the DATA has only one Sftp packet
            Type = (PacketId)reader.ReadByte();
            RequestId = reader.ReadUInt32();
        }

        public ReadOnlySequence<byte> Payload
        {
            get
            {
                if (_sequence == null)
                {
                    return default;
                }

                if (_payloadLength == 0)
                {
                    return default;
                }

                return _sequence.AsReadOnlySequence().Slice(DataOffset, _payloadLength);
            }
        }
        // Think about proper disposing pattern for this
        public void Dispose()
        {
            _sequence.Dispose();
        }
    }
}