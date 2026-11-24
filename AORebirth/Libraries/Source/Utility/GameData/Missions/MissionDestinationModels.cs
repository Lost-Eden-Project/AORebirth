namespace Utility.GameData.Missions
{
    using System;

    /// <summary>Captured WorldPos representation for one exact placement; not an operational entrance key.</summary>
    public sealed class MissionDestinationWorldPosition
    {
        internal MissionDestinationWorldPosition(MissionPlacementIdentity identity, MissionWorldPositionData data)
        {
            Identity = identity;
            PlayfieldIdentityType = data.PlayfieldIdentityType;
            WorldOffsetX = data.WorldOffsetX;
            WorldOffsetZ = data.WorldOffsetZ;
        }

        public MissionPlacementIdentity Identity { get; }
        public uint PlayfieldIdentityType { get; }
        public int WorldOffsetX { get; }
        public int WorldOffsetZ { get; }
    }

    /// <summary>The complete client placement identity; names and coordinates are not identity.</summary>
    public readonly struct MissionPlacementIdentity : IEquatable<MissionPlacementIdentity>
    {
        public MissionPlacementIdentity(uint identityType, uint identityInstance)
        {
            IdentityType = identityType;
            IdentityInstance = identityInstance;
        }

        public uint IdentityType { get; }
        public uint IdentityInstance { get; }

        public bool Equals(MissionPlacementIdentity other)
        {
            return IdentityType == other.IdentityType && IdentityInstance == other.IdentityInstance;
        }

        public override bool Equals(object obj)
        {
            return obj is MissionPlacementIdentity && Equals((MissionPlacementIdentity)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return ((int)IdentityType * 397) ^ (int)IdentityInstance; }
        }

        public override string ToString()
        {
            return IdentityType.ToString("X8") + ":" + IdentityInstance.ToString("X8");
        }
    }

    public sealed class MissionEntrancePlacement
    {
        internal MissionEntrancePlacement(PlacementData data)
        {
            Identity = new MissionPlacementIdentity(data.IdentityType, data.IdentityInstance);
            PlayfieldId = data.PlayfieldId;
            LocalX = (float)data.LocalX;
            LocalY = (float)data.LocalY;
            LocalZ = (float)data.LocalZ;
            LocalXBits = data.LocalXBits;
            LocalYBits = data.LocalYBits;
            LocalZBits = data.LocalZBits;
            RotationComponent0 = (float)data.RotationComponent0;
            RotationComponent1 = (float)data.RotationComponent1;
            RotationComponent2 = (float)data.RotationComponent2;
            RotationComponent3 = (float)data.RotationComponent3;
            DisplayName = data.DisplayName;
            RawNameHex = data.RawNameHex;
        }

        /// <summary>
        /// The placement of a Dynels.dat dynel the entrance catalog does not hold (a door an ACG dungeon is built
        /// behind), shaped as a catalog row would be: <paramref name="headingX"/>..<paramref name="headingW"/> are the
        /// Dynels reader's XYZW heading, stored as the catalog's raw WXYZ components.
        /// </summary>
        public static MissionEntrancePlacement FromPlacedDynel(uint identityType, uint identityInstance, int playfieldId,
            float x, float y, float z, float headingX, float headingY, float headingZ, float headingW, string displayName)
        {
            if (identityType == 0 || identityInstance == 0)
                throw new ArgumentException("A complete placement identity is required.");
            if (playfieldId <= 0)
                throw new ArgumentOutOfRangeException(nameof(playfieldId));
            return new MissionEntrancePlacement(new PlacementData
            {
                IdentityType = identityType,
                IdentityInstance = identityInstance,
                PlayfieldId = playfieldId,
                LocalX = x,
                LocalY = y,
                LocalZ = z,
                LocalXBits = BitConverter.ToUInt32(BitConverter.GetBytes(x), 0),
                LocalYBits = BitConverter.ToUInt32(BitConverter.GetBytes(y), 0),
                LocalZBits = BitConverter.ToUInt32(BitConverter.GetBytes(z), 0),
                RotationComponent0 = headingW,
                RotationComponent1 = headingX,
                RotationComponent2 = headingY,
                RotationComponent3 = headingZ,
                DisplayName = displayName ?? string.Empty,
                RawNameHex = string.Empty
            });
        }

        public MissionPlacementIdentity Identity { get; }
        public uint IdentityType { get { return Identity.IdentityType; } }
        public uint IdentityInstance { get { return Identity.IdentityInstance; } }
        public int PlayfieldId { get; }
        public float LocalX { get; }
        public float LocalY { get; }
        public float LocalZ { get; }
        public uint LocalXBits { get; }
        public uint LocalYBits { get; }
        public uint LocalZBits { get; }
        public float RotationComponent0 { get; }
        public float RotationComponent1 { get; }
        public float RotationComponent2 { get; }
        public float RotationComponent3 { get; }
        public string DisplayName { get; }
        public string RawNameHex { get; }
    }
}
