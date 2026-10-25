namespace ZoneEngine_New.Core.Characters
{
    using System.Collections.Generic;

    using ZoneEngine_New.Core.Data;

    public sealed class CharacterHydrationResult
    {
        public CharacterRecord Character { get; init; } = null!;

        public IReadOnlyList<StatRecord> Stats { get; init; } = [];

        public IReadOnlyList<ItemInstanceRecord> Items { get; init; } = [];

        public IReadOnlyList<int> UploadedNanoIds { get; init; } = [];

        /// <summary>Stored NCU. Entries whose deadline already passed are dropped on apply.</summary>
        public IReadOnlyList<ActiveNanoRecord> ActiveNanos { get; init; } = [];

        /// <summary>Stored LockSkill cooldowns. Expired entries are dropped on apply.</summary>
        public IReadOnlyList<SkillLockRecord> SkillLocks { get; init; } = [];

        /// <summary>Trained perk ids from charactersperks.</summary>
        public IReadOnlyList<int> TrainedPerkIds { get; init; } = [];

        /// <summary>Stored LockPerk cooldowns (StatId = perk id). Expired entries are dropped on apply.</summary>
        public IReadOnlyList<SkillLockRecord> PerkLocks { get; init; } = [];

        /// <summary>Personal research tiers in progress from characterresearch.</summary>
        public IReadOnlyList<ResearchProgressRecord> Research { get; init; } = [];

        /// <summary>Stored nano line cooldowns (StatId = nano strain). Expired entries are dropped on apply.</summary>
        public IReadOnlyList<SkillLockRecord> NanoLineLocks { get; init; } = [];

        /// <summary>Stored /pet rename names per pet type from characterpetnames.</summary>
        public IReadOnlyList<PetNameRecord> PetNames { get; init; } = [];

        public bool IsSpawnReady => CharacterHydrationValidator.Validate(this).IsValid;
    }
}
