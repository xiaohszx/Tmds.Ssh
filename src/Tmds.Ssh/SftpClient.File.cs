using System;
using System.Threading.Tasks;
using System.Buffers;

namespace Tmds.Ssh
{
    public class SftpFile
    {
        private byte[] handle;
        internal SftpFile(byte[] handle)
        {
            this.handle = handle;
        }
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
        public async Task<SftpFile> OpenFileAsync(string path, SftpOpenFlags openFlags)
        {
            int requestId = GetNextRequestId();
            var operation = new OpenFileOperation();

            _operations.TryAdd(requestId, operation);

            await _context.SftpOpenFileMessageAsync((UInt32)requestId, path, openFlags, attributes, default);
            return await operation.Task;
        }
    }

    class OpenFileOperation : SftpOperation
    {
        private TaskCompletionSource<SftpFile> _tcs = new TaskCompletionSource<SftpFile>();

        public override ValueTask HandleResponse(SftpPacketType type, ReadOnlySequence<byte> fields)
        {
            if (type == SftpPacketType.SSH_FXP_STATUS)
            {
                _tcs.SetException(CreateExceptionForStatus(fields));
            }
            else if (type == SftpPacketType.SSH_FXP_HANDLE)
            {
                /*
                    byte   SSH_FXP_HANDLE
                    uint32 request-id
                    string handle
                */
                var reader = new SequenceReader(fields);
                byte[] handle = reader.ReadStringAsBytes().ToArray();
                _tcs.SetResult(new SftpFile(handle));
            }
            else
            {
                _tcs.SetException(CreateExceptionForUnexpectedType(type));
            }

            return default;
        }

        public Task<SftpFile> Task => _tcs.Task;
    }
}