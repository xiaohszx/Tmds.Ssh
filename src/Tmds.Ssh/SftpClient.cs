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

    public class SftpFile
    {
        private byte[] handle;
        internal SftpFile(byte[] handle)
        {
            this.handle = handle;
        }
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
    /*
        Right now operation is for version 3 of the SFTP protocol,
        But how do we handle different SFTP protocols 
        or do we support only one of them?
     */
    class OpenFileOperation : SftpOperation
    {
        private TaskCompletionSource<SftpFile> _tcs = new TaskCompletionSource<SftpFile>();

        public override void HandleResponse(ReadOnlySequence<byte> response)
        {

            /*
                The response to this message will be either SSH_FXP_HANDLE(if the
                operation is successful) or SSH_FXP_STATUS(if the operation fails).
            */

            var reader = new SequenceReader(response);
            var type = (SftpPacketType)reader.ReadByte();
            if (type == SftpPacketType.SSH_FXP_STATUS)
            {
                // Handle failure??? Should I throw an exception 
                // or return an class with a flag or empty handle?
                Console.WriteLine("FAILURE !!!");
                _tcs.SetResult(new SftpFile(Array.Empty<byte>()));
            }
            else if (type == SftpPacketType.SSH_FXP_HANDLE)
            {
                reader.SkipUInt32();
                byte[] handle = reader.ReadStringAsBytes().ToArray();
                _tcs.SetResult(new SftpFile(handle));

            }
            throw new SshException("This probably shouldn't ever happen"); // or should?
        }

        public Task<SftpFile> Task => _tcs.Task;
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

            await _context.SftpOpenDirMessageAsync((UInt32)requestId, directory, default);

            return await operation.Task;
        }
        public async Task<SftpFile> OpenFileAsync(string path, FileOpenFlags openFlags, FileAttributes attributes)
        {
            int requestId = GetNextRequestId();
            var operation = new OpenFileOperation();

            _operations.TryAdd(requestId, operation);

            await _context.SftpOpenFileMessageAsync((UInt32)requestId, path, openFlags, attributes, default);
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
                    messageId = packet.MessageId!.Value;

                    if (messageId == MessageId.SSH_MSG_CHANNEL_DATA)
                    {
                        HandleChannelData(packet);
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
            // assumes the packet in message is only one right now
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
    public enum FileOpenFlags : Int32
    {
        READ = 0x00000001,
        WRITE = 0x00000002,
        APPEND = 0x00000004,
        CREAT = 0x00000008,
        TRUNC = 0x00000010,
        EXCL = 0x00000020,
    }
    // TODO how to handle this?
    public struct FileAttributes
    {
        /*
        FLAGS
        #define SSH_FILEXFER_ATTR_SIZE          0x00000001
        #define SSH_FILEXFER_ATTR_UIDGID        0x00000002
        #define SSH_FILEXFER_ATTR_PERMISSIONS   0x00000004
        #define SSH_FILEXFER_ATTR_ACMODTIME     0x00000008
        #define SSH_FILEXFER_ATTR_EXTENDED      0x80000000
         */

        public UInt32 flags;
        public UInt64 size;           //   present only if flag SSH_FILEXFER_ATTR_SIZE
        public UInt32 uid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        public UInt32 gid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        public UInt32 permissions;    //   present only if flag SSH_FILEXFER_ATTR_PERMISSIONS
        public UInt32 atime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        public UInt32 mtime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        public UInt32 extended_count; //   present only if flag SSH_FILEXFER_ATTR_EXTENDED

        string?[] extensions; // Type/Data Tuples
        // string   extended_type;
        // string   extended_data;
        // ...      more extended data (extended_type - extended_data pairs),
        //            so that number of pairs equals extended_count
        internal FileAttributes(ref SequenceReader reader)
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

            extensions = new string[extended_count];

            for (int i = 0; i < extended_count; i++)
            {
                extensions[i] = reader.ReadUtf8String();
            }
        }

        // Todo think how the hell let people create one
        // lets keep it simple now?
        public FileAttributes(UInt32 flags)
        {
            this.flags = flags;
            size = uid = gid = permissions = atime = mtime = extended_count = 0;
            extensions = Array.Empty<string>();
        }
    }
}