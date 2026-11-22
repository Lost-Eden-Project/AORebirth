namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using LostEden.Protocol.GameData;
    using ZoneEngine_New.Core.Helpers;

    using ZoneEngine_New.Core.Entities;

    public sealed class DynelRegistry
    {
        private const int FirstNpcInstance = 1_000_000;
        private const int FirstCorpseInstance = 2_000_000;
        private const int FirstVendingMachineInstance = 3_000_000;
        private const int FirstTempBagInstance = 4_000_000;
        private const int FirstStaticDynelInstance = 5_000_000;

        private readonly Lock _sync = new();
        private readonly Dictionary<ulong, Dynel> _dynels = new();
        /// <summary>
        /// Engine-wide, unlike the other counters: an NPC can change playfield alive (a pet zoning with its owner)
        /// and keeps its identity, so no two NPCs on any playfield may ever share one.
        /// </summary>
        private static int _nextNpcInstance = FirstNpcInstance - 1;
        /// <summary>
        /// Engine-wide too, and from World's block when there is one: the client keeps a container's name by its identity
        /// (as it keeps a bag's), so a corpse id handed out again on another playfield (each mission floor is one) opened
        /// with the name of the corpse that had it before (bugs #619, #574, #589).
        /// </summary>
        private static int _nextCorpseInstance = FirstCorpseInstance - 1;
        private static int _firstCorpseInstance = FirstCorpseInstance;
        private static int _lastCorpseInstance = int.MaxValue;
        private int _nextVendingMachineInstance = FirstVendingMachineInstance - 1;
        private int _nextTempBagInstance = FirstTempBagInstance - 1;
        private int _nextStaticDynelInstance = FirstStaticDynelInstance - 1;

        /// <summary>Last NPC instance id this engine may hand out; another zone instance owns the next block.</summary>
        private static int _lastNpcInstance = int.MaxValue;
        private static int _npcRangeConfigured;
        private static int _firstNpcInstance = FirstNpcInstance;

        /// <summary>
        /// The block of NPC instance ids World gave this zone instance (its slot), so NPCs and pets on different
        /// instances never share an identity. Set once, before the first playfield builds.
        /// </summary>
        public static bool ConfigureNpcRange(int first, int count)
        {
            if (first <= 0 || count <= 0) throw new ArgumentOutOfRangeException(nameof(first));
            // Already set: true only when World gave the same block again.
            if (Interlocked.CompareExchange(ref _npcRangeConfigured, 1, 0) != 0)
                return first == Volatile.Read(ref _firstNpcInstance);
            Volatile.Write(ref _firstNpcInstance, first);
            Volatile.Write(ref _lastNpcInstance, (int)Math.Min(int.MaxValue, first + (long)count - 1));
            Volatile.Write(ref _nextNpcInstance, first - 1);
            // Corpses are their own identity type, so they take the same block without meeting an NPC id, and two zone
            // instances never hand out the same corpse.
            Volatile.Write(ref _firstCorpseInstance, first);
            Volatile.Write(ref _lastCorpseInstance, (int)Math.Min(int.MaxValue, first + (long)count - 1));
            Volatile.Write(ref _nextCorpseInstance, first - 1);
            return true;
        }

        public Identity AllocateNpcIdentity()
        {
            int instance = Interlocked.Increment(ref _nextNpcInstance);
            if (instance > Volatile.Read(ref _lastNpcInstance))
                throw new InvalidOperationException("This zone instance has used up its NPC identity range.");
            return new Identity
            {
                Type = IdentityType.SimpleChar,
                Instance = instance
            };
        }

        /// <summary>
        /// A corpse identity no other corpse on this engine has had since the block last wrapped. A death must always get
        /// a corpse, so the end of the block wraps to its start (millions of corpses later) instead of failing.
        /// </summary>
        public Identity AllocateCorpseIdentity()
            => new()
            {
                Type = IdentityType.Corpse,
                Instance = NextCorpseInstance()
            };

        internal static int NextCorpseInstance()
        {
            while (true)
            {
                int first = Volatile.Read(ref _firstCorpseInstance);
                int last = Volatile.Read(ref _lastCorpseInstance);
                int current = Volatile.Read(ref _nextCorpseInstance);
                int next = current >= last || current < first - 1 ? first : current + 1;
                if (Interlocked.CompareExchange(ref _nextCorpseInstance, next, current) == current)
                    return next;
            }
        }

        /// <summary>Identity for a shop attached to an NPC vendor rather than placed by Dynels.dat.</summary>
        public Identity AllocateVendingMachineIdentity()
        {
            int instance = Interlocked.Increment(ref _nextVendingMachineInstance);
            return new Identity
            {
                Type = IdentityType.VendingMachine,
                Instance = instance
            };
        }

        /// <summary>Identity for a static item dynel spawned from a hash rather than placed by Dynels.dat.</summary>
        public Identity AllocateStaticDynelIdentity()
        {
            int instance = Interlocked.Increment(ref _nextStaticDynelInstance);
            return new Identity
            {
                Type = IdentityType.Terminal,
                Instance = instance
            };
        }

        /// <summary>Identity for a trade window's temporary bag. Not a registered dynel.</summary>
        public Identity AllocateTempBagIdentity()
        {
            int instance = Interlocked.Increment(ref _nextTempBagInstance);
            return new Identity
            {
                Type = IdentityType.TempBag,
                Instance = instance
            };
        }

        public void Register(Dynel? dynel)
        {
            if (dynel == null)
            {
                return;
            }

            lock (_sync)
            {
                _dynels[dynel.Identity.Long()] = dynel;
            }
        }

        /// <summary>Accepted fixed identities fail on collision instead of replacing another actor.</summary>
        public bool TryRegister(Dynel dynel)
        {
            lock (_sync) return _dynels.TryAdd(dynel.Identity.Long(), dynel);
        }

        public bool UnregisterExact(Dynel dynel)
        {
            ArgumentNullException.ThrowIfNull(dynel);
            ulong key = dynel.Identity.Long();
            Character? leaving = CombatantIfRegistered(key, dynel);
            leaving?.LeaveCombat();
            lock (_sync)
                return _dynels.TryGetValue(key, out Dynel? current)
                    && ReferenceEquals(current, dynel)
                    && _dynels.Remove(key);
        }

        public void Unregister(Identity identity)
        {
            ulong key = identity.Long();
            Character? leaving = CombatantIfRegistered(key, expected: null);
            leaving?.LeaveCombat();
            lock (_sync)
                _dynels.Remove(key);
        }

        public bool TryGet(Identity identity, out Dynel? dynel)
        {
            lock (_sync)
            {
                return _dynels.TryGetValue(identity.Long(), out dynel);
            }
        }

        public IEnumerable<Dynel> Dynels()
        {
            lock (_sync)
            {
                return _dynels.Values.ToList();
            }
        }

        /// <summary>A snapshot of the players into the caller's buffer (cleared first), for per-tick passes.</summary>
        public void CopyPlayers(List<Player> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            lock (_sync)
            {
                foreach (Dynel dynel in _dynels.Values)
                {
                    if (dynel is Player player)
                        buffer.Add(player);
                }
            }
        }

        /// <summary>Whether any player is registered, without copying the list.</summary>
        public bool HasPlayers()
        {
            lock (_sync)
            {
                foreach (Dynel dynel in _dynels.Values)
                {
                    if (dynel is Player)
                        return true;
                }
            }

            return false;
        }

        /// <summary>A snapshot of every dynel into the caller's buffer (cleared first), for per-tick passes.</summary>
        public void CopyDynels(List<Dynel> buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            buffer.Clear();
            lock (_sync)
            {
                buffer.AddRange(_dynels.Values);
            }
        }

        public IEnumerable<Dynel> Players()
        {
            lock (_sync)
            {
                return _dynels.Values.Where(d => d.IsPlayer).ToList();
            }
        }

        public IEnumerable<Player> PlayerEntities()
        {
            lock (_sync)
            {
                return _dynels.Values.OfType<Player>().ToList();
            }
        }

        public void Clear()
        {
            Character[] leaving;
            lock (_sync)
                leaving = _dynels.Values.OfType<Character>().ToArray();

            foreach (Character character in leaving)
                character.LeaveCombat();

            lock (_sync)
                _dynels.Clear();
        }

        /// <summary>
        /// The registered character for <paramref name="key"/>, when it is a
        /// <see cref="Character"/> and, if <paramref name="expected"/> is set, that same instance.
        /// Read outside the lock that removes it so <see cref="Character.LeaveCombat"/> can run first.
        /// </summary>
        Character? CombatantIfRegistered(ulong key, Dynel? expected)
        {
            lock (_sync)
            {
                if (!_dynels.TryGetValue(key, out Dynel? current))
                    return null;
                if (expected != null && !ReferenceEquals(current, expected))
                    return null;
                return current as Character;
            }
        }
    }
}
