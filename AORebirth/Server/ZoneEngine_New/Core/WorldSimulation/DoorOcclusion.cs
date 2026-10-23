namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;
    using System.Collections.Generic;

    using N3Lite;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    using AoVector3 = AORebirth.Core.Vector.Vector3;
    using PlayfieldType = ZoneEngine_New.Core.Playfield.Playfield;

    /// <summary>
    /// Doors as line-of-sight blockers, open and closed as every client shows them. Gamecode.dll makes a door
    /// non-repulsive and asks for its contacts (0x1011687B); a character touching a closed, unlocked door opens it
    /// (Door_t contact handler 0x1007FAA9 calls Open, vtable +0xAC). Each client runs this for every character it
    /// sees, and retail sends nothing as doors open and close in play (capture: a character ran through several
    /// doors, only movement followed). A live retail door log shows the door stays open exactly while a character is
    /// in contact and closes as the last one leaves: template 300548's contact reaches 2.70 m out at the same floor,
    /// and every open came at 2.44-2.63 m and every close at 2.69-2.89 m, whether running through (0.42 s open) or
    /// standing in the doorway (10.71 s). So a door here opens on contact and closes when its contact ends; an
    /// arriving client gets each static door's state in a DoorStatusUpdate at zone-in.
    /// <para>
    /// A door is a vertical panel through its position, across its heading. Its template's collision sphere
    /// (DynelCollision.json, the sphere the client's contacts use) gives the panel's height (twice the sphere centre)
    /// and half width (the rest of the radius), and the touch reach (sphere radius plus the player's).
    /// </para>
    /// </summary>
    public sealed class DoorOcclusion
    {
        /// <summary>Seconds between contact checks: how far the server may trail a client's door.</summary>
        const double TouchIntervalSeconds = 0.1;

        readonly List<Blocker> _blockers = new();
        readonly List<Character> _nearby = new();
        readonly float _touchRadius;
        readonly float _touchCenterY;
        double _sinceTouch;

        public DoorOcclusion(float playerRadius, float playerCenterY)
        {
            _touchRadius = playerRadius;
            _touchCenterY = playerCenterY;
        }

        public int Count => _blockers.Count;

        /// <summary>Raised when a door changes state, so cached sight lines through it are dropped.</summary>
        public event Action? Changed;

        /// <summary>A quest dungeon's door entity: its own flags say open and locked.</summary>
        public void Add(Door door, float radius, float centerY)
        {
            ArgumentNullException.ThrowIfNull(door);
            Add(new Blocker(door, door.Position, door.Rotation.yf, door.Rotation.wf, radius, centerY,
                door.IsOpen, door.IsLocked));
        }

        /// <summary>A door placed in the playfield's static data (Dynels.dat), which the client creates itself.</summary>
        public void Add(int doorInstance, AoVector3 position, float rotationY, float rotationW, float radius, float centerY,
            int flags)
            => Add(new Blocker(null, position, rotationY, rotationW, radius, centerY,
                (flags & Door.OpenFlag) != 0, (flags & Door.LockedFlag) != 0)
            {
                StaticIdentity = new Identity { Type = IdentityType.Door, Instance = doorInstance }
            });

        /// <summary>
        /// Puts an arriving client's copy of every static door on the server's state, as retail's zone-in sends a
        /// DoorStatusUpdate per door. Quest dungeon doors carry theirs in their spawn.
        /// </summary>
        public void SendStates(Network.IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            foreach (Blocker d in _blockers)
            {
                if (d.StaticIdentity is { } identity)
                    session.Send(Door.StatusMessage(identity, d.IsOpen, d.IsLocked));
            }
        }

        void Add(Blocker blocker)
        {
            if (blocker.Height <= 0f || blocker.HalfWidth <= 0f)
                return;
            _blockers.Add(blocker);
            Changed?.Invoke();
        }

        /// <summary>True when the segment passes through a closed door's panel.</summary>
        public bool Blocks(Vec3 from, Vec3 to)
        {
            float minX = MathF.Min(from.X, to.X), maxX = MathF.Max(from.X, to.X);
            float minZ = MathF.Min(from.Z, to.Z), maxZ = MathF.Max(from.Z, to.Z);
            foreach (Blocker d in _blockers)
            {
                if (d.IsOpen
                    || maxX < d.X - d.HalfWidth || minX > d.X + d.HalfWidth
                    || maxZ < d.Z - d.HalfWidth || minZ > d.Z + d.HalfWidth)
                    continue;

                // Signed distances to the panel's plane (vertical, normal = its facing).
                float sa = ((from.X - d.X) * d.FacingX) + ((from.Z - d.Z) * d.FacingZ);
                float sb = ((to.X - d.X) * d.FacingX) + ((to.Z - d.Z) * d.FacingZ);
                if ((sa > 0f && sb > 0f) || (sa < 0f && sb < 0f) || sa == sb)
                    continue;

                float t = sa / (sa - sb);
                float hx = from.X + ((to.X - from.X) * t) - d.X;
                float hy = from.Y + ((to.Y - from.Y) * t) - d.Y;
                float hz = from.Z + ((to.Z - from.Z) * t) - d.Z;
                if (MathF.Abs((hx * d.AcrossX) + (hz * d.AcrossZ)) <= d.HalfWidth && hy >= 0f && hy <= d.Height)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Opens every closed, unlocked door a living character is touching, and closes every door whose contact
        /// ended. A door that is open with nobody having touched it (spawned open, or opened by Use) stays open until
        /// someone passes through it.
        /// </summary>
        public void Tick(PlayfieldType playfield, double deltaTime)
        {
            _sinceTouch += deltaTime;
            if (_sinceTouch < TouchIntervalSeconds || _blockers.Count == 0)
                return;
            _sinceTouch = 0;

            PlayfieldLocality? locality = playfield.GetService<PlayfieldLocality>();
            if (locality == null)
                return;

            bool changed = false;
            foreach (Blocker d in _blockers)
            {
                if (d.IsLocked && !d.IsOpen)
                    continue;

                bool touched = IsTouched(locality, d);
                if (touched && !d.IsOpen)
                {
                    d.Open();
                    changed = true;
                }
                else if (!touched && d.Touched && d.IsOpen)
                {
                    d.Close();
                    changed = true;
                }

                d.Touched = touched;
            }

            _nearby.Clear();
            if (changed)
                Changed?.Invoke();
        }

        bool IsTouched(PlayfieldLocality locality, Blocker d)
        {
            float reach = d.Radius + _touchRadius;
            locality.CollectNear(new AoVector3(d.X, d.Y, d.Z), reach, _nearby);
            foreach (Character character in _nearby)
            {
                if (character.IsDead)
                    continue;
                float dx = character.Position.xf - d.X;
                float dy = character.Position.yf + _touchCenterY - (d.Y + d.CenterY);
                float dz = character.Position.zf - d.Z;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= reach * reach)
                    return true;
            }

            return false;
        }

        sealed class Blocker
        {
            readonly Door? _door;
            bool _open;
            readonly bool _locked;

            public Blocker(Door? door, AoVector3 position, float rotationY, float rotationW, float radius, float centerY,
                bool open, bool locked)
            {
                _door = door;
                _open = open;
                _locked = locked;
                X = position.xf;
                Y = position.yf;
                Z = position.zf;
                Radius = radius;
                CenterY = centerY;
                Height = 2f * centerY;
                HalfWidth = MathF.Sqrt(MathF.Max(0f, (radius * radius) - (centerY * centerY)));

                // Doors turn about the vertical axis only: facing is +Z turned by the yaw, across is +X.
                float yaw = 2f * MathF.Atan2(rotationY, rotationW);
                float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
                AcrossX = cos;
                AcrossZ = -sin;
                FacingX = sin;
                FacingZ = cos;
            }

            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            public float Radius { get; }
            public float CenterY { get; }
            public float Height { get; }
            public float HalfWidth { get; }
            public float AcrossX { get; }
            public float AcrossZ { get; }
            public float FacingX { get; }
            public float FacingZ { get; }

            /// <summary>Set for a door from the playfield data; a quest dungeon door is its entity's.</summary>
            public Identity? StaticIdentity { get; init; }

            public bool IsOpen => _door?.IsOpen ?? _open;

            public bool IsLocked => _door?.IsLocked ?? _locked;

            /// <summary>A character was in contact at the last check.</summary>
            public bool Touched { get; set; }

            public void Open()
            {
                if (_door != null)
                    _door.OpenByTouch();
                else
                    _open = true;
            }

            public void Close()
            {
                if (_door != null)
                    _door.CloseAfterTouch();
                else
                    _open = false;
            }
        }
    }
}
