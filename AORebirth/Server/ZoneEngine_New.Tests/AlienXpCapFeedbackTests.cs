namespace ZoneEngine_New.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Core.GameData;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    /// <summary>
    /// Bug #188: a character whose level is below the next alien level's MinLevel had the bar held full in
    /// silence. Retail says once, as the bar fills, that the level must be N to obtain it.
    /// </summary>
    [TestClass]
    public sealed class AlienXpCapFeedbackTests
    {
        const int StepXp = 1000;
        const int RequiredLevel = 15;

        [TestMethod]
        public void BarHeldFullByTheLevelCapIsAnnouncedOnce()
        {
            using var world = new AlienWorld();
            Player player = world.Player(level: 10);

            for (int i = 0; i < 40 && player.Stats.GetOrZero(CharacterStat.AlienXP) < StepXp; i++)
                player.AwardAlienXp(StepXp);

            Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.AlienLevel));
            Assert.AreEqual(StepXp, player.Stats.GetOrZero(CharacterStat.AlienXP));
            Assert.AreEqual(1, world.Session.Sent.OfType<FormatFeedbackMessage>().Count());

            player.AwardAlienXp(StepXp);
            player.AwardAlienXp(StepXp);

            Assert.AreEqual(StepXp, player.Stats.GetOrZero(CharacterStat.AlienXP));
            Assert.AreEqual(1, world.Session.Sent.OfType<FormatFeedbackMessage>().Count());
        }

        [TestMethod]
        public void HighEnoughCharacterTakesTheAlienLevelWithoutTheMessage()
        {
            using var world = new AlienWorld();
            Player player = world.Player(level: RequiredLevel);

            for (int i = 0; i < 40 && player.Stats.GetOrZero(CharacterStat.AlienLevel) < 1; i++)
                player.AwardAlienXp(StepXp);

            Assert.AreEqual(1, player.Stats.GetOrZero(CharacterStat.AlienLevel));
            Assert.AreEqual(0, world.Session.Sent.OfType<FormatFeedbackMessage>().Count());
        }

        sealed class AlienWorld : System.IDisposable
        {
            readonly ServiceProvider _services;

            public Playfield Playfield { get; }

            public RecordingZoneSession Session { get; } = new() { State = SessionState.InPlay };

            public AlienWorld()
            {
                var data = new StubGameData(HashItemCatalog.Parse("{}"), alienXp: new Dictionary<int, AlienXpLevelEntry>
                {
                    [1] = new AlienXpLevelEntry { Level = 1, NextLevelXp = StepXp, MinLevel = RequiredLevel },
                    [2] = new AlienXpLevelEntry { Level = 2, NextLevelXp = StepXp * 2, MinLevel = RequiredLevel }
                });
                _services = new ServiceCollection()
                    .AddSingleton(new DynelRegistry())
                    .AddSingleton(new PlayfieldLocality(4582, null))
                    .AddSingleton<IGameData>(data)
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider();
                Playfield = BarePlayfield.Create(4582, _services);
            }

            public Player Player(int level)
            {
                Player player = TestWorld.CreatePlayer(1);
                player.Playfield = Playfield;
                player.Stats.Set(CharacterStat.Level, level);
                player.Stats.Set(CharacterStat.AlienLevel, 0);
                player.Stats.Set(CharacterStat.AlienXP, 0);
                player.Stats.Set(CharacterStat.XPModifier, 0);
                Session.BindPlayer(player);
                player.Session = Session;
                return player;
            }

            public void Dispose() => _services.Dispose();
        }
    }
}
