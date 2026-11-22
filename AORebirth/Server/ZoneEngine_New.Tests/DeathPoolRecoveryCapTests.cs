namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A kill on a far higher mob is held to the per-kill cap (10% of the level bar by default), but the death pool
/// recovery was half of the kill's uncapped XP: one such kill gave back hundreds of thousands of lost XP and jumped
/// a dozen levels. The recovery now follows the capped kill XP.
/// </summary>
[TestClass]
public sealed class DeathPoolRecoveryCapTests
{
    const int CharacterId = 4401;
    const int LevelBar = 100_000;

    [TestMethod]
    public void HighLevelKillRecoversFromTheCappedXpNotTheUncappedXp()
    {
        using var rig = new Rig(deathPool: 50_000);

        // 10% of a 100,000 bar = 10,000 XP; the kill is worth 400,000 uncapped.
        rig.Player.AwardXp(400_000, XpSource.Kill);

        // Half of the capped 10,000 comes back, not half of 400,000 (which would have emptied the pool).
        Assert.AreEqual(45_000, rig.Pool);
        Assert.AreEqual(45_000, rig.Player.Stats.GetOrZero(CharacterStat.UnsavedXP, StatDetail.Base));
        Assert.AreEqual(10_000 + 5_000, rig.Player.Stats.GetOrZero(CharacterStat.XP));
        Assert.AreEqual(1, rig.Player.Stats.GetOrZero(CharacterStat.Level));
    }

    [TestMethod]
    public void KillUnderTheCapStillRecoversHalfOfItsXp()
    {
        using var rig = new Rig(deathPool: 50_000);

        rig.Player.AwardXp(4_000, XpSource.Kill);

        Assert.AreEqual(48_000, rig.Pool);
        Assert.AreEqual(4_000 + 2_000, rig.Player.Stats.GetOrZero(CharacterStat.XP));
    }

    [TestMethod]
    public void RepeatedHighLevelKillsGiveBackNoMoreThanWasLost()
    {
        using var rig = new Rig(deathPool: 12_000);

        for (int i = 0; i < 3; i++)
            rig.Player.AwardXp(400_000, XpSource.Kill);

        // 5,000, 5,000, then the last 2,000: the pool ends at 0 and the player gets back exactly what was lost.
        Assert.AreEqual(0, rig.Pool);
        Assert.AreEqual(3 * 10_000 + 12_000, rig.Player.Stats.GetOrZero(CharacterStat.XP));
    }

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        readonly ServiceProvider _services;
        readonly Playfield _world = Blank<Playfield>();

        internal Rig(int deathPool)
        {
            var levels = new Dictionary<int, XpLevelEntry>
            {
                [1] = new() { Level = 1, NextLevelXp = LevelBar, FloorXp = 0 },
                [2] = new() { Level = 2, NextLevelXp = LevelBar * 2, FloorXp = LevelBar }
            };
            IGameData data = Fake<IGameData>((method, args) =>
            {
                if (method.Name == nameof(IGameData.TryGetXpLevel))
                {
                    bool found = levels.TryGetValue((int)args![0]!, out XpLevelEntry? entry);
                    args[1] = entry;
                    return found;
                }

                return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                    ? Activator.CreateInstance(method.ReturnType)
                    : null;
            });

            var log = new Logger();
            var items = new Items();
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = CharacterId }, log, items) { FirstName = "Recoup" };
            Player.Inventory.Apply(new CharacterHydrationResult(), CharacterId, items);
            Player.Stats.Set(CharacterStat.Level, 1);
            Player.Stats.Set(CharacterStat.XP, 0);
            Player.Stats.Set(CharacterStat.XPModifier, 0);
            Player.Stats.Set(CharacterStat.RegainXPPercentage, 0);
            Player.Stats.Set(CharacterStat.DeathXpPool, deathPool, StatDetail.Base);
            Player.Stats.Set(CharacterStat.UnsavedXP, deathPool, StatDetail.Base);
            Player.EnterOnline(new Session());

            _services = new ServiceCollection()
                .AddSingleton(data)
                .AddSingleton(new DynelRegistry())
                .AddSingleton(new ZoneEngine_New.Core.Playfield.Locality.PlayfieldLocality(4582, null))
                .BuildServiceProvider();
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = 4582 });
            Set(_world, "_serviceProvider", _services);
            Set(_world, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_logger", log);
            Player.Playfield = _world;
        }

        internal int Pool => Player.Stats.GetOrZero(CharacterStat.DeathXpPool, StatDetail.Base);

        public void Dispose() => _services.Dispose();
    }

    sealed class Session : IZoneSession
    {
        internal readonly List<MessageBody> Sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new AssertFailedException("ERROR " + message);
        public void Error(Exception exception, string message) => throw new AssertFailedException("ERROR " + message + " " + exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();
        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };
        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>An interface stub: every call goes to <see cref="Handler"/>.</summary>
    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Set(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("No field " + name + " on " + target.GetType().Name)).SetValue(target, value);
}
