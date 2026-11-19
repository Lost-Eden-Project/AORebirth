namespace AORebirth.Interfaces.Persistence.Characters
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Authoritative storage operations used by full gameplay hydration and character/item saves.
    /// Directory/admission ownership remains ICharacterDao. Callers serialize current-session
    /// snapshots and mutations under their existing persistence ownership before invoking writes.
    /// Compound methods commit once; they never acknowledge or replay an indeterminate commit.
    /// </summary>
    public interface ICharacterPersistenceDao
    {
        CharacterStateData LoadCharacter(int characterId);
        IList<CharacterStatData> LoadStats(int characterId);
        IList<PersistedItemData> LoadCarriedItems(int characterId);
        IList<PersistedItemData> LoadBankItems(int characterId);
        IList<PersistedItemData> LoadContainerItems(int containerInstanceId);
        /// <summary>The contents of several containers in one query (bags kept in the bank, read without opening them).</summary>
        IList<PersistedItemData> LoadContainerItems(IList<int> containerInstanceIds);
        IDictionary<int, string> LoadItemNames();
        IList<int> LoadUploadedNanos(int characterId);
        IList<PersistedActiveNanoData> LoadActiveNanos(int characterId);
        IList<PersistedSkillLockData> LoadSkillLocks(int characterId);
        /// <summary>Trained perk ids (charactersperks.PacketId).</summary>
        IList<int> LoadTrainedPerks(int characterId);
        /// <summary>Replaces the character's trained perk ids in one transaction.</summary>
        void SaveTrainedPerks(int characterId, IList<int> perkIds);
        /// <summary>
        /// In one transaction: deletes the <paramref name="removed"/> perk ids and inserts the <paramref name="trained"/>
        /// ones not stored yet. Every other stored perk of the character is left as it is.
        /// </summary>
        void SaveTrainedPerkChanges(int characterId, IList<int> trained, IList<int> removed);
        /// <summary>LockPerk cooldowns (characterperklocks).</summary>
        IList<PersistedPerkLockData> LoadPerkLocks(int characterId);
        /// <summary>Replaces the character's perk locks in one transaction.</summary>
        void SavePerkLocks(int characterId, IList<PersistedPerkLockData> perkLocks);
        /// <summary>Nano line cooldowns (characternanolinelocks).</summary>
        IList<PersistedNanoLineLockData> LoadNanoLineLocks(int characterId);
        /// <summary>Replaces the character's nano line locks in one transaction.</summary>
        void SaveNanoLineLocks(int characterId, IList<PersistedNanoLineLockData> nanoLineLocks);
        /// <summary>Pet names per pet type (characterpetnames).</summary>
        IList<PersistedPetNameData> LoadPetNames(int characterId);
        /// <summary>Replaces the character's pet names in one transaction.</summary>
        void SavePetNames(int characterId, IList<PersistedPetNameData> petNames);
        /// <summary>Names the character gave their bags, per bag item instance (characterbagnames).</summary>
        IList<PersistedBagNameData> LoadBagNames(int characterId);
        /// <summary>Replaces the character's bag names in one transaction.</summary>
        void SaveBagNames(int characterId, IList<PersistedBagNameData> bagNames);
        /// <summary>Personal research tiers in progress (characterresearch).</summary>
        IList<PersistedResearchData> LoadResearch(int characterId);
        /// <summary>Replaces the character's research progress in one transaction.</summary>
        void SaveResearch(int characterId, IList<PersistedResearchData> research);
        /// <summary>
        /// In one transaction: deletes the <paramref name="removedResearchIds"/> rows and inserts or updates the
        /// <paramref name="research"/> rows. Every other stored row of the character is left as it is.
        /// </summary>
        void SaveResearchChanges(int characterId, IList<PersistedResearchData> research, IList<int> removedResearchIds);
        int LeaseItemInstanceIds(int count);
        void SaveLocation(CharacterStateData character, int online);
        void SaveSnapshot(CharacterStateData character, int online, IList<CharacterStatData> stats);
        void SaveStats(int characterId, IList<CharacterStatData> stats);
        /// <summary>
        /// Online write-behind checkpoint. Locks the character row and writes only while it is still
        /// marked online, so a checkpoint can never land after the logout snapshot; returns false when
        /// skipped. A null <paramref name="location"/> leaves the stored location untouched. Never writes Online.
        /// </summary>
        bool SaveOnlineCheckpoint(int characterId, CharacterStateData location, IList<CharacterStatData> stats);
        void InsertItem(PersistedItemData item);
        void UpdateItemLocation(ItemLocationData location);
        void SaveItemLocations(IList<PersistedItemData> inserts, IList<ItemLocationData> locations);
        /// <summary>
        /// <paramref name="activeNanos"/> null means NCU did not change and must not be touched;
        /// a non-null list (including an empty one) replaces the character's stored NCU.
        /// <paramref name="skillLocks"/> follows the same rule for the character's skill locks.
        /// </summary>
        void SaveInventoryAndUploadedNanos(int characterId, IList<PersistedItemData> inserts,
            IList<ItemLocationData> locations, IList<int> uploadedNanoIds,
            IList<PersistedActiveNanoData>? activeNanos = null,
            IList<PersistedSkillLockData>? skillLocks = null);
        void CommitInventoryMutation(CharacterInventoryMutationData mutation);
        // Retains the existing item/credit boundary; this does not implement a new trade feature.
        void CommitItemCredits(ItemCreditMutationData mutation);
        // Retains active-nano/base-stat coupling without moving gameplay nano policy into storage.
        void CommitActiveNanos(IList<CharacterActiveNanoData> characters);
    }

    /// <summary>COMMIT was attempted but its durable outcome was not acknowledged. Never blindly retry.</summary>
    public sealed class CharacterPersistenceCommitOutcomeUnknownException : Exception
    {
        public CharacterPersistenceCommitOutcomeUnknownException(Exception inner)
            : base("Character persistence commit outcome is unknown; authoritative reload is required.", inner) { }
    }

    /// <summary>
    /// The write was rolled back because it contradicts stored rows (a duplicate key: a slot the server believed
    /// empty is occupied). The server's memory and the database disagree, so retrying the same batch can never
    /// succeed; the character must be reloaded from the database.
    /// </summary>
    public sealed class CharacterPersistenceStateConflictException : Exception
    {
        public CharacterPersistenceStateConflictException(Exception inner)
            : base("Character persistence conflicts with stored state; authoritative reload is required.", inner) { }
    }
}
