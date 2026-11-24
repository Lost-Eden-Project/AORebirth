namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// An Engineer's support pet (PRM0, and the Predator/Marauder dogs) shares the Support slot with the
    /// Metaphysicist's mezz pets. Every pet of that slot used to be taken for a mezz pet, so /pet guard put the
    /// Engineer's pet on follow and it never defended its owner. Only the pets PetTypes.json lists under MezzPets turn
    /// guard into follow.
    /// </summary>
    [TestClass]
    public sealed class PetGuardSupportSlotTests
    {
        const string EngineerPet = "PRM0";
        const string MezzPet = "DEMD";

        [TestMethod]
        public void EngineerSupportPetGuardsWhenToldToGuard()
        {
            using var world = new PetWorld();
            PetController pet = world.Pet(EngineerPet);

            world.Pets.Command(world.Owner, PetCommandCode.Follow, []);
            Assert.AreEqual(PetMode.Follow, pet.Mode);

            world.Pets.Command(world.Owner, PetCommandCode.Guard, []);
            Assert.AreEqual(PetMode.Guard, pet.Mode, "/pet guard puts the Engineer's support pet on guard");
        }

        [TestMethod]
        public void MezzPetStillFollowsWhenToldToGuard()
        {
            using var world = new PetWorld();
            PetController pet = world.Pet(MezzPet);

            world.Pets.Command(world.Owner, PetCommandCode.Guard, []);
            Assert.AreEqual(PetMode.Follow, pet.Mode, "a mezz pet's guard is follow: it only fights when sent");
        }

        [TestMethod]
        public void ShippedCatalogKeepsEngineerPetsOffTheMezzList()
        {
            PetTypeCatalog catalog = PetTypeCatalog.For(GameDataPaths.ResolveRuntimeRoot());
            if (catalog.Count == 0)
                Assert.Inconclusive("No PetTypes.json in the resolved GameData root.");

            foreach (string hash in new[] { "PRM0", "PDM3", "UPPE", "ADPE", "SMPE", "MIPE", "MAM4", "MLMA", "RMM6" })
            {
                Assert.AreEqual(PetTypes.Support, catalog.TypeOf(hash), hash + " is a Support pet");
                Assert.IsFalse(catalog.IsMezzPet(hash), hash + " is an Engineer pet, not a mezz pet");
            }

            Assert.IsTrue(catalog.IsMezzPet("DEMD"), "Deranged Mindreaver is a Metaphysicist mezz pet");
        }

        /// <summary>A bare playfield with a PetService and a pet catalog holding one Engineer and one mezz pet.</summary>
        sealed class PetWorld : IDisposable
        {
            const int PlayfieldId = 4583;

            readonly DynelRegistry _registry = new();
            readonly ServiceProvider _services;
            readonly string _root;
            readonly Playfield _playfield;
            int _nextNpc = 2000;

            public PetWorld()
            {
                _root = Path.Combine(Path.GetTempPath(), "PetGuardSupportSlotTests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                File.WriteAllText(Path.Combine(_root, PetTypeCatalog.FileName),
                    "{\"DefaultType\":1,\"MezzPets\":[\"" + MezzPet + "\"],\"Types\":{\"" + EngineerPet + "\":2001,\""
                    + MezzPet + "\":2001}}");

                IGameData data = RootOnlyGameData.For(_root);
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(data)
                    .AddSingleton(new PlayfieldLocality(PlayfieldId, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider();
                _playfield = CreatePlayfield(PlayfieldId, _services);
                Pets = new PetService(_playfield, data, new Logger());

                var items = new Items();
                Owner = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 1 }, new Logger(), items)
                {
                    FirstName = "Engineer"
                };
                Owner.Inventory.Apply(new CharacterHydrationResult(), 1, items);
                Owner.Playfield = _playfield;
                Owner.Position = new Vector3(0, 0, 0);
                Owner.Stats.Set(CharacterStat.Level, 100);
                Owner.Stats.Set(CharacterStat.MaxHealth, 1000);
                Owner.Stats.Set(CharacterStat.Health, 1000);
                _registry.Register(Owner);
            }

            public PetService Pets { get; }

            public Player Owner { get; }

            /// <summary>A summoned pet of <paramref name="hash"/>, typed by the catalog as SummonPet types it.</summary>
            public PetController Pet(string hash)
            {
                var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = ++_nextNpc }, new Items())
                {
                    Playfield = _playfield,
                    Attackable = true,
                    Position = new Vector3(1.5, 0, 0)
                };
                npc.Stats.Set(CharacterStat.Level, 100);
                npc.Stats.Set(CharacterStat.MaxHealth, 1000);
                npc.Stats.Set(CharacterStat.Health, 1000);
                _registry.Register(npc);

                int type = PetTypeCatalog.For(_root).TypeOf(hash);
                Assert.AreEqual(PetTypes.Support, type, "fixture: the pet is in the Support slot");
                var controller = new PetController(Owner, type, hash, 100, expiresUtc: null);
                npc.BindPet(controller);
                Owner.OwnedPets.Add(npc);
                return controller;
            }

            static Playfield CreatePlayfield(int playfieldId, IServiceProvider services)
            {
                var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                Set(playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = playfieldId });
                Set(playfield, "_serviceProvider", services);
                Set(playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
                Set(playfield, "_lifecycleSync", new System.Threading.Lock());
                Set(playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
                return playfield;
            }

            static void Set(Playfield playfield, string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);

            public void Dispose()
            {
                _services.Dispose();
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>An IGameData that only knows its root: PetService reads its catalogs from there.</summary>
        class RootOnlyGameData : DispatchProxy
        {
            string _root = string.Empty;

            public static IGameData For(string root)
            {
                IGameData data = Create<IGameData, RootOnlyGameData>();
                ((RootOnlyGameData)(object)data)._root = root;
                return data;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => targetMethod?.Name == "get_RootPath"
                    ? _root
                    : throw new NotSupportedException(targetMethod?.Name);
        }

        sealed class Logger : IZoneLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public IZoneLogger CreateForPlayfield(int playfieldId) => this;
        }

        sealed class Items : IItemBuilder
        {
            public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
                Identity? identity = null, byte[]? statsBlob = null) => new()
                {
                    LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                    InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
                };

            public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
                => Create(lowId, highId, quality, source, stackCount);

            public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

            public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
        }
    }
}
