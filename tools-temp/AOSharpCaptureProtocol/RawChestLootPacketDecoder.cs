namespace AORebirth.CaptureProtocol
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>Offline recovered chest/inventory grammar. Neutral fields are preserved, never assigned gameplay meaning.</summary>
    internal static class RawChestLootPacketDecoder
    {
        internal const int ChestKey = 0x465A5D73;
        internal const int InventoryKey = 0x4E536976;
        internal const int GenericKey = 0x52526858;
        internal const int MoveKey = 0x5469373F;
        internal const int AddKey = 0x47537A24;

        internal static Dictionary<string, object> DecodePacket(byte[] packet, bool allowRecordedOutgoingZeroLength = false)
        {
            if (packet == null)
                throw new ArgumentNullException("packet");
            if (packet.Length < 29)
                throw new InvalidDataException("The source packet lacks a complete N3 prefix.");
            var reader = new Reader(packet, 16);
            int key = reader.Int32();
            int declaredLength = (packet[6] << 8) | packet[7];
            // Recorder outbound callbacks retain the pre-send header with size zero.
            // Only the measured GenericCmd/Move requests admit this explicit capture
            // form. Keep its source bytes; never replace the header or pretend it matched.
            bool outgoingZero = allowRecordedOutgoingZeroLength && declaredLength == 0
                && (key == GenericKey || key == MoveKey);
            if (declaredLength != packet.Length && !outgoingZero)
                throw new InvalidDataException("Frame length does not match the complete source packet.");
            var result = new Dictionary<string, object>
            {
                { "PacketKey", key }, { "PacketKeyHex", "0x" + key.ToString("X8") },
                { "PacketLength", packet.Length }, { "FrameHeaderHex", Hex(packet, 0, 16) },
                { "DeclaredFrameLength", declaredLength },
                { "FrameLengthStatus", outgoingZero ? "CAPTURED_OUTGOING_ZERO_LENGTH" : "MATCHED" },
                { "Identity", reader.Identity() }, { "PassOn", reader.Byte() }
            };
            if ((byte)result["PassOn"] > 1)
                throw new InvalidDataException("Original N3 pass-on marker is neither zero nor one.");
            switch (key)
            {
                case ChestKey:
                    result["Family"] = "ChestFullUpdate";
                    Chest(reader, result);
                    break;
                case InventoryKey:
                    result["Family"] = "InventoryUpdate";
                    Inventory(reader, result);
                    break;
                case GenericKey:
                    result["Family"] = "GenericCmd";
                    result["Temp1"] = reader.Int32();
                    result["Count"] = reader.Int32();
                    result["Action"] = reader.Int32();
                    result["Temp4"] = reader.Int32();
                    result["User"] = reader.Identity();
                    if (reader.Remaining % 8 != 0)
                        throw new InvalidDataException("GenericCmd target identities have an incomplete final identity.");
                    var targets = new List<RawChestIdentity>();
                    while (reader.Remaining > 0)
                        targets.Add(reader.Identity());
                    result["Targets"] = targets;
                    break;
                case MoveKey:
                    result["Family"] = "ClientMoveItemToInventory";
                    result["SourceContainer"] = reader.Identity();
                    result["TargetPlacement"] = reader.Int32();
                    break;
                case AddKey:
                    result["Family"] = "ContainerAddItem";
                    result["SourceContainer"] = reader.Identity();
                    result["Target"] = reader.Identity();
                    result["TargetPlacement"] = reader.Int32();
                    break;
                default:
                    throw new InvalidDataException("No recovered chest-loot decoder for key 0x" + key.ToString("X8") + ".");
            }
            if (reader.Remaining != 0)
                throw new InvalidDataException("Unconsumed source bytes: " + reader.Remaining + ".");
            result["DecodedBytes"] = packet.Length;
            return result;
        }

        private static void Chest(Reader reader, Dictionary<string, object> result)
        {
            // Gamecode+A1661. A parented bag has no absolute coordinate/quaternion.
            result["CommonMarker"] = reader.Expect(11, "common FullUpdate marker");
            RawChestIdentity parent = reader.Identity();
            result["Parent"] = parent;
            result["Coordinate"] = null;
            result["CoordinateBits"] = null;
            result["Heading"] = null;
            result["HeadingBits"] = null;
            if (parent.Type == 0)
            {
                uint[] coordinateBits, headingBits;
                result["Coordinate"] = reader.Singles(3, out coordinateBits);
                result["CoordinateBits"] = coordinateBits;
                result["Heading"] = reader.Singles(4, out headingBits);
                result["HeadingBits"] = headingBits;
            }
            result["PlayfieldId"] = reader.Int32();
            result["NeutralIdentity"] = reader.Identity();
            result["HeaderStatBytes"] = new[] { reader.Byte(), reader.Byte() };
            int encoded = reader.Int32();
            result["EncodedStatCount"] = encoded;
            int count = reader.ScaledCount(encoded, 8, "common stats");
            var stats = new List<RawChestStat>();
            for (int index = 0; index < count; index++)
                stats.Add(new RawChestStat { StatId = reader.Int32(), Value = unchecked((uint)reader.Int32()) });
            result["Stats"] = stats;
            int length = reader.Int32();
            result["OpaqueLength"] = length;
            result["OpaqueHex"] = reader.HexBytes(length);

            // +9F88A -> +A0C87. Recovery001/002 supersedes the old trap-list label:
            // these identities reach Lockable+98 and its authorization consumer.
            result["LockableMarker"] = reader.Expect(2, "lockable marker");
            result["LockableRaw"] = reader.Int32();
            encoded = reader.Int32();
            result["EncodedAuthorizationCount"] = encoded;
            count = reader.ScaledCount(encoded, 8, "lockable authorization identities");
            var identities = new List<RawChestIdentity>();
            for (int index = 0; index < count; index++)
                identities.Add(reader.Identity());
            result["AuthorizationIdentities"] = identities;
            result["ChestMarker"] = reader.Expect(3, "chest marker");
        }

        private static void Inventory(Reader reader, Dictionary<string, object> result)
        {
            // Existing InventoryUpdate contract and Recovery001's byte-verified fixture.
            result["NumberOfSlots"] = reader.Int32();
            result["InventoryFlags"] = reader.Int32();
            int encoded = reader.Int32();
            result["EncodedEntryCount"] = encoded;
            int count = reader.ScaledCount(encoded, 32, "inventory entries");
            var entries = new List<RawChestInventoryEntry>();
            for (int index = 0; index < count; index++)
                entries.Add(new RawChestInventoryEntry
                {
                    Slot = reader.Int32(), Flags = reader.UInt16(), Counter = reader.UInt16(),
                    ItemIdentity = reader.Identity(), LowId = reader.Int32(), HighId = reader.Int32(),
                    Quality = reader.Int32(), Reserved = reader.Int32()
                });
            result["Entries"] = entries;
            result["Owner"] = reader.Identity();
            result["Handle"] = reader.Int32();
            result["OpenResponseFlags"] = reader.Int32();
        }

        private static string Hex(byte[] data, int offset, int count)
            => BitConverter.ToString(data, offset, count).Replace("-", string.Empty).ToLowerInvariant();

        private sealed class Reader
        {
            private readonly byte[] data;
            private int cursor;
            internal Reader(byte[] data, int cursor) { this.data = data; this.cursor = cursor; }
            internal int Remaining { get { return this.data.Length - this.cursor; } }
            internal byte Byte()
            {
                if (this.Remaining < 1)
                    throw new EndOfStreamException("Incomplete chest-loot field at byte " + this.cursor + ".");
                return this.data[this.cursor++];
            }
            internal byte[] Bytes(int length)
            {
                if (length < 0 || length > this.Remaining)
                    throw new EndOfStreamException("Invalid chest-loot extent at byte " + this.cursor + ".");
                var value = new byte[length];
                Buffer.BlockCopy(this.data, this.cursor, value, 0, length);
                this.cursor += length;
                return value;
            }
            internal string HexBytes(int length) { byte[] value = this.Bytes(length); return Hex(value, 0, value.Length); }
            internal ushort UInt16() { return (ushort)((this.Byte() << 8) | this.Byte()); }
            internal int Int32()
            {
                byte[] bytes = this.Bytes(4);
                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                return BitConverter.ToInt32(bytes, 0);
            }
            internal float[] Singles(int count, out uint[] bits)
            {
                var values = new float[count]; bits = new uint[count];
                for (int index = 0; index < count; index++)
                {
                    int raw = this.Int32(); bits[index] = unchecked((uint)raw);
                    values[index] = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
                    if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                        throw new InvalidDataException("Nonfinite original transform at byte " + (this.cursor - 4) + ".");
                }
                return values;
            }
            internal RawChestIdentity Identity() { return new RawChestIdentity { Type = this.Int32(), Instance = this.Int32() }; }
            internal int Expect(int expected, string field)
            {
                int actual = this.Int32();
                if (actual != expected)
                    throw new InvalidDataException("Expected " + field + " " + expected + ", got " + actual + " at byte " + (this.cursor - 4) + ".");
                return actual;
            }
            internal int ScaledCount(int encoded, int stride, string field)
            {
                if (encoded < 1009 || encoded % 1009 != 0)
                    throw new InvalidDataException("Noncanonical scaled " + field + " count " + encoded + ".");
                int count = encoded / 1009 - 1;
                if (count > 30000 || count > this.Remaining / stride)
                    throw new InvalidDataException("Declared " + field + " exceed source bytes.");
                return count;
            }
        }
    }

    internal sealed class RawChestIdentity
    {
        public int Type { get; set; }
        public int Instance { get; set; }
    }
    internal sealed class RawChestStat
    {
        public int StatId { get; set; }
        public uint Value { get; set; }
    }
    internal sealed class RawChestInventoryEntry
    {
        public int Slot { get; set; }
        public ushort Flags { get; set; }
        public ushort Counter { get; set; }
        public RawChestIdentity ItemIdentity { get; set; }
        public int LowId { get; set; }
        public int HighId { get; set; }
        public int Quality { get; set; }
        public int Reserved { get; set; }
    }
}
