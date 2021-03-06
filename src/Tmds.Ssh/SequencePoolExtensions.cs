// This file is part of Tmds.Ssh which is released under MIT.
// See file LICENSE for full license details.

using System;

namespace Tmds.Ssh
{
    static class SequencePoolExtensions
    {
        public static Packet RentPacket(this SequencePool sequencePool)
        {
            Sequence sequence = sequencePool.RentSequence();
            return new Packet(sequence);
        }
    }
}