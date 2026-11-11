namespace ZoneEngine_New.Core.Mobs
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    using SmokeLounge.AOtomation.Messaging.GameData;

    public sealed class MobItemTableEntry
    {
        public string Hash { get; set; } = string.Empty;

        public int Repeats { get; set; }

        public int Chance { get; set; }

        public int LevelMod { get; set; }

        /// <summary>
        /// The row drops whatever the mob's (or mission's) level: items outside the LevelMod window are not filtered out,
        /// they drop at the rolled QL clamped into their own range.
        /// </summary>
        public bool IgnoreLevel { get; set; }

        /// <summary>
        /// Percent of the mob's (or mission's) level the row's quality centres on: 100 the level itself, 50 half of it
        /// (a level 50 mission's row drops QL 25). The LevelMod window is taken around that quality.
        /// </summary>
        public int QualityPercent { get; set; } = 100;

        /// <summary>Highest <see cref="QualityPercent"/> a row may ask for.</summary>
        public const int MaxQualityPercent = 1000;

        /// <summary>
        /// A row a loot roll can use: a hash, at least one repeat, a chance from 1 to 100, no negative level window and a
        /// quality percent from 1 to <see cref="MaxQualityPercent"/>.
        /// </summary>
        public static bool IsWellFormed(MobItemTableEntry? entry)
            => entry != null && !string.IsNullOrWhiteSpace(entry.Hash) && entry.Repeats > 0
                && entry.Chance is > 0 and <= 100 && entry.LevelMod >= 0
                && entry.QualityPercent is > 0 and <= MaxQualityPercent;

        /// <summary>The level this row's quality centres on for a mob (or mission) of <paramref name="level"/>.</summary>
        public int QualityLevel(int level)
            => QualityPercent == 100 ? level : Math.Max(1, (int)Math.Round(level * QualityPercent / 100.0, MidpointRounding.AwayFromZero));

        public static List<MobItemTableEntry> Clone(IEnumerable<MobItemTableEntry>? source)
        {
            var copy = new List<MobItemTableEntry>();
            if (source == null)
                return copy;
            foreach (MobItemTableEntry entry in source)
                copy.Add(new MobItemTableEntry { Hash = entry.Hash, Repeats = entry.Repeats, Chance = entry.Chance, LevelMod = entry.LevelMod, IgnoreLevel = entry.IgnoreLevel, QualityPercent = entry.QualityPercent });
            return copy;
        }
    }

    /// <summary>
    /// Resolved NPC template used at spawn. Materialized from NpcTemplates.json.
    /// </summary>
    public sealed class MobTemplate
    {
        public bool HasHeadMesh { get; set; }

        public string Hash { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int TemplateId { get; set; }

        /// <summary>
        /// Placeholder leaf used when a spawn hash is not in <c>NpcTemplates.json</c>.
        /// </summary>
        public const string FallbackHash = "AAAA";

        /// <summary>Per-band stats copied from NpcTemplates.json.</summary>
        public Dictionary<int, int> Stats { get; set; } = new();

        /// <summary>
        /// Behaviour switches from the level entry's "Features" (plus NoCombat for the unresolved placeholder and for
        /// blue-named NPCs).
        /// </summary>
        public HashSet<NpcFeature> Features { get; set; } = new();

        public bool Has(NpcFeature feature) => Features.Contains(feature);

        public int MinLevel { get; set; }

        public int MaxLevel { get; set; }

        /// <summary>Historical evidence metadata is preserved but does not authorize runtime activation.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("ContentAcceptance")]
        public JsonElement? ContentProvenance { get; set; }

        public bool UnresolvedPlaceholder { get; set; }

        public List<NpcWeaponVariant> WeaponVariants { get; set; } = new();

        /// <summary>Per-slot AOID lists from the template Equipment jagged array.</summary>
        public List<List<int>> Equipment { get; set; } = new();

        /// <summary>SCFU texture overrides keyed by place. Empty omits the texture block.</summary>
        public Dictionary<int, int> Textures { get; set; } = new();

        /// <summary>Opaque SCFU extended texture override payload copied from template data.</summary>
        public List<TextureOverride> TextureOverrides { get; set; } = new();

        /// <summary>Optional raw CorpseFullUpdate template for NPCs whose corpse visuals are not codec-representable.</summary>
        public MobCorpseFullUpdateTemplate? CorpseFullUpdateTemplate { get; set; }

        public int KnuBotId { get; set; }

        public List<MobItemTableEntry> ItemTable { get; set; } = new();

        public string BinaryListData { get; set; } = string.Empty;

        public JsonElement? BinaryList { get; set; }
    }

    public sealed class MobCorpseFullUpdateTemplate
    {
        public byte[] PacketTemplate { get; set; } = [];

        public int MessageId { get; set; }

        public int MessageIdOffset { get; set; } = -1;

        public int PacketLengthOffset { get; set; } = -1;

        public int SenderInstanceOffset { get; set; } = -1;

        public int ReceiverInstanceOffset { get; set; } = -1;

        public int CorpseInstanceOffset { get; set; } = -1;

        public int PositionXOffset { get; set; } = -1;

        public int PositionYOffset { get; set; } = -1;

        public int PositionZOffset { get; set; } = -1;

        public int PlayfieldIdOffset { get; set; } = -1;

        public int DeadNpcInstanceOffset { get; set; } = -1;

        public int CatMeshOffset { get; set; } = -1;

        public int CashOffset { get; set; } = -1;

        public int MonsterDataOffset { get; set; } = -1;

        public int TailDeadNpcInstanceOffset { get; set; } = -1;
    }
}
