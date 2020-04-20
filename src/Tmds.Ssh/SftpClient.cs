using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using Tmds.Ssh;
using System.Text;
using System.IO;
namespace Tmds.Ssh
{
    public class SftpClient : IDisposable
    {
        private ChannelContext _context;
        private readonly SftpSettings _settings;
        private readonly Task _receiveLoopTask;
        private readonly ILogger _logger;
        private int requestId;
        // IAsyncEnumerable<TResult> ListFilesAsync<TResult>(ReadOnlySpan<byte> directory, ListTransform<TResult> transform, ListPredicate? predicate = null);
        // ValueTask<SftpFile> OpenFileAsync(ReadOnlySpan<byte> path, SftpAccessFlags access, SftpOpenFlags openFlags, SetAttributes? setAttributes = null);
        // ValueTask RemoveAsync(ReadOnlySpan<byte> path);
        // ValueTask RenameAsync(ReadOnlySpan<byte> oldpath, ReadOnlySpan<byte> newpath, SftpRenameFlags flags);
        // ValueTask CreateDirectory(ReadOnlySpan<byte> path, SetAttributes? setAttributes = null);
        //
        // delegate TResult <TResult>ListTransform<TResult>(ref DirectoryEntry entry);
        // delegate bool ListPredicate(ref DirectoryEntry entry);
        // delegate void SetAttributes(ref DirectoryEntry entry);

        public async ValueTask<List<string>> ListFilesAsync(string directory) {
            // SSH_FXP_READDIR until SSH_FXP_STATUS if error or SSH_FX_EOF to read all file names, then close the handle 
            await _context.SftpOpenDirMessageAsync(requestId++, directory, CancellationToken.None);
            var handle = await _context.ReceiveDirectoryHandleAsync("Handle receive failed", CancellationToken.None);
            await _context.SftpReadDirMessageAsync(requestId++, handle, CancellationToken.None);
            var names = await _context.SftpReceiveFilenameAsync("Filename receive failure", CancellationToken.None);
            return names;
        }
        internal SftpClient(ChannelContext context, SftpSettings settings, ILogger logger) {
            _context = context;
            _settings = settings;
            _receiveLoopTask = ReceiveLoopAsync();
            _logger = logger;
            requestId = 0;
            // Console.WriteLine($"SFTP Version: {settings.version}");
            // foreach (var extension in settings.extensions)
            // {
            //     Console.WriteLine($"Extension name: {extension.Item1} data: {extension.Item2}");
            // }
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
                        using SftpPacket sftpPacket = ParseSftpPacket(packet);
                        _logger.ReceivedSftp(sftpPacket);
                    }
                    else
                    {
                        // Dunno
                    }

                } while (messageId != MessageId.SSH_MSG_CHANNEL_CLOSE);

                // _readQueue.Writer.Complete();
            }
            catch (Exception e)
            {
                _readQueue.Writer.Complete(e);
            }
        }

        private SftpPacket ParseSftpPacket(Packet packet){

        }
    }
    internal class SftpSettings {
        public readonly uint version; // Negotiated SFTP version
        public readonly List<Tuple<string, string>> extensions; // Tuple of extension-name and extension-data
        internal SftpSettings(uint version, List<Tuple<string, string>> extensions) {
            this.version = version;
            this.extensions = extensions;
        }
    }
    public enum SftpPacketTypes
    {
        SSH_FXP_INIT            = 1,
        SSH_FXP_VERSION         = 2,
        SSH_FXP_OPEN            = 3,
        SSH_FXP_CLOSE           = 4,
        SSH_FXP_READ            = 5,
        SSH_FXP_WRITE           = 6,
        SSH_FXP_LSTAT           = 7,
        SSH_FXP_FSTAT           = 8,
        SSH_FXP_SETSTAT         = 9,
        SSH_FXP_FSETSTAT        = 10,
        SSH_FXP_OPENDIR         = 11,
        SSH_FXP_READDIR         = 12,
        SSH_FXP_REMOVE          = 13,
        SSH_FXP_MKDIR           = 14,
        SSH_FXP_RMDIR           = 15,
        SSH_FXP_REALPATH        = 16,
        SSH_FXP_STAT            = 17,
        SSH_FXP_RENAME          = 18,
        SSH_FXP_READLINK        = 19,
        SSH_FXP_LINK            = 21,
        SSH_FXP_BLOCK           = 22,
        SSH_FXP_UNBLOCK         = 23,
        
        SSH_FXP_STATUS          = 101,
        SSH_FXP_HANDLE          = 102,
        SSH_FXP_DATA            = 103,
        SSH_FXP_NAME            = 104,
        SSH_FXP_ATTRS           = 105,
         
        SSH_FXP_EXTENDED        = 200,
        SSH_FXP_EXTENDED_REPLY  = 201,
    }

    ref struct FileAttributes {
        /*
        FLAGS

        #define SSH_FILEXFER_ATTR_SIZE          0x00000001
        #define SSH_FILEXFER_ATTR_UIDGID        0x00000002
        #define SSH_FILEXFER_ATTR_PERMISSIONS   0x00000004
        #define SSH_FILEXFER_ATTR_ACMODTIME     0x00000008
        #define SSH_FILEXFER_ATTR_EXTENDED      0x80000000
         */

        UInt32   flags;
        UInt64   size;           //   present only if flag SSH_FILEXFER_ATTR_SIZE
        UInt32   uid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        UInt32   gid;            //   present only if flag SSH_FILEXFER_ATTR_UIDGID
        UInt32   permissions;    //   present only if flag SSH_FILEXFER_ATTR_PERMISSIONS
        UInt32   atime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        UInt32   mtime;          //   present only if flag SSH_FILEXFER_ACMODTIME
        UInt32   extended_count; //   present only if flag SSH_FILEXFER_ATTR_EXTENDED

        List<Tuple<string, string>> extensions; // Type/Data Tuples
        // string   extended_type;
        // string   extended_data;
        // ...      more extended data (extended_type - extended_data pairs),
        //            so that number of pairs equals extended_count

        public FileAttributes(ref SequenceReader reader){
            flags = reader.ReadUInt32();
            size = uid = gid = permissions = atime = mtime = extended_count = 0;
            if((flags & 0x00000001) == 0x00000001) {
                size = reader.ReadUInt64();
            }
            if((flags & 0x00000002) == 0x00000002) {
                uid = reader.ReadUInt32();
                gid = reader.ReadUInt32();
            }
            if((flags & 0x00000004) == 0x00000004) {
                permissions = reader.ReadUInt32();
            }
            if((flags & 0x00000008) == 0x00000008) {
                atime = reader.ReadUInt32();
                mtime = reader.ReadUInt32();
            }
            if((flags & 0x80000000) == 0x80000000) {
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