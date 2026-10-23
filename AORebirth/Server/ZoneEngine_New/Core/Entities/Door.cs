namespace ZoneEngine_New.Core.Entities
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

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
        public int Flags { get; private set; }

        /// <summary>
        /// DoorFullUpdate Unknown7 in live ACG dungeons: (room &lt;&lt; 16) | linked room; the exit is 0xFFFF0000
        /// (outside to the entrance room).
        /// </summary>
        public int RoomLink { get; }

        public bool IsOpen => (Flags & OpenFlag) != 0;

        public bool IsLocked => (Flags & LockedFlag) != 0;

        /// <summary>Stat 299, sent while the door is locked so the client lets a lock pick be tried on it.</summary>
        public int LockDifficulty { get; }

        public override MessageBody BuildSpawnMessage()
        {
            static GameTuple<CharacterStat, uint> Stat(CharacterStat id, uint value) => new() { Value1 = id, Value2 = value };
            return new DoorFullUpdateMessage
            {
                Identity = Identity,
                Unknown = 0,
                MsgVersion = 0x0B,
                Identitytype = 0,
                Instance = 0,
                Coordinate = new SmokeLounge.AOtomation.Messaging.GameData.Vector3(Position.xf, Position.yf, Position.zf),
                Heading = new SmokeLounge.AOtomation.Messaging.GameData.Quaternion
                {
                    X = Rotation.xf,
                    Y = Rotation.yf,
                    Z = Rotation.zf,
                    W = Rotation.wf
                },
                Playfield = Playfield?.ClientPlayfieldId ?? 0,
                Unknown1 = new Identity { Type = (IdentityType)0xF424F, Instance = 1 },
                Unknown2 = 0,
                Unknown3 = 0x6F,
                Stats =
                [
                    Stat(CharacterStat.Flags, unchecked((uint)Flags)),
                    Stat(CharacterStat.StaticInstance, (uint)TemplateId),
                    Stat(CharacterStat.ACGItemLevel, 0),
                    Stat(CharacterStat.ACGItemTemplateID, 0),
                    Stat(CharacterStat.ACGItemTemplateID2, 0),
                    Stat(CharacterStat.MultipleCount, 1),
                    Stat(CharacterStat.MetersWalked, 0),
                    Stat(CharacterStat.ExternalPlayfieldInstance, 0),
                    Stat(CharacterStat.ExternalDoorInstance, 0),
                    Stat(CharacterStat.AccessKey, 0),
                    Stat(CharacterStat.DoorFlags, 0),
                    .. (IsLocked && LockDifficulty > 0 ? [Stat(CharacterStat.LockDifficulty, (uint)LockDifficulty)] : Array.Empty<GameTuple<CharacterStat, uint>>())
                ],
                Name = string.Empty,
                Unknown4 = 2,
                Unknown5 = 50,
                Identities = [],
                Unknown6 = 2,
                Unknown7 = RoomLink
            };
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
            locality.Announce(this, StatusMessage(Identity, IsOpen, IsLocked), includeSelf: true);
            return true;
        }

        /// <summary>
        /// DoorStatusUpdate: sets a door the client already has open or closed, locked or unlocked (Gamecode.dll
        /// DoorStatusUpdateIIR_t, read 0x100A010A, applied 0x1009FF34). The body is version 2, locked, open,
        /// AccessKey (stat 195), a flag byte and an identity list; retail sends one per door at zone-in
        /// (every captured door closed and unlocked: 00000002 00 00 00000000 00 03F1).
        /// </summary>
        public static DoorStatusUpdateMessage StatusMessage(Identity door, bool open, bool locked) => new()
        {
            Identity = door,
            Unknown = 0,
            Unknown1 = 2,
            Unknown2 = (byte)(locked ? 1 : 0),
            Unknown3 = (byte)(open ? 1 : 0),
            Unknown4 = 0,
            Unknown5 = 0,
            Unknown6 = []
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

        /// <summary>The last character in contact with the door left it: every client closes it then.</summary>
        public bool CloseAfterTouch()
        {
            if (!IsOpen)
                return false;
            Flags &= ~OpenFlag;
            return true;
        }

        /// <summary>
        /// A Break and Enter item used on the locked door: it unlocks and opens for everyone nearby (the client's
        /// unlock result does both).
        /// </summary>
        public BreakAndEnterResult TryBreakAndEnter(Player player, int pickRating)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!IsLocked || Playfield == null || player.IsDead || !ReferenceEquals(player.Playfield, Playfield)
                || GetEdgeDistanceTo(player) > UseRange)
                return BreakAndEnterResult.Refused;

            if (pickRating < LockDifficulty)
            {
                BreakAndEnterActions.SendFailed(this, player);
                return BreakAndEnterResult.Failed;
            }

            Flags = (Flags & ~LockedFlag) | OpenFlag;
            Playfield.WorldAccess.Instance?.InvalidateLineOfSight();
            Playfield.GetRequiredService<PlayfieldLocality>().Announce(this,
                BreakAndEnterActions.Result(Identity, player.Identity, BreakAndEnterActions.Unlocked), includeSelf: true);
            return BreakAndEnterResult.Unlocked;
        }

        /// <summary>Heading for a door turned <paramref name="yawDegrees"/> about the vertical axis.</summary>
        public static AORebirth.Core.Vector.Quaternion Heading(int yawDegrees)
        {
            double half = yawDegrees * Math.PI / 360.0;
            return new AORebirth.Core.Vector.Quaternion(0, Math.Sin(half), 0, Math.Cos(half));
        }
    }
}
