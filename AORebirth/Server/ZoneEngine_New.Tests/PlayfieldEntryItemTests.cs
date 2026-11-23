namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Shops;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Metrics;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Teams;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Playfield items (Gameplay.json playfieldItems): the item's OnUse runs on each player entering the playfield. Driven the
/// way a zone change runs: <see cref="SpawnService.ArriveFromTransfer"/> on the destination's tick, then the client's
/// reconnect (<see cref="SpawnService.CompletePendingReconnect"/>) once it has loaded. Real playfields, items.dat and nanos
/// from the runtime GameData (AO_REBIRTH_GAMEDATA_PATH): 4873 Adonis Abyss (239736: zone effect 231227, whose fumes 274322
/// choke anyone without stat 467 bit 0, which the Metawater Repellent Spiritech Suit 227633 sets), 4582 ICC Shuttleport (256413, eject 256412 above level 19). 231227 has TimeExist -1: it runs until the player
/// leaves, and leaving ends it with the fumes it keeps going. Only the network and persistence are doubles.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlayfieldEntryItemTests
{
    const int AdonisAbyss = 4873;
    const int IccShuttleport = 4582;
    const int Unmapped = 100;
    const int AdonisItem = 239736;
    const int IccItem = 256413;
    const int AdonisAbyssEffect = 231227;
    const int AdonisChoke = 274322;
    const int AdonisWatch = 274323;
    const int MetawaterSuit = 227633;
    const int IccEjectNano = 256412;
    const string ChokeMessage = "You are choking from the fumes";
    const string IccWarning = "You can not be level 20 or higher and be in the ICC shuttleport.";
    const string GmProtectedMessage = "You are protected from Environmental Damage";

    static GameplaySettings Mapped() => new()
    {
        PlayfieldItems = new Dictionary<int, int> { [AdonisAbyss] = AdonisItem, [IccShuttleport] = IccItem }
    };

    [TestMethod]
    public void Entering_adonis_abyss_unprotected_chokes_on_the_fumes()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(1, level: 60);

        w.Enter(player, w.Playfield(AdonisAbyss));
        w.Wait(player, 6);

        Assert.IsFalse(player.IsDead, "the rig's character survives the first fumes");
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisAbyssEffect), "the zone effect lands on entry");
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisChoke), "no suit: the fumes start");
        Assert.IsTrue(w.SessionOf(player).Texts.Any(t => t.Contains(ChokeMessage)), "the player is told");
        Assert.IsTrue(player.Stats.Get(CharacterStat.Health) < Rig.StartHealth, "the fumes hurt");
        Assert.IsFalse(w.SessionOf(player).Texts.Any(t => t.Contains(GmProtectedMessage)), "the GM-only line is not sent");
        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State, "the zone-in completes");
    }

    [TestMethod]
    public void Entering_adonis_abyss_in_a_metawater_suit_does_not_choke()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(9, level: 60);
        w.Wear(player, MetawaterSuit);
        Assert.AreNotEqual(0, player.Stats.Get(CharacterStat.PetReq1) & 1, "the suit sets the Abyss protection bit (stat 467 bit 0)");

        w.Enter(player, w.Playfield(AdonisAbyss));
        w.Wait(player, 6);

        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisAbyssEffect), "the zone effect still lands");
        Assert.IsFalse(player.Buffs.Any(b => b.Id == AdonisChoke), "protected: no fumes");
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisWatch), "the effect keeps watching in case the suit comes off");
        Assert.IsFalse(w.SessionOf(player).Texts.Any(t => t.Contains(ChokeMessage)));
        Assert.AreEqual(Rig.StartHealth, player.Stats.Get(CharacterStat.Health));
    }

    [TestMethod]
    public void The_zone_effect_keeps_choking_while_the_player_stays()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(10, level: 60);

        w.Enter(player, w.Playfield(AdonisAbyss));
        w.Wait(player, 6);
        int afterFirst = player.Stats.Get(CharacterStat.Health);
        w.Wait(player, 12);
        w.Wait(player, 18);

        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisAbyssEffect), "the zone effect runs until something ends it");
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisChoke), "the fumes recast themselves as they end");
        Assert.IsTrue(player.Stats.Get(CharacterStat.Health) < afterFirst, "and keep hurting");
    }

    [TestMethod]
    public void Leaving_adonis_abyss_ends_the_fumes()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(11, level: 60);
        w.Enter(player, w.Playfield(AdonisAbyss));
        w.Wait(player, 6);
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisChoke), "choking before leaving");

        w.Enter(player, w.Playfield(Unmapped));
        int texts = w.SessionOf(player).Texts.Count(t => t.Contains(ChokeMessage));
        int health = player.Stats.Get(CharacterStat.Health);
        w.Wait(player, 6);
        w.Wait(player, 12);

        Assert.IsFalse(player.Buffs.Any(b => b.Id is AdonisAbyssEffect or AdonisChoke or AdonisWatch),
            "the Abyss effect and its fumes stay behind");
        Assert.AreEqual(texts, w.SessionOf(player).Texts.Count(t => t.Contains(ChokeMessage)), "no more choking outside");
        Assert.AreEqual(health, player.Stats.Get(CharacterStat.Health), "no more damage outside");
        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State, "the zone-in completes");
    }

    [TestMethod]
    public void Leaving_adonis_abyss_in_a_metawater_suit_ends_the_watch()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(12, level: 60);
        w.Wear(player, MetawaterSuit);
        w.Enter(player, w.Playfield(AdonisAbyss));
        w.Wait(player, 6);
        Assert.IsTrue(player.Buffs.Any(b => b.Id == AdonisWatch), "watching before leaving");

        w.Enter(player, w.Playfield(IccShuttleport));
        w.Wait(player, 12);

        Assert.IsFalse(player.Buffs.Any(b => b.Id is AdonisAbyssEffect or AdonisChoke or AdonisWatch));
    }

    [TestMethod]
    public void A_nano_with_negative_time_exist_runs_until_removed()
    {
        using var w = new Rig(Mapped());
        Assert.IsTrue(w.Templates.TryGet(AdonisAbyssEffect, out ItemTemplate template));
        NanoSpell spell = NanoSpell.From(template);
        Assert.IsTrue(spell.RunsUntilRemoved, "231227 carries TimeExist -1");

        DateTime now = DateTime.UtcNow;
        Buff buff = Buff.Create(spell, new Identity { Type = IdentityType.SimpleChar, Instance = 1 }, 1, now);

        Assert.IsFalse(buff.IsExpired(now.AddHours(1)), "it does not end on its own");
        Assert.IsTrue(buff.IsExpired(now.AddMilliseconds(Buff.MaxDurationCentiseconds * 10L)), "the NCU ceiling still bounds it");
    }

    [TestMethod]
    public void Entering_adonis_abyss_again_keeps_a_single_zone_effect()
    {
        using var w = new Rig(Mapped());
        Playfield abyss = w.Playfield(AdonisAbyss), rubiKa = w.Playfield(Unmapped);
        Player player = w.Player(2, level: 60);

        w.Enter(player, abyss);
        w.Enter(player, rubiKa);
        w.Enter(player, abyss);

        Assert.AreEqual(1, player.Buffs.Count(b => b.Id == AdonisAbyssEffect), "re-entering never stacks the zone effect");
    }

    [TestMethod]
    public void A_gm_entering_adonis_abyss_gets_the_gm_line_instead_of_the_nano()
    {
        using var w = new Rig(Mapped());
        Player gm = w.Player(3, level: 60);
        gm.Stats.Set(CharacterStat.GmLevel, 1, StatDetail.Base);

        w.Enter(gm, w.Playfield(AdonisAbyss));

        Assert.IsFalse(gm.Buffs.Any(b => b.Id == AdonisAbyssEffect));
        Assert.IsTrue(w.SessionOf(gm).Texts.Any(t => t.Contains(GmProtectedMessage)), "the GM is told");
    }

    [TestMethod]
    public void Entering_icc_shuttleport_above_level_19_warns_and_starts_the_eject()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(4, level: 20);

        w.Enter(player, w.Playfield(IccShuttleport));

        Assert.IsTrue(w.SessionOf(player).Texts.Any(t => t.Contains(IccWarning)), "the player is warned");
        Assert.IsTrue(player.Buffs.Any(b => b.Id == IccEjectNano), "the eject is under way");
    }

    [TestMethod]
    public void Entering_icc_shuttleport_at_level_19_does_nothing()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(5, level: 19);

        w.Enter(player, w.Playfield(IccShuttleport));

        Assert.IsFalse(w.SessionOf(player).Texts.Any(t => t.Contains(IccWarning)));
        Assert.IsFalse(player.Buffs.Any(b => b.Id == IccEjectNano));
        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State);
    }

    [TestMethod]
    public void A_reconnect_without_zoning_is_not_an_entry()
    {
        using var w = new Rig(Mapped());
        Playfield icc = w.Playfield(IccShuttleport);
        Player player = w.Player(6, level: 19);
        w.Enter(player, icc);
        player.Stats.Set(CharacterStat.Level, 30, StatDetail.Base);

        // The client drops and reconnects in place: no transfer came before it.
        w.Reconnect(player, icc);

        Assert.IsFalse(w.SessionOf(player).Texts.Any(t => t.Contains(IccWarning)));
        Assert.IsFalse(player.Buffs.Any(b => b.Id == IccEjectNano));
        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State);
    }

    [TestMethod]
    public void A_playfield_without_an_item_runs_nothing()
    {
        using var w = new Rig(Mapped());
        Player player = w.Player(7, level: 60);

        w.Enter(player, w.Playfield(Unmapped));

        Assert.IsFalse(player.Buffs.Any(b => b.Id is AdonisAbyssEffect or IccEjectNano));
        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State);
    }

    [TestMethod]
    public void An_item_missing_from_the_catalog_never_blocks_the_zone_in()
    {
        using var w = new Rig(new GameplaySettings { PlayfieldItems = new Dictionary<int, int> { [AdonisAbyss] = 999_999_999 } });
        Player player = w.Player(8, level: 60);

        w.Enter(player, w.Playfield(AdonisAbyss));

        Assert.AreEqual(SessionState.InPlay, w.SessionOf(player).State);
        Assert.AreEqual(0, player.Buffs.Count);
    }

    [TestMethod]
    public void The_shipped_gameplay_config_maps_adonis_abyss_and_icc_shuttleport()
    {
        GameplaySettings settings = GameplaySettings.Load(RepoGameplayJson());

        Assert.AreEqual(AdonisItem, settings.PlayfieldItems[AdonisAbyss]);
        Assert.AreEqual(IccItem, settings.PlayfieldItems[IccShuttleport]);
    }

    [TestMethod]
    public void A_non_positive_mapping_fails_the_config_load()
    {
        string path = Path.Combine(Path.GetTempPath(), "playfield-items-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "stuckLocation": { "playfield": 566, "x": 440, "y": 29, "z": 285 },
              "playfieldItems": { "4873": 0 }
            }
            """);
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => GameplaySettings.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    static string RepoGameplayJson([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Config", GameplaySettings.ConfigFileName));

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static object Field(object target, string name)
        => typeof(Playfield).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    sealed class Rig : IDisposable
    {
        readonly Dictionary<Player, Session> _sessions = new();
        readonly ItemBuilder _items;

        internal const int StartHealth = 20000;

        internal PlayfieldManager Manager { get; }

        internal ItemTemplateCatalog Templates { get; }

        internal Rig(GameplaySettings gameplay)
        {
            var logger = new StubLogger();
            var data = new GameDataStore(logger);
            foreach (int id in new[] { AdonisAbyss, IccShuttleport })
            {
                if (!Directory.Exists(Path.Combine(data.RootPath, GameDataPaths.PlayfieldRelativeDirectory(id))))
                    Assert.Inconclusive("The runtime GameData at " + data.RootPath + " lacks playfield " + id + ".");
            }
            var templates = new ItemTemplateCatalog(data, logger);
            Templates = templates;
            foreach (int id in new[] { AdonisItem, IccItem, AdonisAbyssEffect, AdonisChoke, AdonisWatch, MetawaterSuit, IccEjectNano })
            {
                if (!templates.TryGet(id, out _))
                    Assert.Inconclusive("The runtime items.dat lacks " + id + ".");
            }
            var ids = new InstanceIds();
            _items = new ItemBuilder(templates, ids, logger);
            PlayfieldManager? manager = null;
            var lazy = new Lazy<PlayfieldManager>(() => manager ?? throw new InvalidOperationException("The manager is not built yet."));
            var persistence = new Persistence();
            var flush = new InventoryFlushService(lazy, persistence, logger);
            var actions = new InventoryActionService(persistence, flush, ids, logger);
            var minter = new HashItemMinter(data, templates, _items);
            var teams = new TeamService(dispatchOnOwner: (player, action) => player.Playfield?.DispatchPlayerProjection(player, action));
            manager = new PlayfieldManager(logger, new MessageRouter([], logger), new PlayerHydrator(_items, data), data, _items, minter,
                new Inventory(), ids, new InventoryMoveService(logger, flush, actions), flush,
                new TradeService(logger, data, templates, minter, ids, flush, persistence), Blank<CharacterSnapshotService>(),
                new PlayfieldMetricsRegistry(), teams, templates, new Shops(), gameplay: gameplay, summons: new SummonService(lazy));
            Manager = manager;
        }

        internal Playfield Playfield(int id) => Manager.GetOrCreate(id);

        /// <summary>A player in play, on no playfield yet.</summary>
        internal Player Player(int id, int level)
        {
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubLogger(), _items)
            {
                FirstName = "Entry" + id,
                Name = "Entry" + id
            };
            player.LastName = "Tester";
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            // A spawnable character, as hydration leaves one: the reconnect checks its payload before it goes out.
            foreach (CharacterStat stat in CharacterHydrationValidator.RequiredSpawnStats)
                player.Stats.Set(stat, 100, StatDetail.Base);
            player.Stats.Set(CharacterStat.Flags, 0, StatDetail.Base);
            player.Stats.Set(CharacterStat.VisualFlags, 0, StatDetail.Base);
            player.Stats.Set(CharacterStat.Expansion, 0, StatDetail.Base);
            player.Stats.Set(CharacterStat.Breed, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.Sex, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.Race, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.Profession, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.Side, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.TitleLevel, 1, StatDetail.Base);
            player.Stats.Set(CharacterStat.HeadMesh, 40683, StatDetail.Base);
            player.Stats.Set(CharacterStat.MaxNCU, 1200, StatDetail.Base);
            player.Stats.Set(CharacterStat.Level, level, StatDetail.Base);
            // The fumes' Modify rebases the character, which recomputes max health from Body Development: enough of it
            // that a few 1000-point fume hits never kill the character mid-test (death would empty NCU).
            player.Stats.Set(CharacterStat.BodyDevelopment, 3000, StatDetail.Base);
            player.Rebase();
            player.Stats.Set(CharacterStat.MaxHealth, StartHealth);
            player.Stats.Set(CharacterStat.Health, StartHealth);
            player.Session = session;
            session.BindPlayer(player);
            Manager.RegisterPlayer(player);
            _sessions[player] = session;
            return player;
        }

        internal Session SessionOf(Player player) => _sessions[player];

        /// <summary>The player's nanos run on its playfield's tick, <paramref name="seconds"/> from now.</summary>
        internal void Wait(Player player, double seconds)
        {
            Playfield playfield = player.Playfield!;
            DateTime at = DateTime.UtcNow.AddSeconds(seconds);
            OnTick(playfield, () => NanoRuntime.Tick(player, at));
        }

        /// <summary>Puts the item on in the first armor slot it fits, as a worn item counts after a rebase.</summary>
        internal void Wear(Player player, int itemId)
        {
            Item item = _items.Create(itemId, itemId, 1, ItemSource.Quest);
            int slots = item.Definition.Stats[CharacterStat.Slot];
            int slot = Enumerable.Range(0, 31).First(bit => (slots & (1 << bit)) != 0);
            // Armor placements start at 0x10: Slot bit 1 (body) is placement 0x11, bit 2 (head) 0x12.
            Assert.IsTrue(player.Inventory.Armor.Add(0x10 + slot, item), "worn");
            player.Rebase();
            player.Stats.Set(CharacterStat.MaxHealth, StartHealth);
            player.Stats.Set(CharacterStat.Health, StartHealth);
        }

        /// <summary>
        /// A zone change into <paramref name="destination"/>: the source lets go, the destination registers the player on
        /// its tick, then the client finishes loading and reconnects.
        /// </summary>
        internal void Enter(Player player, Playfield destination)
        {
            if (player.Playfield is Playfield source)
                OnTick(source, () => source.GetRequiredService<SpawnService>().LeaveForTransfer(player));
            Session session = SessionOf(player);
            session.State = SessionState.Loading;
            OnTick(destination, () => destination.GetRequiredService<SpawnService>()
                .ArriveFromTransfer(player, destination.SnapFeetToFloor(new Vector3(200, 10, 200))));
            Reconnect(player, destination);
        }

        /// <summary>The client connects (again) to the playfield the player is on.</summary>
        internal void Reconnect(Player player, Playfield playfield)
        {
            Session session = SessionOf(player);
            session.State = SessionState.Loading;
            OnTick(playfield, () => playfield.GetRequiredService<SpawnService>().CompletePendingReconnect(session,
                new PendingReconnectInboundItem { Session = session, CharacterId = player.Identity.Instance }));
        }

        static void OnTick(Playfield playfield, Action work)
        {
            lock ((Lock)Field(playfield, "_tickSync"))
                work();
        }

        public void Dispose()
        {
            foreach (Player player in _sessions.Keys)
            {
                player.Playfield?.GetService<DynelRegistry>()?.UnregisterExact(player);
                Manager.UnregisterPlayer(player);
            }
        }
    }

    sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        internal readonly List<string> Texts = [];
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing, AORebirth.Core.Vector.Quaternion heading) => false;
        public void SendIntrazoneTeleport(Vector3 landing, AORebirth.Core.Vector.Quaternion heading, int destinationKey) { }
        public void Send(byte[] packet) { }
        public void Send(MessageBody body)
        {
            if (body is FormatFeedbackMessage feedback && feedback.Message != null)
                Texts.Add(feedback.Message);
            else if (body is ChatTextMessage chat && chat.Text != null)
                Texts.Add(chat.Text);
        }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>An entry writes nothing durable here; the character's save goes through its own lane.</summary>
    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
        {
            if (inserts.Count > 0)
                throw new AssertFailedException("A playfield item grants no items.");
        }
        public void Persist(InventoryMutationBatch batch) => throw new AssertFailedException("A playfield item moves no items.");
        public void Persist(TradePersistenceBatch batch) => throw new AssertFailedException("A playfield item trades nothing.");
    }

    sealed class Shops : IShopDao
    {
        public IList<ShopVendorData> ListForPlayfield(int playfieldId) => [];
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) => throw new AssertFailedException(message, exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Inventory : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new AssertFailedException("A playfield item grants no items.");
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement)
            => throw new AssertFailedException("A playfield item moves no items.");
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) => throw new AssertFailedException("A playfield item moves no items.");
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
            => throw new AssertFailedException("A playfield item grants no items.");
    }

    sealed class InstanceIds : IItemInstanceIdAllocator
    {
        int _next = 700000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
