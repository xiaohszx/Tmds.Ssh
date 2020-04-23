// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
namespace Tmds.Ssh
{
    static class ChannelContextReceiveMessageExtensions
    {
        public static async ValueTask ReceiveChannelOpenConfirmationAsync(this ChannelContext context, CancellationToken ct)
        {
            using var packet = await context.ReceivePacketAsync(ct).ConfigureAwait(false);

            switch (packet.MessageId)
            {
                case MessageId.SSH_MSG_CHANNEL_OPEN_CONFIRMATION:
                    return;
                case MessageId.SSH_MSG_CHANNEL_OPEN_FAILURE:
                    (ChannelOpenFailureReason reason, string description) = ParseChannelOpenFailure(packet);
                    throw new ChannelOpenFailureException(reason, description);
                default:
                    ThrowHelper.ThrowProtocolUnexpectedMessageId(packet.MessageId!.Value);
                    break;
            }

            static (ChannelOpenFailureReason reason, string description) ParseChannelOpenFailure(ReadOnlyPacket packet)
            {
                /*
                    byte      SSH_MSG_CHANNEL_OPEN_FAILURE
                    uint32    recipient channel
                    uint32    reason code
                    string    description in ISO-10646 UTF-8 encoding [RFC3629]
                    string    language tag [RFC3066]
                 */
                var reader = packet.GetReader();
                reader.ReadMessageId(MessageId.SSH_MSG_CHANNEL_OPEN_FAILURE);
                reader.SkipUInt32();
                ChannelOpenFailureReason reason = (ChannelOpenFailureReason)reader.ReadUInt32();
                string description = reader.ReadUtf8String();
                reader.SkipString();
                reader.ReadEnd();

                return (reason, description);
            }
        }

        public static async ValueTask ReceiveChannelRequestSuccessAsync(this ChannelContext context, string failureMessage, CancellationToken ct)
        {
            using var packet = await context.ReceivePacketAsync(ct).ConfigureAwait(false);

            ParseChannelOpenConfirmation(packet, failureMessage);

            static void ParseChannelOpenConfirmation(ReadOnlyPacket packet, string failureMessage)
            {
                var reader = packet.GetReader();
                var msgId = reader.ReadMessageId();
                switch (msgId)
                {
                    case MessageId.SSH_MSG_CHANNEL_SUCCESS:
                        break;
                    case MessageId.SSH_MSG_CHANNEL_FAILURE:
                        throw new ChannelRequestFailed(failureMessage);
                    default:
                        ThrowHelper.ThrowProtocolUnexpectedMessageId(msgId);
                        break;
                }
            }
        }

        public static async ValueTask ReceiveSftpSettingsAsync(this ChannelContext context, string failureMessage, CancellationToken ct)
        {
            using var packet = await context.ReceivePacketAsync(ct).ConfigureAwait(false);

            ParseSftpSettings(packet, failureMessage);
            /*
                        byte            SSH_MSG_CHANNEL_DATA
                        uint32          recipient channel
                        string          data
                        uint32          length
                        byte            type

                Version 6:
                        string          extension-name
                        string          extension-data
            */

            static void ParseSftpSettings(ReadOnlyPacket packet, string failureMessage)
            {
                var reader = packet.GetReader();
                reader.ReadMessageId(MessageId.SSH_MSG_CHANNEL_DATA);
                var channelId = reader.ReadUInt32();
                var dataLength = reader.ReadUInt32();
                if (dataLength < 5) // Not appropriate SSH_FXP_VERSION packet
                    ThrowHelper.ThrowProtocolInvalidPacketLength();

                var sftpPacketLength = reader.ReadUInt32();
                var type = (SftpPacketType)reader.ReadByte();
                var version = reader.ReadUInt32();

                var extensions = new List<Tuple<string, string>>();
                // TODO have a look at how extensions are passed across various SFTP versions (Some use different packet)
                if (version == 3 || version == 6)
                {
                    sftpPacketLength -= 5;
                    while (sftpPacketLength > 0)
                    { // read extensions
                        var extensionName = reader.ReadUtf8String();
                        var extensionData = reader.ReadUtf8String();
                        extensions.Add(new Tuple<string, string>(extensionName, extensionData));

                        // TODO Length returns number of chars, but UTF has variable amount of bytes per char
                        if (extensionData.Length + extensionName.Length + 4 + 4 > sftpPacketLength)
                            ThrowHelper.ThrowProtocolInvalidPacketLength();
                        sftpPacketLength -= (uint)(extensionData.Length + extensionName.Length + 4 + 4); // RHS should be always positive and <= LHS
                    }
                }
            }
        }
    }
}