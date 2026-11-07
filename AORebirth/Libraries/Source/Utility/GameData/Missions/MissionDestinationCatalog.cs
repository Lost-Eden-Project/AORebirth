namespace Utility.GameData.Missions
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Xml;

    /// <summary>
    /// The mission entrance placements (GameData/Missions/Destinations/MissionEntrancePlacements.json): where each ACG
    /// entrance stands on its playfield, and for entrances a terminal offer was ever captured at, the world offsets the
    /// journal waypoint carries. Immutable after load.
    /// </summary>
    public sealed class MissionDestinationCatalog
    {
        private const string PlacementFile = "MissionEntrancePlacements.json";

        private static readonly IReadOnlyList<MissionEntrancePlacement> EmptyPlacements =
            new ReadOnlyCollection<MissionEntrancePlacement>(new MissionEntrancePlacement[0]);
        private readonly Dictionary<MissionPlacementIdentity, MissionEntrancePlacement> identities =
            new Dictionary<MissionPlacementIdentity, MissionEntrancePlacement>();
        private readonly Dictionary<int, IReadOnlyList<MissionEntrancePlacement>> playfields;
        private readonly Dictionary<MissionPlacementIdentity, MissionDestinationWorldPosition> worldPositions =
            new Dictionary<MissionPlacementIdentity, MissionDestinationWorldPosition>();

        private MissionDestinationCatalog(PlacementData[] placementRows)
        {
            var placementList = new List<MissionEntrancePlacement>();
            foreach (PlacementData row in placementRows)
            {
                ValidatePlacement(row);
                var placement = new MissionEntrancePlacement(row);
                Require(!identities.ContainsKey(placement.Identity), "Duplicate placement identity: " + placement.Identity);
                identities.Add(placement.Identity, placement);
                placementList.Add(placement);
                if (row.WorldPos != null)
                {
                    Require(row.WorldPos.PlayfieldIdentityType == 40016,
                        "Invalid WorldPos playfield identity type: " + placement.Identity);
                    worldPositions.Add(placement.Identity, new MissionDestinationWorldPosition(placement.Identity, row.WorldPos));
                }
            }

            Placements = placementList.AsReadOnly();
            playfields = placementList.GroupBy(x => x.PlayfieldId).ToDictionary(
                x => x.Key, x => (IReadOnlyList<MissionEntrancePlacement>)x.ToList().AsReadOnly());
        }

        public int Count { get { return Placements.Count; } }
        public IReadOnlyList<MissionEntrancePlacement> Placements { get; }

        /// <summary>Loads the placements from the caller's selected GameData root.</summary>
        public static MissionDestinationCatalog Load(string gameDataRoot)
        {
            if (string.IsNullOrWhiteSpace(gameDataRoot))
                throw new ArgumentException("A GameData root is required.", nameof(gameDataRoot));

            string path = Path.Combine(gameDataRoot, "Missions", "Destinations", PlacementFile);
            var placements = Read<PlacementCatalogData>(File.ReadAllBytes(path), PlacementFile);
            Require(placements != null && placements.SchemaVersion == 2 && placements.CatalogKind == "MISSION_ENTRANCE_PLACEMENTS",
                "Unsupported placement schema or catalog kind.");
            Require(placements.Placements != null && placements.Placements.Length > 0, "Placement catalog is empty.");
            return new MissionDestinationCatalog(placements.Placements);
        }

        public bool TryGetWorldPosition(MissionPlacementIdentity identity, out MissionDestinationWorldPosition worldPosition)
        {
            return worldPositions.TryGetValue(identity, out worldPosition);
        }

        public bool TryGetByIdentity(uint identityType, uint identityInstance, out MissionEntrancePlacement placement)
        {
            return identities.TryGetValue(new MissionPlacementIdentity(identityType, identityInstance), out placement);
        }

        public IReadOnlyList<MissionEntrancePlacement> GetPlacementsForPlayfield(int playfieldId)
        {
            IReadOnlyList<MissionEntrancePlacement> result;
            return playfields.TryGetValue(playfieldId, out result) ? result : EmptyPlacements;
        }

        private static void ValidatePlacement(PlacementData row)
        {
            Require(row != null, "Null placement row.");
            Require(row.IdentityType > 0 && row.IdentityInstance > 0, "Invalid complete placement identity.");
            Require(row.PlayfieldId > 0, "Invalid placement playfield.");
            ValidateCoordinate(row.LocalX, row.LocalXBits, "X");
            ValidateCoordinate(row.LocalY, row.LocalYBits, "Y");
            ValidateCoordinate(row.LocalZ, row.LocalZBits, "Z");
            ValidateBinary32(row.RotationComponent0, "rotation 0");
            ValidateBinary32(row.RotationComponent1, "rotation 1");
            ValidateBinary32(row.RotationComponent2, "rotation 2");
            ValidateBinary32(row.RotationComponent3, "rotation 3");
            Require(!string.IsNullOrEmpty(row.DisplayName) && row.RawNameHex != null
                && row.RawNameHex.Length > 0 && row.RawNameHex.Length % 2 == 0 && IsHex(row.RawNameHex, row.RawNameHex.Length),
                "Missing or malformed placement name bytes.");
            var characters = new char[row.RawNameHex.Length / 2];
            for (int i = 0; i < characters.Length; i++)
            {
                characters[i] = (char)byte.Parse(row.RawNameHex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            Require(new string(characters) == row.DisplayName, "Display name differs from byte-preserving raw name.");
        }

        private static void ValidateCoordinate(double value, uint bits, string axis)
        {
            float decoded = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            ValidateBinary32(value, "local " + axis);
            Require(!float.IsNaN(decoded) && !float.IsInfinity(decoded) && value == (double)decoded
                && BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0) == bits,
                "Coordinate number and exact binary32 bits disagree for " + axis + ".");
        }

        private static void ValidateBinary32(double value, string label)
        {
            float single = (float)value;
            Require(!double.IsNaN(value) && !double.IsInfinity(value) && !float.IsNaN(single)
                && !float.IsInfinity(single) && (double)single == value, "Invalid or rounded binary32 " + label + ".");
        }

        private static T Read<T>(byte[] bytes, string label)
        {
            try
            {
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max))
                {
                    var settings = new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 1000000 };
                    return (T)new DataContractJsonSerializer(typeof(T), settings).ReadObject(reader);
                }
            }
            catch (SerializationException ex)
            {
                throw new InvalidDataException("Malformed catalog JSON: " + label, ex);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("Malformed catalog JSON: " + label, ex);
            }
        }

        private static bool IsHex(string value, int length)
        {
            return value != null && value.Length == length && value.All(
                x => (x >= '0' && x <= '9') || (x >= 'a' && x <= 'f') || (x >= 'A' && x <= 'F'));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidDataException(message);
        }
    }
}
