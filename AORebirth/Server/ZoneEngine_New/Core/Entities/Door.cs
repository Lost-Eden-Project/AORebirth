namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// A door dynel (0xC748), sent as DoorFullUpdate: a door item template (StaticInstance, e.g. 41557) whose Flags
    /// stat is the door's base flags, plus its state bits: the client's IsLocked is
    /// HasFlag(0x40) (Gamecode.dll Door_t vtable +0xFC, 0x10085412) and closing a door clears 0x80
    /// (0x1008541A), so 0x80 is open. Using an unlocked door opens or closes it for everyone nearby; a locked door
    /// refuses (the client already shows "It is locked"), but a Break and Enter item unlocks and opens it.
    /// Wire values are from captured live ACG dungeon doors.
    /// </summary>
    public sealed class Door : Dynel, IUsableDynel, IBreakAndEnterTarget
    {
        public const int OpenFlag = 0x80;

        public const int LockedFlag = 0x40;

        /// <summary>How close a character must be to open or close a door (units).</summary>
        const float UseRange = 5f;

        /// <summary>Chance (percent) a lock pick opens a locked door even when its rating is below LockDifficulty.</summary>
        const int BasePickChancePercent = 10;

        public Door(Identity identity, int templateId, int flags, int roomLink, int lockDifficulty = 0)
            : base(identity)
        {
            TemplateId = templateId;
            Flags = flags;
            RoomLink = roomLink;
            LockDifficulty = Math.Max(0, lockDifficulty);
        }

        /// <summary>Door item template (StaticInstance, stat 23).</summary>
        public int TemplateId { get; }

        /// <summary>Stat 0; see <see cref="OpenFlag"/> and <see cref="LockedFlag"/>.</summary>
        public int Flags
        {
            get => Stats.GetOrZero(CharacterStat.Flags);
            private set => Stats.Set(CharacterStat.Flags, value);
        }

        /// <summary>
        /// DoorFullUpdate Unknown7 in live ACG dungeons: (room &lt;&lt; 16) | linked room; the exit is 0xFFFF0000
        /// (outside to the entrance room).
        /// </summary>
        public int RoomLink { get; }

        public bool IsOpen => (Flags & OpenFlag) != 0;

        public bool IsLocked => (Flags & LockedFlag) != 0;

        /// <summary>Authored open state held until an explicit close/lock; ordinary touch doors default false.</summary>
        public bool CloseInhibited { get; private set; }

        /// <summary>Applies authoritative flags without treating trigger signal bits as door flags.</summary>
        public void ApplyFlags(int flags, bool? closeInhibit = null)
        {
            int previous = Flags;
            bool previousInhibit = CloseInhibited;
            Flags = flags;
            if (closeInhibit is bool inhibit)
                CloseInhibited = inhibit;
            if (((previous ^ flags) & (OpenFlag | LockedFlag)) == 0 && previousInhibit == CloseInhibited)
                return;

            Playfield?.WorldAccess.Instance?.InvalidateLineOfSight();
            AnnounceStatus();
        }

        /// <summary>Stat 299, sent while the door is locked so the client lets a lock pick be tried on it.</summary>
        public int LockDifficulty { get; }

        public override MessageBody BuildSpawnMessage()
        {
            static GameTuple<Stat, int> Stat(CharacterStat id, int value) => new((Stat)id, value);
            return new DoorFullUpdateMessage
            {
                Identity = Identity,
                Unknown = 0,
                Version = 0x0B,
                Owner = Identity.None,
                Position = new LostEden.Protocol.GameData.Vector3(Position.xf, Position.yf, Position.zf),
                Rotation = new LostEden.Protocol.GameData.Quaternion
                {
                    X = Rotation.xf,
                    Y = Rotation.yf,
                    Z = Rotation.zf,
                    W = Rotation.wf
                },
                Playfield = Playfield?.ClientPlayfieldId ?? 0,
                StateMachine = new Identity { Type = (IdentityType)0xF424F, Instance = 1 },
                InventoryId = 0,
                CurrBodyLocation = 0x6F,
                Stats =
                [
                    Stat(CharacterStat.Flags, Flags),
                    Stat(CharacterStat.StaticInstance, TemplateId),
                    Stat(CharacterStat.ACGItemLevel, 0),
                    Stat(CharacterStat.ACGItemTemplateID, 0),
                    Stat(CharacterStat.ACGItemTemplateID2, 0),
                    Stat(CharacterStat.MultipleCount, 1),
                    Stat(CharacterStat.MetersWalked, 0),
                    Stat(CharacterStat.ExternalPlayfieldInstance, 0),
                    Stat(CharacterStat.ExternalDoorInstance, 0),
                    Stat(CharacterStat.AccessKey, 0),
                    Stat(CharacterStat.DoorFlags, 0),
                    .. (IsLocked && LockDifficulty > 0 ? [Stat(CharacterStat.LockDifficulty, LockDifficulty)] : Array.Empty<GameTuple<Stat, int>>())
                ],
                Blob = [],
                Version2 = 2,
                Unknown3 = 50,
                Identities = [],
                Version3 = 2,
                DoorValue = RoomLink
            };
        }

        /// <summary>The full spawn carries flags, but an authored close-inhibit latch needs the status message.</summary>
        public override IEnumerable<MessageBody> BuildSpawnCompanionMessages()
        {
            if (CloseInhibited)
            {
                yield return StatusMessage(Identity, IsOpen, IsLocked, closeInhibit: true);
            }
        }

        /// <summary>Opens or closes the door for a character standing at it, unless it is locked.</summary>
        public bool TryUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (IsLocked || Playfield == null || player.IsDead || !ReferenceEquals(player.Playfield, Playfield)
                || GetEdgeDistanceTo(player) > UseRange)
                return false;

            Flags ^= OpenFlag;
            Playfield.WorldAccess.Instance?.InvalidateLineOfSight();
            var locality = Playfield.GetRequiredService<PlayfieldLocality>();
            locality.Announce(this, BuildSpawnMessage(), includeSelf: true);
            // A client that already has the door ignores a second full update; the status update moves it.
            locality.Announce(this, StatusMessage(Identity, IsOpen, IsLocked, CloseInhibited), includeSelf: true);
            return true;
        }

        /// <summary>
        /// DoorStatusUpdate: sets a door the client already has open or closed, locked or unlocked (Gamecode.dll
        /// DoorStatusUpdateIIR_t, read 0x100A010A, applied 0x1009FF34). The body is version 2, locked, open,
        /// AccessKey (stat 195), a close-inhibit byte and an identity list; retail sends these at zone-in and on
        /// authored state changes. The PF665 capture includes closed/unlocked, open/unlocked and closed/locked
        /// states; its empty identity list is the four-byte encoded count 000003F1.
        /// </summary>
        // A true close-inhibit byte sets the client's latch. False does not clear an already set native latch;
        // explicit locked/closed state must therefore perform a relock rather than relying on that byte alone.
        public static DoorStatusUpdateMessage StatusMessage(Identity door, bool open, bool locked, bool closeInhibit = false) => new()
        {
            Identity = door,
            Unknown = 0,
            Version = 2,
            FlagA = (byte)(locked ? 1 : 0),
            Open = (byte)(open ? 1 : 0),
            Unknown195 = 0,
            FlagC = (byte)(closeInhibit ? 1 : 0),
            Identities = []
        };

        /// <summary>
        /// A character walked into the closed, unlocked door: every client opens it on that contact (Gamecode.dll
        /// Door_t contact handler, 0x1007FAA9), so it is open here too, and new viewers get it open in the spawn.
        /// </summary>
        public bool OpenByTouch()
        {
            if (IsOpen || IsLocked)
                return false;
            Flags |= OpenFlag;
            return true;
        }

        /// <summary>Sends the door's state to everyone who sees it (a change no client saw happen).</summary>
        public void AnnounceStatus()
            => Playfield?.GetRequiredService<PlayfieldLocality>().Announce(this, StatusMessage(Identity, IsOpen, IsLocked, CloseInhibited), includeSelf: true);

        /// <summary>The last character in contact with the door left it: every client closes it then.</summary>
        public bool CloseAfterTouch()
        {
            if (!IsOpen)
                return false;
            Flags &= ~OpenFlag;
            return true;
        }

        /// <summary>
        /// A Break and Enter item used on the locked door: it unlocks and opens for everyone nearby, through the
        /// unlock action result and a DoorStatusUpdate.
        /// </summary>
        public BreakAndEnterResult TryBreakAndEnter(Player player, int pickRating)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!IsLocked || Playfield == null || player.IsDead || !ReferenceEquals(player.Playfield, Playfield)
                || GetEdgeDistanceTo(player) > UseRange)
                return BreakAndEnterResult.Refused;

            // A picker without the skill gets one flat base-chance try at this door: retrying the base chance would open
            // any lock to anyone who spams the pick.
            if (pickRating < LockDifficulty
                && (!_baseChanceTried.Add(player.Identity.Instance) || Random.Shared.Next(100) >= BasePickChancePercent))
            {
                BreakAndEnterActions.SendFailed(this, player);
                return BreakAndEnterResult.Failed;
            }

            Flags = (Flags & ~LockedFlag) | OpenFlag;
            AwardPickXp(player);
            Playfield.WorldAccess.Instance?.InvalidateLineOfSight();
            Playfield.GetRequiredService<PlayfieldLocality>().Announce(this,
                BreakAndEnterActions.Result(Identity, player.Identity, BreakAndEnterActions.Unlocked), includeSelf: true);
            // The action result alone leaves the stock client's door locked ("It is locked" on the next use): the status
            // update unlocks and opens it for everyone who has the door.
            AnnounceStatus();
            // The other doorways between the same two rooms are the same lock to the client: they open with it, and pay
            // no XP of their own.
            if (_twins != null)
                foreach (Door twin in _twins)
                {
                    twin._pickXpPaid = true;
                    if (twin.IsLocked)
                        twin.ApplyFlags((twin.Flags & ~LockedFlag) | OpenFlag);
                }
            return BreakAndEnterResult.Unlocked;
        }

        /// <summary>
        /// The first successful pick of this door pays Gameplay.json xp.doorPickXpPerLevel x the picker's level, counted
        /// no higher than the lock's own level (LockDifficulty: a dungeon door's is the mission QL), so a high level
        /// picking a QL 1 mission's doors earns QL 1 XP. A door an authored trigger locks again does not pay twice, and
        /// neither does the same door rebuilt locked when its dungeon reloads from the seed (<see cref="PaidPicks"/>).
        /// </summary>
        void AwardPickXp(Player player)
        {
            if (_pickXpPaid || Playfield == null)
                return;
            _pickXpPaid = true;
            if (!MarkPaid(player.Identity.Instance, Playfield.Identity.Instance, Identity.Instance))
                return;
            int perLevel = Playfield.GetService<Configuration.GameplaySettings>()?.Xp.DoorPickXpPerLevel ?? 0;
            if (perLevel <= 0)
                return;
            int level = player.Stats.GetOrOne(CharacterStat.Level);
            if (LockDifficulty > 0)
                level = Math.Min(level, LockDifficulty);
            long xp = (long)perLevel * level;
            using (Modules.StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Lockpick, Modules.ModuleHooks.SourceOf(this)))
                player.AwardXp((int)Math.Min(int.MaxValue, xp), XpSource.Quest);
        }

        bool _pickXpPaid;

        /// <summary>Pickers who already had their base-chance try at this door.</summary>
        readonly HashSet<int> _baseChanceTried = new();

        /// <summary>
        /// (character, playfield, door) picks that paid XP, for the life of the process: a new Player is built on every
        /// zone arrival and a dungeon rebuilds its doors from the seed, so neither can remember it. Bounded.
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Character, int Playfield, int Door), byte> PaidPicks = new();

        const int MaxPaidPicks = 200_000;

        static bool MarkPaid(int character, int playfield, int door)
        {
            if (PaidPicks.Count >= MaxPaidPicks)
                PaidPicks.Clear();
            return PaidPicks.TryAdd((character, playfield, door), 0);
        }

        /// <summary>The other doorways between the same two dungeon rooms (they share one lock); null when there are none.</summary>
        List<Door>? _twins;

        internal void AddTwin(Door twin) => (_twins ??= []).Add(twin);

        /// <summary>Heading for a door turned <paramref name="yawDegrees"/> about the vertical axis.</summary>
        public static AORebirth.Core.Vector.Quaternion Heading(int yawDegrees)
        {
            double half = yawDegrees * Math.PI / 360.0;
            return new AORebirth.Core.Vector.Quaternion(0, Math.Sin(half), 0, Math.Cos(half));
        }
    }
}
