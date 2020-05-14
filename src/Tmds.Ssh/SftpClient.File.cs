// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using System.Text;
namespace Tmds.Ssh
{
    public sealed class SftpFile
    {
        private readonly byte[] _handle;
        private readonly SftpClient _client;

        internal SftpFile(byte[] handle, SftpClient client)
        {
            _handle = handle;
            _client = client;
        }

        public ValueTask<bool> CloseAsync() => _client.SendCloseHandleAsync(_handle);
        // Temporary, need to figure out access to the packet sending function if these function will be under SftpFile
        internal ValueTask<(int bytesRead, bool eof)> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken ct)
            => _client.ReadAsync(_handle, offset, buffer, ct);
    }

    public enum SftpOpenFlags
    {
        Read = 0x00000001,
        Write = 0x00000002,
        Append = 0x00000004,
        CreateNewOrOpen = 0x00000008,
        Truncate = 0x00000010,
        CreateNew = 0x00000020 | CreateNewOrOpen,
    }

    public partial class SftpClient
    {
        internal async ValueTask<(int bytesRead, bool eof)> ReadAsync(byte[] handle, ulong offset, Memory<byte> buffer, CancellationToken ct)
        {
            using var packet = CreateFileReadMessage(handle, offset, (UInt32)buffer.Length);
            var operation = new ReadFileOperation(buffer);

            await SendRequestAsync(packet.Move(), operation);

            return await operation.Task;

            Packet CreateFileReadMessage(byte[] handle, UInt64 offset, UInt32 len)
            {
                using var packet = _context.RentPacket();
                var writer = packet.GetWriter();
                writer.Reserve(13); // DataHeaderLength

                /*
                    SSH_FXP_READ
                    uint32     id
                    string     handle
                    uint64     offset
                    uint32     len
                */

                writer.WriteSftpPacketType(SftpPacketType.SSH_FXP_READ);
                writer.WriteUInt32(0);
                writer.WriteString(handle);
                writer.WriteUInt64(offset);
                writer.WriteUInt32(len);
                return packet.Move();
            }
        }
        public async ValueTask DownloadAsync(string filename, string destinationFile, CancellationToken ct)
        {
            SftpFile file = await OpenFileAsync(filename, SftpOpenFlags.Read);
            /// get file size from the file handle?
            var buffer = new Memory<byte>(new byte[200]);
            (int bytesRead, bool eof) readState = (0, false);
            ulong offset = 0;
            while (!readState.eof)
            {
                readState = await file.ReadAsync(offset, buffer, default);
                offset += (ulong)readState.bytesRead;
            }
            string str = Encoding.ASCII.GetString(buffer.Span);
            Console.WriteLine(str);
        }

        // TODO add CancellationToken
        public async ValueTask<SftpFile> OpenFileAsync(string path, SftpOpenFlags openFlags)
        {
            using var packet = CreateOpenMessage(path, openFlags);
            var operation = new OpenFileOperation();

            await SendRequestAsync(packet.Move(), operation);

            return await operation.Task;

            Packet CreateOpenMessage(string filename, SftpOpenFlags flags)
            {
                using var packet = _context.RentPacket();
                var writer = packet.GetWriter();
                writer.Reserve(DataHeaderLength);

                /*
                    SSH_FXP_OPEN
                    uint32        id
                    string        filename
                    uint32        pflags
                    ATTRS         attrs
                */

                writer.WriteSftpPacketType(SftpPacketType.SSH_FXP_OPEN);
                writer.WriteUInt32(0);
                writer.WriteString(filename);
                writer.WriteUInt32((int)flags);
                writer.WriteUInt32(0);
                return packet.Move();
            }
        }

        // TODO add CancellationToken
        // This can be used for any handle, so also for directories
        internal async ValueTask<bool> SendCloseHandleAsync(byte[] handle)
        {
            using var packet = CreateCloseMessage(handle);
            var operation = new CloseHandleOperation();

            await SendRequestAsync(packet.Move(), operation);

            return await operation.Task;

            Packet CreateCloseMessage(byte[] handle)
            {
                using var packet = _context.RentPacket();
                var writer = packet.GetWriter();
                writer.Reserve(DataHeaderLength);

                /*
                    SSH_FXP_CLOSE
                    uint32        id
                    string        handle
                */

                writer.WriteSftpPacketType(SftpPacketType.SSH_FXP_CLOSE);
                writer.WriteUInt32(0);
                writer.WriteString(handle);
                return packet.Move();
            }
        }
    }
    sealed class ReadFileOperation : SftpOperation
    {
        private TaskCompletionSource<(int bytesRead, bool eof)> _tcs = new TaskCompletionSource<(int bytesRead, bool eof)>();

        private Memory<byte> _buffer;
        internal ReadFileOperation(Memory<byte> buffer)
        {
            _buffer = buffer;
        }

        public override ValueTask HandleResponse(SftpPacketType type, ReadOnlySequence<byte> fields, SftpClient client)
        {
            //      SSH_FXP_DATA
            //      uint32     id
            //      string     data

            if (type == SftpPacketType.SSH_FXP_DATA)
            {
                var reader = new SequenceReader(fields);
                ReadOnlySequence<byte> data = reader.ReadStringAsBytes(_buffer.Length);
                data.CopyTo(_buffer.Span);
                _tcs.SetResult(((int)data.Length, false));
            }
            // EOF or error
            else if (type == SftpPacketType.SSH_FXP_STATUS)
            {
                (SftpErrorCode errorCode, string? errorMessage) status = ParseStatusFields(fields);
                if (status.errorCode == SftpErrorCode.SSH_FX_EOF)
                {
                    _tcs.SetResult((0, true));
                }
                else
                {
                    _tcs.SetException(CreateExceptionForStatus(status.errorCode, status.errorMessage));
                }
            }
            else
            {
                _tcs.SetException(CreateExceptionForUnexpectedType(type));
            }

            return default;
        }

        public Task<(int bytesRead, bool eof)> Task => _tcs.Task;
    }

    sealed class OpenFileOperation : SftpOperation
    {
        private TaskCompletionSource<SftpFile> _tcs = new TaskCompletionSource<SftpFile>();

        public override ValueTask HandleResponse(SftpPacketType type, ReadOnlySequence<byte> fields, SftpClient client)
        {
            if (type == SftpPacketType.SSH_FXP_STATUS)
            {
                _tcs.SetException(CreateExceptionForStatus(fields));
            }
            else if (type == SftpPacketType.SSH_FXP_HANDLE)
            {
                var handle = ParseHandleFields(fields);
                _tcs.SetResult(new SftpFile(handle, client));
            }
            else
            {
                _tcs.SetException(CreateExceptionForUnexpectedType(type));
            }

            return default;
        }

        public Task<SftpFile> Task => _tcs.Task;

        static byte[] ParseHandleFields(ReadOnlySequence<byte> fields)
        {
            /*
                byte   SSH_FXP_HANDLE
                uint32 request-id

                string handle
            */
            var reader = new SequenceReader(fields);
            byte[] handle = reader.ReadStringAsBytes().ToArray();
            return handle;
        }
    }

    // This can be used for any handle, so also for directories
    sealed class CloseHandleOperation : SftpOperation
    {
        private TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        public override ValueTask HandleResponse(SftpPacketType type, ReadOnlySequence<byte> fields, SftpClient client)
        {
            if (type == SftpPacketType.SSH_FXP_STATUS)
            {
                (SftpErrorCode errorCode, string? errorMessage) status = ParseStatusFields(fields);

                if (status.errorCode == SftpErrorCode.SSH_FX_OK)
                {
                    _tcs.SetResult(true);
                }
                else
                {
                    CreateExceptionForStatus(status.errorCode, status.errorMessage);
                }
            }
            else
            {
                _tcs.SetException(CreateExceptionForUnexpectedType(type));
            }
            return default;
        }

        public Task<bool> Task => _tcs.Task;

    }
}