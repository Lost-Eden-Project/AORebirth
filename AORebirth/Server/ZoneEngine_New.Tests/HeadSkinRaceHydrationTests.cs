namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// Live report (Rioki, Lost Eden): a Solitus made with a dark-skinned head showed a light-skinned body. Creation
    /// never stored Race, so login filled the default 1 and the client re-skinned the body light on its next
    /// appearance refresh. These drive the login hydration and check the Race both spawn packets carry.
    /// </summary>
    [TestClass]
    public sealed class HeadSkinRaceHydrationTests
    {
        const int CharacterId = 9951;

        [DataTestMethod]
        [DataRow(40625, HeadSkinRace.African)] // head_solitusfemale_african00
        [DataRow(40621, HeadSkinRace.African)] // head_solitusfemale_african07
        [DataRow(40685, HeadSkinRace.African)] // head_solitusmale_african00
        [DataRow(40674, HeadSkinRace.African)] // head_solitusmale_african08
        [DataRow(40614, HeadSkinRace.Asian)]   // head_solitusfemale_asian03
        [DataRow(40675, HeadSkinRace.Asian)]   // head_solitusmale_asian00
        [DataRow(223829, HeadSkinRace.Asian)]  // head_solitusmale_asian08
        [DataRow(40683, HeadSkinRace.Default)] // head_solitusmale02
        public void Login_without_a_stored_race_sends_the_heads_skin_race(int head, int race)
        {
            Player player = LogIn(Stats(head, storedRace: null));

            Assert.AreEqual(race, player.Stats.Get(CharacterStat.Race, StatDetail.Base));
            AssertWireRace(player, race);
        }

        [TestMethod]
        public void A_stored_default_race_under_a_dark_head_is_lifted_without_rewriting_the_stored_rows()
        {
            StatRecord[] stored = Stats(40619, storedRace: HeadSkinRace.Default);
            var before = stored.Select(row => (row.StatId, row.StatValue)).ToArray();

            Player player = LogIn(stored);

            AssertWireRace(player, HeadSkinRace.African);
            CollectionAssert.AreEqual(before, stored.Select(row => (row.StatId, row.StatValue)).ToArray());
        }

        [TestMethod]
        public void An_explicit_race_and_an_unset_race_are_left_to_the_stored_value_and_the_validator()
        {
            Player explicitRace = LogIn(Stats(40619, storedRace: HeadSkinRace.Asian));
            AssertWireRace(explicitRace, HeadSkinRace.Asian);

            // A corrupt sentinel is not papered over by the head: hydration still refuses the character.
            CharacterHydrationResult? corrupt = Service(Stats(40619, storedRace: (int)CharacterStat.Unset)).LoadForLogin(CharacterId);
            Assert.IsNull(corrupt);
        }

        static void AssertWireRace(Player player, int race)
        {
            SimpleCharFullUpdateMessage scfu = player.BuildSpawnMessage();
            FullCharacterMessage full = player.BuildFullCharacterMessage();
            PlayerSpawnPayloadValidator.RequireValidMessages(scfu, full, isGm: false);

            Assert.AreEqual((uint)race, scfu.Appearance.Race, "others see the body skin from the spawn appearance");
            // Race (89) rides the byte-stat block of the player's own full update.
            Assert.AreEqual(race, (int)full.Stats3.Single(row => row.Value1 == (int)CharacterStat.Race).Value2,
                "the player's own client re-skins from its Race stat on every appearance refresh");
        }

        /// <summary>The login path: the hydration service over stored rows, then the hydrator's stat copy.</summary>
        static Player LogIn(StatRecord[] stored)
        {
            CharacterHydrationResult hydration = Service(stored).LoadForLogin(CharacterId)
                ?? throw new AssertFailedException("hydration refused a complete character");

            var items = new Items();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = CharacterId }, new Logger(), items)
            {
                Name = hydration.Character.Name
            };
            player.Inventory.Apply(hydration, CharacterId, items);
            foreach (StatRecord stat in hydration.Stats)
            {
                if (!StatCollection.IsRuntimeOnly((CharacterStat)stat.StatId))
                    player.Stats.Set((CharacterStat)stat.StatId, stat.StatValue, StatDetail.Base);
            }

            player.Position = new AORebirth.Core.Vector.Vector3(hydration.Character.X, hydration.Character.Y, hydration.Character.Z);
            player.Playfield = BlankPlayfield(hydration.Character.Playfield);
            player.Rebase();
            PlayerSpawnPayloadValidator.RequireValid(player);
            return player;
        }

        static ZoneEngine_New.Core.Playfield.Playfield BlankPlayfield(int id)
        {
            var playfield = (ZoneEngine_New.Core.Playfield.Playfield)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(ZoneEngine_New.Core.Playfield.Playfield));
            typeof(ZoneEngine_New.Core.Playfield.Playfield)
                .GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(playfield, new Identity { Type = IdentityType.Playfield2, Instance = id });
            return playfield;
        }

        static CharacterHydrationService Service(StatRecord[] stored)
        {
            var character = new CharacterRecord
            {
                Id = CharacterId, Name = "Skinfixture", Playfield = 4582, X = 100, Y = 0, Z = 100, HeadingW = 1
            };
            return new CharacterHydrationService(
                Repository<ICharacterRepository>(method => method.Name == nameof(ICharacterRepository.GetById) ? character : null),
                Repository<IStatRepository>(method => method.Name == nameof(IStatRepository.GetForCharacter) ? stored : null),
                Repository<IInventoryRepository>(),
                Repository<IUploadedNanoRepository>(),
                Repository<IActiveNanoRepository>(),
                Repository<ISkillLockRepository>(),
                new Logger());
        }

        /// <summary>A Solitus fresh from creation: what LoginEngine stores, plus the stats a first save adds.</summary>
        static StatRecord[] Stats(int head, int? storedRace)
        {
            var values = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.Flags] = 0x00081241, [CharacterStat.Breed] = 1, [CharacterStat.Sex] = 2,
                [CharacterStat.Profession] = 1, [CharacterStat.Fatness] = 0, [CharacterStat.HeadMesh] = head,
                [CharacterStat.VisualFlags] = 31, [CharacterStat.Scale] = 100,
                [CharacterStat.Level] = 1, [CharacterStat.TitleLevel] = 1, [CharacterStat.Side] = 0,
                [CharacterStat.Expansion] = 171,
                [CharacterStat.Strength] = 6, [CharacterStat.Agility] = 6, [CharacterStat.Stamina] = 6,
                [CharacterStat.Intelligence] = 6, [CharacterStat.Sense] = 6, [CharacterStat.Psychic] = 6,
                [CharacterStat.BodyDevelopment] = 5, [CharacterStat.NanoPool] = 5,
                [CharacterStat.MaxHealth] = 31, [CharacterStat.Health] = 25,
                [CharacterStat.MaxNanoEnergy] = 29, [CharacterStat.CurrentNano] = 20,
                [CharacterStat.RunSpeed] = 100,
            };
            if (storedRace.HasValue)
                values[CharacterStat.Race] = storedRace.Value;
            return values.Select(pair => new StatRecord { StatId = (int)pair.Key, StatValue = pair.Value }).ToArray();
        }

        /// <summary>An interface whose calls go to <paramref name="answer"/>; anything unanswered returns an empty list.</summary>
        static T Repository<T>(Func<MethodInfo, object?>? answer = null) where T : class
        {
            T proxy = DispatchProxy.Create<T, RepositoryProxy>();
            ((RepositoryProxy)(object)proxy).Answer = answer;
            return proxy;
        }

        public class RepositoryProxy : DispatchProxy
        {
            internal Func<MethodInfo, object?>? Answer;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                object? answer = Answer?.Invoke(targetMethod!);
                if (answer != null)
                    return answer;

                Type type = targetMethod!.ReturnType;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                    return Array.CreateInstance(type.GetGenericArguments()[0], 0);
                return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
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
            int _next = 700000;

            public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
                Identity? identity = null, byte[]? statsBlob = null) => new()
                {
                    LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                    InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
                };

            public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            {
                Item item = Create(lowId, highId, quality, source, stackCount);
                item.AssignInstanceId(++_next);
                return item;
            }

            public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

            public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
        }
    }
}
