namespace ZoneEngine_New.Core.Quests.Dungeons.Static
{
    using System;
    using System.Buffers.Binary;

    using AODB.Common.RDBObjects;

    using LostEden.Protocol.GameData;

    /// <summary>
    /// Doors the ACG builds a dungeon behind (Lower Scheol's mausoleum entrance, Adonis' Pantheons): stock's placement
    /// carries the building generator under the door's own identity, the way an ACG entrance's does. Other doors carry
    /// none, so the placement data alone says which doors are static dungeon entrances.
    /// </summary>
    public static class AcgBuildingDoors
    {
        /// <summary>The inline building identity type (0xC7A1) the generator is written under, big-endian as the client reads it.</summary>
        const uint BuildingIdentityType = DungeonLayoutGenerator.BuildingIdentityType;

        /// <summary>True when <paramref name="door"/> is a door whose placement carries the ACG building for its own instance.</summary>
        public static bool CarriesOwnBuilding(PlayfieldDynel door)
        {
            ArgumentNullException.ThrowIfNull(door);
            if (door.IdentityType != (int)IdentityType.Door || door.Blob is not { Length: >= 8 } blob)
                return false;

            ReadOnlySpan<byte> bytes = blob;
            uint instance = unchecked((uint)door.IdentityInstance);
            for (int i = 0; i + 8 <= bytes.Length; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4)) == BuildingIdentityType
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i + 4, 4)) == instance)
                    return true;
            }
            return false;
        }
    }
}
