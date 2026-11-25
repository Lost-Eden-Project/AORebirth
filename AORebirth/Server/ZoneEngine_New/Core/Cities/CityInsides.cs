namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Concurrent;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// A city house's inside: its own copy of the playfield the template names (CityHouseTemplate_c::GetInsideTemplateInstance,
    /// House_c::GetInsideID), one per house as City.dll keeps one HouseInside_c per house. The copy's id is derived from the
    /// house: <see cref="FirstPlayfield"/> + the house id, the block below the proxy playfields
    /// (<see cref="ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.First"/>), which the dungeon ranges stop short of.
    /// </summary>
    public static class CityInsides
    {
        /// <summary>First city inside id; house ids run below 2^24.</summary>
        public const int FirstPlayfield = 0x7E000000;

        public static bool IsCityInside(int playfieldId)
            => playfieldId >= FirstPlayfield && playfieldId < ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.First;

        public static bool TryCreate(long houseId, out int playfieldId)
        {
            playfieldId = 0;
            if (houseId <= 0 || houseId >= ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.First - FirstPlayfield)
                return false;
            playfieldId = FirstPlayfield + (int)houseId;
            return true;
        }

        public static long HouseOf(int playfieldId) => IsCityInside(playfieldId) ? playfieldId - FirstPlayfield : 0;

        /// <summary>
        /// What an inside stands for: the house, the playfield copied, the city playfield and the house's org, and the
        /// spot in front of the house's door where its exits put a character back.
        /// </summary>
        public sealed record Target(long HouseId, int ResourceId, int CityPlayfieldId, int OrganizationId,
            Vector3 Outside, Quaternion OutsideHeading);

        /// <summary>The city service's answer for a house id (set when cities start); null while cities are off.</summary>
        public static Func<long, Target?>? Describe { get; set; }

        public static bool TryDescribe(int playfieldId, out Target target)
        {
            target = null!;
            long house = HouseOf(playfieldId);
            if (house <= 0 || Describe?.Invoke(house) is not { } found)
                return false;
            target = found;
            return true;
        }

        // A spawned house door (a static dynel, its instance given out at spawn) on a city playfield: where its inside's
        // exits put a character back. Filled and emptied with the doors.
        static readonly ConcurrentDictionary<(int Playfield, int Door), (Vector3 Landing, Quaternion Heading)> s_doors = new();

        public static void AddDoor(int playfieldId, int doorInstance, Vector3 landing, Quaternion heading)
            => s_doors[(playfieldId, doorInstance)] = (landing, heading);

        public static void RemoveDoor(int playfieldId, int doorInstance) => s_doors.TryRemove((playfieldId, doorInstance), out _);

        /// <summary>The way back out to a house door a character recorded as their entrance.</summary>
        public static bool TryGetDoorReturn(int playfieldId, int doorInstance, out Vector3 landing, out Quaternion heading)
        {
            if (s_doors.TryGetValue((playfieldId, doorInstance), out (Vector3 Landing, Quaternion Heading) found))
            {
                landing = found.Landing;
                heading = found.Heading;
                return true;
            }

            landing = null!;
            heading = null!;
            return false;
        }
    }
}
