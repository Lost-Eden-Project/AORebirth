namespace AORebirth.Tools.RDBDataExtractor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;

    using AODB;
    using AODB.Common.RDBObjects;
    using AORebirth.Core.GameData;

    /// <summary>
    /// Exports the org city data the client reads from its own RDB, so the server uses the same numbers:
    /// <list type="bullet">
    ///   <item>RDB 1000039 (<c>RDBPlayfieldCityData_t</c>, one record per playfield) to CityControllers.json: each city
    ///   controller's purchase and upkeep cost and its buildable zones (City.dll <c>RDBCityController_c::ReadBlob</c>
    ///   1000cf57, ZoneData_c reader 1000d7d1).</item>
    ///   <item>RDB 1000040 (<c>RDBHouseTemplate_t</c>) to HouseTemplates.json: each house's type, upkeep and footprint
    ///   (City.dll <c>operator&gt;&gt;</c> 1000f1a7).</item>
    /// </list>
    /// Both are little-endian after the 12-byte DbObject header. Vector counts are stored as <c>(n + 1) * 1009</c>.
    /// Landing points are read past and not exported.
    /// </summary>
    internal sealed class CityDataExporter
    {
        internal const int CityDataRecordType = 1000039;
        internal const int HouseTemplateRecordType = 1000040;
        const int CountEncoding = 1009;
        const int MaxVectorCount = 30000;
        const int ZoneMaskBytes = 13;

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        readonly RdbController controller;
        readonly string gameDataDirectory;

        internal CityDataExporter(RdbController controller, string gameDataDirectory)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            if (string.IsNullOrWhiteSpace(gameDataDirectory))
                throw new ArgumentException("GameData directory is required.", nameof(gameDataDirectory));
            this.gameDataDirectory = gameDataDirectory;
        }

        internal ExportFileCounts Export(bool overwrite)
        {
            int written = 0;
            int skipped = 0;
            string controllersPath = Path.Combine(this.gameDataDirectory, GameDataPaths.CityControllersFileName);
            string templatesPath = Path.Combine(this.gameDataDirectory, GameDataPaths.HouseTemplatesFileName);
            Directory.CreateDirectory(this.gameDataDirectory);

            if (!overwrite && File.Exists(controllersPath))
            {
                skipped++;
            }
            else
            {
                var playfields = new List<CityPlayfieldRow>();
                foreach (int id in this.Ids(CityDataRecordType))
                {
                    CityPlayfieldRow row = ParseCityData(id, Body(this.controller.GetRaw(CityDataRecordType, id)));
                    row.Name = this.PlayfieldName(id);
                    playfields.Add(row);
                }
                File.WriteAllText(controllersPath, JsonSerializer.Serialize(new CityControllersFile { Playfields = playfields }, JsonOptions));
                Console.WriteLine("exported " + GameDataPaths.CityControllersFileName + " playfields=" + playfields.Count
                    + " controllers=" + playfields.Sum(p => p.Controllers.Count));
                written++;
            }

            if (!overwrite && File.Exists(templatesPath))
            {
                skipped++;
            }
            else
            {
                var templates = new List<HouseTemplateRow>();
                foreach (int id in this.Ids(HouseTemplateRecordType))
                {
                    HouseTemplateRow row = ParseHouseTemplate(id, Body(this.controller.GetRaw(HouseTemplateRecordType, id)));
                    if (row != null)
                        templates.Add(row);
                }

                File.WriteAllText(templatesPath, JsonSerializer.Serialize(new HouseTemplatesFile { Templates = templates }, JsonOptions));
                Console.WriteLine("exported " + GameDataPaths.HouseTemplatesFileName + " templates=" + templates.Count);
                written++;
            }

            return new ExportFileCounts(written, skipped);
        }

        /// <summary>The playfield's name from its RDBPlayfield (1000001) record, for the owners' messages.</summary>
        string PlayfieldName(int playfieldId)
        {
            try
            {
                return this.controller.Get<RDBPlayfield>(playfieldId)?.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        IEnumerable<int> Ids(int recordType)
        {
            if (!this.controller.RecordTypeToId.ContainsKey(recordType))
                throw new InvalidOperationException("RDB record type " + recordType + " was not found.");
            return this.controller.RecordTypeToId[recordType].Keys.OrderBy(id => id).ToArray();
        }

        static byte[] Body(byte[] raw)
        {
            if (raw == null || raw.Length < 12)
                throw new InvalidDataException("RDB record is shorter than its header.");
            return raw.Skip(12).ToArray();
        }

        /// <summary>One RDB 1000039 body (header stripped). Throws unless every byte is consumed.</summary>
        internal static CityPlayfieldRow ParseCityData(int playfieldId, byte[] body)
        {
            var reader = new Reader(body);
            int version = reader.Int32();
            if (version != 1)
                throw new InvalidDataException("City data " + playfieldId + " has version " + version + ".");
            int count = reader.Int32();
            if (count < 0 || count > 0xFFFF)
                throw new InvalidDataException("City data " + playfieldId + " has " + count + " controllers.");
            var playfield = new CityPlayfieldRow
            {
                PlayfieldId = playfieldId,
                TileExtentX = reader.Int32(),
                TileExtentZ = reader.Int32(),
                Controllers = new List<CityControllerRow>(count)
            };
            for (int i = 0; i < count; i++)
            {
                var controllerRow = new CityControllerRow
                {
                    DynelType = reader.Int32(),
                    DynelInstance = unchecked((uint)reader.Int32()),
                    HouseMinLevel = reader.Int16(),
                    HouseMaxLevel = reader.Int16(),
                    PurchaseCost = reader.Int32(),
                    UpkeepCost = reader.Int32(),
                    Zones = new List<CityZoneRow>()
                };
                controllerRow.Index = (int)((controllerRow.DynelInstance >> 16) & 0x3FFF);
                int landingPoints = reader.Count();
                reader.Skip(landingPoints * 16);
                int zones = reader.Count();
                for (int z = 0; z < zones; z++)
                {
                    controllerRow.Zones.Add(new CityZoneRow
                    {
                        ZoneId = reader.UInt16(),
                        Type = reader.Byte(),
                        Mask = Hex(reader.Bytes(ZoneMaskBytes))
                    });
                }

                playfield.Controllers.Add(controllerRow);
            }

            if (reader.Remaining != 0)
                throw new InvalidDataException("City data " + playfieldId + " has " + reader.Remaining + " unread bytes.");
            return playfield;
        }

        /// <summary>
        /// One RDB 1000040 body (header stripped). Version 4 (every town template) and version 1 (the two playfield 6
        /// test templates) are read; the parts after the layout (meshes, doors, guards, advantages, collision) are not
        /// exported. Null for a version this does not know.
        /// </summary>
        internal static HouseTemplateRow ParseHouseTemplate(int id, byte[] body)
        {
            var reader = new Reader(body);
            int version = reader.Int32();
            var row = new HouseTemplateRow { Id = id, Version = version };
            reader.Skip(8); // Identity: 0:0 in every record; the record id names the template.
            if (version == 4)
            {
                row.InsideTemplate = reader.Int32();
                row.FixturesAllowed = reader.Int32();
                int nameLength = reader.Int16();
                row.Name = nameLength > 0 ? Encoding.Latin1.GetString(reader.Bytes(nameLength)) : string.Empty;
                reader.Skip(4);
                row.StealthFactor = reader.Single();
                row.Upkeep = reader.Int32();
                row.HouseType = reader.Int32();
            }
            else if (version == 1)
            {
                // Both version 1 records (393216, 393217): inside, fixtures, name, upkeep, a byte house type.
                row.InsideTemplate = reader.Int32();
                row.FixturesAllowed = reader.Int32();
                int nameLength = reader.Int16();
                row.Name = nameLength > 0 ? Encoding.Latin1.GetString(reader.Bytes(nameLength)) : string.Empty;
                row.Upkeep = reader.Int32();
                row.HouseType = reader.Byte();
            }
            else
            {
                Console.Error.WriteLine("SKIP house template " + id + ": unknown format version " + version);
                return null;
            }

            int bits = reader.Int32();
            row.TilesX = reader.Int32();
            row.TilesZ = reader.Int32();
            if (row.TilesX <= 0 || row.TilesZ <= 0 || bits != row.TilesX * row.TilesZ)
                throw new InvalidDataException("House template " + id + " layout " + bits + " bits for " + row.TilesX + "x" + row.TilesZ + ".");
            row.Layout = Hex(reader.Bytes((bits + 7) / 8));
            return row;
        }

        static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return text.ToString();
        }

        sealed class Reader
        {
            readonly byte[] data;
            int position;

            internal Reader(byte[] data) => this.data = data;

            internal int Remaining => this.data.Length - this.position;

            void Need(int count)
            {
                if (count < 0 || this.position + count > this.data.Length)
                    throw new InvalidDataException("Record ends early at byte " + this.position + ".");
            }

            internal int Int32() { Need(4); int v = BitConverter.ToInt32(this.data, this.position); this.position += 4; return v; }
            internal short Int16() { Need(2); short v = BitConverter.ToInt16(this.data, this.position); this.position += 2; return v; }
            internal int UInt16() { Need(2); int v = BitConverter.ToUInt16(this.data, this.position); this.position += 2; return v; }
            internal int Byte() { Need(1); return this.data[this.position++]; }
            internal float Single() { Need(4); float v = BitConverter.ToSingle(this.data, this.position); this.position += 4; return v; }
            internal void Skip(int count) { Need(count); this.position += count; }

            internal byte[] Bytes(int count)
            {
                Need(count);
                byte[] result = new byte[count];
                Buffer.BlockCopy(this.data, this.position, result, 0, count);
                this.position += count;
                return result;
            }

            internal int Count()
            {
                int encoded = Int32();
                if (encoded % CountEncoding != 0 || encoded / CountEncoding < 1 || encoded / CountEncoding - 1 > MaxVectorCount)
                    throw new InvalidDataException("Bad vector count " + encoded + " at byte " + (this.position - 4) + ".");
                return encoded / CountEncoding - 1;
            }
        }

        internal sealed class CityControllersFile
        {
            public List<CityPlayfieldRow> Playfields { get; set; }
        }

        internal sealed class CityPlayfieldRow
        {
            public int PlayfieldId { get; set; }
            public string Name { get; set; }
            public int TileExtentX { get; set; }
            public int TileExtentZ { get; set; }
            public List<CityControllerRow> Controllers { get; set; }
        }

        internal sealed class CityControllerRow
        {
            public int Index { get; set; }
            public int DynelType { get; set; }
            public uint DynelInstance { get; set; }
            public int HouseMinLevel { get; set; }
            public int HouseMaxLevel { get; set; }
            public int PurchaseCost { get; set; }
            public int UpkeepCost { get; set; }
            public List<CityZoneRow> Zones { get; set; }
        }

        internal sealed class CityZoneRow
        {
            public int ZoneId { get; set; }
            public int Type { get; set; }
            /// <summary>100 bits, 10x10 tiles, lowest bit first, row by row in z (bit = z * 10 + x); 13 bytes hex.</summary>
            public string Mask { get; set; }
        }

        internal sealed class HouseTemplatesFile
        {
            public List<HouseTemplateRow> Templates { get; set; }
        }

        internal sealed class HouseTemplateRow
        {
            public int Id { get; set; }
            public int Version { get; set; }
            public string Name { get; set; }
            public int HouseType { get; set; }
            public int Upkeep { get; set; }
            public int InsideTemplate { get; set; }
            public int FixturesAllowed { get; set; }
            public float StealthFactor { get; set; }
            public int TilesX { get; set; }
            public int TilesZ { get; set; }
            /// <summary>TilesX * TilesZ bits, lowest bit first, row by row in z (bit = z * TilesX + x); hex.</summary>
            public string Layout { get; set; }
        }
    }
}
