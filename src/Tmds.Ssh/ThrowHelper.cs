// This file is part of Tmds.Ssh which is released under LGPL-3.0.
// See file LICENSE for full license details.

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Tmds.Ssh
{
    static class ThrowHelper
    {
        [DoesNotReturn]
        public static void ThrowArgumentOutOfRange(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        [DoesNotReturn]
        public static void ThrowInvalidOperation(string message)
        {
            throw new InvalidOperationException(message);
        }

        [DoesNotReturn]
        public static void ThrowProtocolUnexpectedEndOfPacket()
        {
            throw new ProtocolException("Unexpected end of packet.");
        }

        [DoesNotReturn]
        public static void ThrowArgumentNull(string paramName)
        {
            throw new ArgumentNullException(paramName);
        }

        [DoesNotReturn]
        public static void ThrowProtocolInvalidUtf8()
        {
            throw new ProtocolException("Data contains an invalid UTF-8 sequence.");
        }

        [DoesNotReturn]
        public static void ThrowProtocolInvalidAscii()
        {
            throw new ProtocolException("Data contains an invalid ASCII characters.");
        }
    }
}