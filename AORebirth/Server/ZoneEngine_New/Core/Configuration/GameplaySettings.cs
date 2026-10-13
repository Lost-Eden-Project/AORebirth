namespace ZoneEngine_New.Core.Configuration
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Config/Gameplay.json: tunable gameplay rules, loaded once at startup. Resolved beside the GameData tree first
    /// (a deployment's own Config folder), else beside the process, where the repo copy is placed by the build.
    /// </summary>
    public sealed class GameplaySettings
    {
        public const int CurrentVersion = 1;

        public const string ConfigFileName = "Gameplay.json";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public int SchemaVersion { get; init; } = CurrentVersion;

        /// <summary>Seconds a successful /stuck locks the Stability skill, and so how long until the next one.</summary>
        public int StuckCooldownSeconds { get; init; } = 300;

        /// <summary>Where /stuck sends a player.</summary>
        public GameplayLocation? StuckLocation { get; init; }

        /// <summary>XP rules.</summary>
        public GameplayXp Xp { get; init; } = new();

        /// <summary>How district-spawned NPCs wander around their spawn point.</summary>
        public GameplayWander Wander { get; init; } = new();

        /// <summary>Rules for a player fighting a player.</summary>
        public GameplayPvp Pvp { get; init; } = new();

        public static string ResolvePath(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            string? parent = Directory.GetParent(Path.GetFullPath(gameDataRoot))?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                string besideGameData = Path.Combine(parent, "Config", ConfigFileName);
                if (File.Exists(besideGameData))
                    return besideGameData;
            }

            return Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);
        }

        public static GameplaySettings Load(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!File.Exists(path))
                throw new FileNotFoundException("Gameplay config was not found.", path);

            GameplaySettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<GameplaySettings>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Gameplay config is invalid: " + path, exception);
            }

            if (settings == null)
                throw new InvalidDataException("Gameplay config is empty: " + path);

            settings.Validate(path);
            return settings;
        }

        void Validate(string path)
        {
            if (SchemaVersion != CurrentVersion)
                throw new InvalidDataException(path + ": schemaVersion must be " + CurrentVersion + " but was " + SchemaVersion + ".");
            if (StuckCooldownSeconds < 0)
                throw new InvalidDataException(path + ": stuckCooldownSeconds must not be negative.");
            if (StuckLocation is not GameplayLocation stuck || !stuck.IsValid)
                throw new InvalidDataException(path + ": stuckLocation needs a positive playfield and finite x, y, z.");
            if (Xp == null || !double.IsFinite(Xp.KillPercentPerLevelDifference) || Xp.KillPercentPerLevelDifference < 0)
                throw new InvalidDataException(path + ": xp.killPercentPerLevelDifference must be a finite, non-negative number.");
            if (!double.IsFinite(Xp.SideBonusPercent) || Xp.SideBonusPercent < 0 || Xp.SideBonusPercent > short.MaxValue / 100d)
                throw new InvalidDataException(path + ": xp.sideBonusPercent must fit the non-negative, hundredths-of-percent InfoPacket field.");
            if (Xp.Caps == null || !Xp.Caps.IsValid)
                throw new InvalidDataException(path + ": xp.caps killXp, quest and research must be finite, non-negative percents.");
            // A wanderer more than the leash range from its spawn point would be sent home mid-stroll.
            if (Wander == null || !float.IsFinite(Wander.Radius) || Wander.Radius < 0
                || Wander.Radius >= Ai.NpcAiRules.MaxLeashRange)
                throw new InvalidDataException(path + ": wander.radius must be 0 (off) up to below the "
                    + Ai.NpcAiRules.MaxLeashRange + " m leash range.");
            if (!double.IsFinite(Wander.MinPauseSeconds) || !double.IsFinite(Wander.MaxPauseSeconds)
                || Wander.MinPauseSeconds < 0 || Wander.MaxPauseSeconds < Wander.MinPauseSeconds)
                throw new InvalidDataException(path + ": wander pause seconds must be non-negative with min <= max.");
            if (Pvp == null || !double.IsFinite(Pvp.DamagePercent) || Pvp.DamagePercent < 0)
                throw new InvalidDataException(path + ": pvp.damagePercent must be a finite, non-negative number.");
            if (!double.IsFinite(Pvp.CritChancePercent) || Pvp.CritChancePercent < 0)
                throw new InvalidDataException(path + ": pvp.critChancePercent must be a finite, non-negative number.");
        }
    }

    /// <summary>
    /// The pvp section of Gameplay.json: a player's attack on a player, its own included (self damage counts). 100 leaves
    /// a value as it is.
    /// </summary>
    public sealed class GameplayPvp
    {
        /// <summary>Percent of its rolled amount that a player's damage to a player deals.</summary>
        public double DamagePercent { get; init; } = 100;

        /// <summary>Percent of a player's critical chance (CriticalIncrease) that applies against a player.</summary>
        public double CritChancePercent { get; init; } = 100;

        static bool IsPvp(Entities.Character? source, Entities.Character target)
            => source is Entities.Player && target is Entities.Player;

        /// <summary>
        /// <paramref name="damage"/> as <paramref name="source"/> deals it to <paramref name="target"/>: scaled in PvP,
        /// rounded half up and never below 1 while there was any.
        /// </summary>
        public int ScaleDamage(Entities.Character? source, Entities.Character target, int damage)
        {
            if (damage <= 0 || DamagePercent == 100 || !IsPvp(source, target))
                return damage;
            if (DamagePercent == 0)
                return 0;

            double scaled = Math.Round(damage * DamagePercent / 100, MidpointRounding.AwayFromZero);
            return (int)Math.Clamp(scaled, 1, int.MaxValue);
        }

        /// <summary>The percent chance to crit, <paramref name="critChance"/>, scaled in PvP.</summary>
        public double ScaleCritChance(Entities.Character source, Entities.Character target, double critChance)
            => IsPvp(source, target) ? critChance * CritChancePercent / 100 : critChance;
    }

    /// <summary>The wander section of Gameplay.json.</summary>
    public sealed class GameplayWander
    {
        /// <summary>Meters around the district spawn point an NPC is scattered and wanders within. 0 turns wandering off.</summary>
        public float Radius { get; init; } = 50f;

        /// <summary>Shortest time an NPC stands still between strolls.</summary>
        public double MinPauseSeconds { get; init; } = 5;

        /// <summary>Longest time an NPC stands still between strolls.</summary>
        public double MaxPauseSeconds { get; init; } = 20;
    }

    /// <summary>The xp section of Gameplay.json.</summary>
    public sealed class GameplayXp
    {
        /// <summary>Flat Side XP bonus for every player's regular kill and quest XP. 0 disables it.</summary>
        public double SideBonusPercent { get; init; }

        /// <summary>
        /// Percent of the victim's KillAward added per level the victim is above the recipient (removed per level below).
        /// </summary>
        public double KillPercentPerLevelDifference { get; init; } = 2.5;

        /// <summary>Per-grant caps, in percent of the current level's XP bar.</summary>
        public GameplayXpCaps Caps { get; init; } = new();
    }

    /// <summary>
    /// The xp.caps section of Gameplay.json: the most one grant may give, in percent of the current level's XP bar
    /// (NextLevelXp; the alien level's bar for alien XP), never below 1.
    /// </summary>
    public sealed class GameplayXpCaps
    {
        public static GameplayXpCaps Default { get; } = new();

        /// <summary>Kill XP, and alien XP.</summary>
        public double KillXp { get; init; } = 10;

        /// <summary>Quest XP rewards.</summary>
        public double Quest { get; init; } = 20;

        /// <summary>
        /// The personal research share of one grant, in percent of the XP the selected research tier needs in all (not of
        /// the level bar). Taken before the XP caps; the rest of the share stays XP.
        /// </summary>
        public double Research { get; init; } = 10;

        internal bool IsValid => IsPercent(KillXp) && IsPercent(Quest) && IsPercent(Research);

        /// <summary><paramref name="percent"/> of <paramref name="levelBar"/>, rounded down, at least 1.</summary>
        public static long Of(double percent, long levelBar)
            => (long)Math.Clamp(Math.Floor(levelBar * percent / 100), 1, int.MaxValue);

        static bool IsPercent(double value) => double.IsFinite(value) && value >= 0;
    }

    /// <summary>A playfield and position in Gameplay.json.</summary>
    public sealed class GameplayLocation
    {
        public int Playfield { get; init; }

        public float X { get; init; }

        public float Y { get; init; }

        public float Z { get; init; }

        public bool IsValid => Playfield > 0 && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    }
}
