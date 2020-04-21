using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using Tmds.Ssh;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using System.Buffers;

namespace Tmds.Ssh
{
    abstract class SftpOperation
    {
        public abstract void HandleResponse(ReadOnlySequence<byte> response);
    }

    class SftpFile
    {
        private SftpFile(byte[] handle) { }
    }

    class OpenDirOperation : SftpOperation
    {
        private TaskCompletionSource<byte[]> _tcs = new TaskCompletionSource<byte[]>();

        public override void HandleResponse(ReadOnlySequence<byte> response)
        {
            /*
                   byte   SSH_FXP_HANDLE
            uint32 request-id
            string handle
            */

            var reader = new SequenceReader(response);
            byte type = reader.ReadByte();
            reader.SkipUInt32();
            byte[] handle = reader.ReadStringAsBytes().ToArray();
            _tcs.SetResult(handle);
        }

        public Task<byte[]> Task => _tcs.Task;
    }

    public class SftpClient : IDisposable
    {
        private ChannelContext _context;
        private readonly SftpSettings _settings;
        private readonly Task _receiveLoopTask;
        private readonly ILogger _logger;
        private int _requestId;
        private ConcurrentDictionary<int, SftpOperation> _operations;

        private int GetNextRequestId()
        {
            return Interlocked.Increment(ref _requestId);
        }

        // ValueTask<SftpFile> OpenFileAsync(ReadOnlySpan<byte> path, SftpAccessFlags access, SftpOpenFlags openFlags, SetAttributes? setAttributes = null);
        public async Task<byte[]> OpenDirAsync(string directory)
        {
            int requestId = GetNextRequestId();

            var operation = new OpenDirOperation();

            _operations.TryAdd(requestId, operation);

            // SSH_FXP_READDIR until SSH_FXP_STATUS if error or SSH_FX_EOF to read all file names, then close the handle 
            await _context.SftpOpenDirMessageAsync(requestId, directory, CancellationToken.None);
            // await _context.SftpReadDirMessageAsync(requestId++, "\x00\x00\x00\x00", CancellationToken.None);

            return await operation.Task;
        }

        internal SftpClient(ChannelContext context, SftpSettings settings, ILogger logger)
        {
            _context = context;
            _settings = settings;
            _receiveLoopTask = ReceiveLoopAsync();
            _logger = logger;
            _requestId = 0;
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
                    // SshContext ReceivePacket should already handle failures and window adjustments
                    messageId = packet.MessageId!.Value;

                    if (messageId == MessageId.SSH_MSG_CHANNEL_DATA)
                    {
                        HandleChannelData(packet);
                        

                        // using var sftpPacket = new SftpPacket(packet.MovePayload());
                        // _logger.Received(sftpPacket); // TODO packet might not live long enough to be printed ??
                    }
                    else
                    {
                        // Nothing yet
                    }

                } while (messageId != MessageId.SSH_MSG_CHANNEL_CLOSE);

                // _readQueue.Writer.Complete();
            }
            catch (Exception e)
            {
                // _readQueue.Writer.Complete(e);
                throw e;
            }
        }

        private void HandleChannelData(Packet packet)
        {
            /*
                byte      SSH_MSG_CHANNEL_DATA
                uint32    recipient channel
                string    data
            */
            /*
                uint32           length
                byte             type
                uint32           request-id
                    ... type specific fields ...
            */
            var reader = packet.GetReader();
            reader.Skip(9);
            uint length = reader.ReadUInt32();
            byte type = reader.ReadByte();
            uint requestId = reader.ReadUInt32();
            if (_operations.TryGetValue((int)requestId, out SftpOperation operation))
            {
                operation.HandleResponse(packet.Payload.Slice(4 + 9));
            }
        }
    }

    internal class SftpSettings
    {
        public readonly uint version; // Negotiated SFTP version
        public readonly List<Tuple<string, string>> extensions; // Tuple of extension-name and extension-data
        internal SftpSettings(uint version, List<Tuple<string, string>> extensions)
        {
            this.version = version;
            this.extensions = extensions;
        }
    }

    ref struct FileAttributes
    {
        /*
        FLAGS

        #define SSH_FILEXFER_ATTR_SIZE          0x00000001
        #define SSH_FILEXFER_ATTR_UIDGID        0x00000002
        #define SSH_FILEXFER_ATTR_PERMISSIONS   0x00000004
        #define SSH_FILEXFER_ATTR_ACMODTIME     0x00000008
        #define SSH_FILEXFER_ATTR_EXTENDED      0x80000000
         */

        UInt32 flags;
        UInt64 size;           //   present only if flag SSH_FILEXFER_ATTR_SIZE
        UInt32 uid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        UInt32 gid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        UInt32 permissions;    //   present only if flag SSH_FILEXFER_ATTR_PERMISSIONS
        UInt32 atime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        UInt32 mtime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        UInt32 extended_count; //   present only if flag SSH_FILEXFER_ATTR_EXTENDED

        List<Tuple<string, string>> extensions; // Type/Data Tuples
        // string   extended_type;
        // string   extended_data;
        // ...      more extended data (extended_type - extended_data pairs),
        //            so that number of pairs equals extended_count

        public FileAttributes(ref SequenceReader reader)
        {
            flags = reader.ReadUInt32();
            size = uid = gid = permissions = atime = mtime = extended_count = 0;
            if ((flags & 0x00000001) == 0x00000001)
            {
                size = reader.ReadUInt64();
            }
            if ((flags & 0x00000002) == 0x00000002)
            {
                uid = reader.ReadUInt32();
                gid = reader.ReadUInt32();
            }
            if ((flags & 0x00000004) == 0x00000004)
            {
                permissions = reader.ReadUInt32();
            }
            if ((flags & 0x00000008) == 0x00000008)
            {
                atime = reader.ReadUInt32();
                mtime = reader.ReadUInt32();
            }
            if ((flags & 0x80000000) == 0x80000000)
            {
                extended_count = reader.ReadUInt32();
            }

            extensions = new List<Tuple<string, string>>();

            for (int i = 0; i < extended_count; i++)
            {
                string extended_type = reader.ReadUtf8String();
                string extended_data = reader.ReadUtf8String();

                extensions.Add(new Tuple<string, string>(extended_type, extended_data));
            }
        }
    }
}