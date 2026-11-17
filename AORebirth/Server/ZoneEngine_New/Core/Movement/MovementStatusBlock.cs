namespace ZoneEngine_New.Core.Movement
{
    using System;
    using System.Buffers.Binary;

    /// <summary>
    /// The movement block a SimpleCharFullUpdate sends, laid out as retail sends it: 0x2a bytes for a player (header
    /// 0x80, the velocity in the first three floats when there is one, the state bytes at 0xc..0x15, the last speed mode
    /// at 0x19, the speed factor at 0x1a) and 0x1c for an NPC. The client reads the velocity, the state bytes and the
    /// last speed mode (<c>LostEden.Protocol.GameData.CharMovementStatus</c>); the rest rides along as captured.
    /// </summary>
    public struct MovementStatusBlock
    {
        public const int Size = 0x19;
        public const int NpcWireSize = 0x1c;
        public const int PlayerWireSize = 0x2a;

        public byte Header;
        public byte ModeId;
        public byte FwdState;
        public byte FwdDir;
        public byte StrafeState;
        public byte StrafeDir;
        public byte ElevateState;
        public byte ElevateDir;
        public byte TurnState;
        public byte TurnDir;
        public byte JumpState;
        public byte LastSpeedMode;
        public float VelocityX;
        public float VelocityY;
        public float VelocityZ;
        public float PlayerSpeedFactor;

        public static MovementStatusBlock FromBytes(ReadOnlySpan<byte> data)
        {
            if (data.Length < Size)
                throw new ArgumentException($"Expected at least {Size} bytes.", nameof(data));

            var result = new MovementStatusBlock
            {
                Header = data[0x00],
                ModeId = data[0x0c],
                FwdState = data[0x0d],
                FwdDir = data[0x0e],
                StrafeState = data[0x0f],
                StrafeDir = data[0x10],
                ElevateState = data[0x11],
                ElevateDir = data[0x12],
                TurnState = data[0x13],
                TurnDir = data[0x14],
                JumpState = data[0x15],
                VelocityX = ReadSingle(data, 0x00),
                VelocityY = ReadSingle(data, 0x04),
                VelocityZ = ReadSingle(data, 0x08)
            };
            if (data.Length >= 0x1a)
                result.LastSpeedMode = data[0x19];
            if (data.Length >= PlayerWireSize)
                result.PlayerSpeedFactor = ReadSingle(data, 0x1a);
            return result;
        }

        /// <summary>The block as the wire carries it (the movement bytes of the full update).</summary>
        public byte[] ToBytes()
        {
            var data = new byte[Header == 0x80 ? PlayerWireSize : NpcWireSize];
            data[0x00] = Header;
            if (VelocityX != 0f || VelocityY != 0f || VelocityZ != 0f)
            {
                WriteSingle(data, 0x00, VelocityX);
                WriteSingle(data, 0x04, VelocityY);
                WriteSingle(data, 0x08, VelocityZ);
            }

            if (data.Length >= PlayerWireSize && PlayerSpeedFactor != 0f)
                WriteSingle(data, 0x1a, PlayerSpeedFactor);
            data[0x0c] = ModeId;
            data[0x0d] = FwdState;
            data[0x0e] = FwdDir;
            data[0x0f] = StrafeState;
            data[0x10] = StrafeDir;
            data[0x11] = ElevateState;
            data[0x12] = ElevateDir;
            data[0x13] = TurnState;
            data[0x14] = TurnDir;
            data[0x15] = JumpState;
            data[0x19] = LastSpeedMode;
            return data;
        }

        static float ReadSingle(ReadOnlySpan<byte> data, int offset)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4)));

        static void WriteSingle(byte[] data, int offset, float value)
            => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
    }
}
