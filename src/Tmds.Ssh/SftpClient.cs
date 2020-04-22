using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Buffers;

namespace Tmds.Ssh
{
    abstract class SftpOperation
    {
        public abstract ValueTask HandleResponse(SftpPacketType type, ReadOnlySequence<byte> fields);

        protected static Exception CreateExceptionForStatus(ReadOnlySequence<byte> fields)
        {
            return new SshException("TODO"); // TODO: use fields
        }

        protected static Exception CreateExceptionForUnexpectedType(SftpPacketType type)
        {
            return new ProtocolException($"Unexpected response type: {type}");
        }
    }

    public partial class SftpClient : IDisposable
    {
        private readonly ChannelContext _context;
        private readonly Task _receiveLoopTask;
        private int _requestId;
        private ConcurrentDictionary<int, SftpOperation> _operations;
        private Sequence? _readBuffer;

        private int GetNextRequestId()
        {
            return Interlocked.Increment(ref _requestId);
        }

        internal SftpClient(ChannelContext context)
        {
            _context = context;
            _receiveLoopTask = ReceiveLoopAsync();
            _operations = new ConcurrentDictionary<int, SftpOperation>();
        }

        public void Dispose()
        {
            _context?.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                MessageId messageId;
                do
                {
                    using var packet = await _context.ReceivePacketAsync(ct: default).ConfigureAwait(false);
                    messageId = packet.MessageId!.Value;

                    if (messageId == MessageId.SSH_MSG_CHANNEL_DATA)
                    {
                        await HandleChannelData(packet.Move()).ConfigureAwait(false);
                    }
                    else
                    {
                        // Nothing yet
                    }

                } while (messageId != MessageId.SSH_MSG_CHANNEL_CLOSE);

            }
            catch (Exception e)
            {
                throw e;
            }
        }

        private async ValueTask HandleChannelData(Packet packet)
        {
            /*
                byte      SSH_MSG_CHANNEL_DATA
                uint32    recipient channel
                string    data
            */
            using Sequence payload = packet.MovePayload();
            payload.Remove(9);
            while (ReadSftpPacket(payload, out SftpPacketType type, out uint requestId, out ReadOnlySequence<byte> fields, out uint consumed))
            {
                if (_operations.TryGetValue((int)requestId, out SftpOperation operation))
                {
                    await operation.HandleResponse(type, fields);
                }
                payload.Remove(consumed);
            }
        }

        private bool ReadSftpPacket(Sequence payload, out SftpPacketType type, out uint requestId, out ReadOnlySequence<byte> fields, out uint consumed)
        {
            if (payload.IsEmpty)
            {
                type = default;
                requestId = 0;
                fields = default;
                consumed = 0;
                return false;
            }
            /*
                uint32           length        // The length of the entire packet, excluding the length field
                byte             type
                uint32           request-id
                    ... type specific fields ...
            */
            var reader = new SequenceReader(payload);
            uint length = reader.ReadUInt32();
            type = (SftpPacketType)reader.ReadByte();
            requestId = reader.ReadUInt32();
            fields = payload.AsReadOnlySequence().Slice(5, length - 5);
            consumed = 4 + length;
            return true;
        }
    }
}