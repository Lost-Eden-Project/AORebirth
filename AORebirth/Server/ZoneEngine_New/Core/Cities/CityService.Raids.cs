namespace ZoneEngine_New.Core.Cities
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    using PlayfieldInstance = ZoneEngine_New.Core.Playfield.Playfield;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// CityRules.json "Raid": what starts an alien raid on an org city. The raid itself (its timings, music and ship
    /// scripts, waves, General, end) is the NPC script of <see cref="DirectorHash"/>, an invisible NPC put at the city
    /// controller when the raid starts (GameData/NpcScripts, bound by hash, per playfield where a city differs). Nothing
    /// of a raid is in the client (Gamecode, City.dll and GUI.dll hold no raid logic); docs/CITY_RAIDS.md has the capture.
    /// </summary>
    public sealed class CityRaidRules
    {
        public bool Enabled { get; set; } = true;

        /// <summary>The controller's charge needed to start a raid (the guide: 50%).</summary>
        public float MinCharge { get; set; } = 0.5f;

        /// <summary>The raid director's NPC hash: its NPC script runs the raid.</summary>
        public string DirectorHash { get; set; } = "RDIR";
    }

    public sealed partial class CityService
    {
        // The raid director running on each district, while it lives, and the character who started each director's raid.
        readonly ConcurrentDictionary<(int, int), NpcCharacter> _raids = new();
        readonly ConcurrentDictionary<int, int> _raidInitiators = new();

        /// <summary>
        /// The raid group of the raid <paramref name="director"/> runs: the team or raid the character who lowered the
        /// cloak is in now (or that character alone), counting those alive, on the director's playfield and within
        /// <paramref name="range"/> metres of it, and the highest level among them. Read each time it is asked, so a
        /// group that leaves, goes inside a building (another playfield) or dies counts no more. (0, 0) with nobody.
        /// </summary>
        public (int Members, int Highest) RaidGroup(NpcCharacter director, float range)
        {
            if (!_raidInitiators.TryGetValue(director.Identity.Instance, out int initiatorId)
                || director.Playfield is not { } playfield
                || !_playfields.Value.FindPlayer(initiatorId, out Player initiator))
                return (0, 0);

            IEnumerable<int> ids = initiator.Playfield?.GetService<Teams.TeamService>()?.GetTeam(initiator) is { } team
                ? team.MemberIds
                : new[] { initiatorId };
            int members = 0, highest = 0;
            double range2 = range * (double)range;
            foreach (int id in ids)
            {
                if (!_playfields.Value.FindPlayer(id, out Player member) || !ReferenceEquals(member.Playfield, playfield) || member.IsDead)
                    continue;
                double dx = member.Position.xf - director.Position.xf, dz = member.Position.zf - director.Position.zf;
                if (dx * dx + dz * dz > range2)
                    continue;
                members++;
                highest = Math.Max(highest, member.Stats.GetOrOne(CharacterStat.Level));
            }
            return (members, highest);
        }

        /// <summary>A raid is running on the district: its director lives.</summary>
        public bool RaidRunning(int playfieldId, int index)
            => _raids.TryGetValue((playfieldId, index), out NpcCharacter? director) && !director.IsDead && director.Playfield != null;

        /// <summary>
        /// The cloak went down: the raid director spawns at the city controller (the guide: lowering the cloak brings an
        /// attack; none below 50% charge). Off the tick: the spawn goes to the city playfield's tick, and nothing happens
        /// when that playfield is not running.
        /// </summary>
        void ScheduleRaid(CityControllerDefinition controller, CityPlotState state, int initiatorId)
        {
            CityRaidRules rules = Rules.Raid;
            if (!rules.Enabled || string.IsNullOrEmpty(rules.DirectorHash) || state.OrganizationId is not int org || org <= 0
                || state.ControllerCharge < rules.MinCharge
                || !_playfields.Value.TryGet(controller.PlayfieldId, out PlayfieldInstance? playfield) || playfield == null)
                return;
            playfield.Dispatch(() =>
            {
                if (RaidRunning(controller.PlayfieldId, controller.Index) || ControllerDynel(playfield, controller) is not { } dynel)
                    return;
                if (!_gameData.TryResolveMobTemplate(rules.DirectorHash, null, out Mobs.MobTemplate template))
                {
                    _logger.Warn("City raid director hash " + rules.DirectorHash + " is not an NPC template");
                    return;
                }

                // Server only and brainless: it stands at the controller and runs its script; no client ever has it.
                NpcCharacter director = playfield.GetRequiredService<SpawnService>().SpawnMob(template,
                    new Vector3(dynel.Position.xf, dynel.Position.yf, dynel.Position.zf),
                    // Facing north (no turn): its script's offsets are world axes. Without a heading the rotation is all
                    // zeros and every offset it turns comes out NaN.
                    new AORebirth.Core.Vector.Quaternion(0, 0, 0, 1), null, SpawnSource.Script,
                    spawnHash: rules.DirectorHash, configure: npc => npc.ServerOnly = true, attachDefaultBrain: false);
                _raids[(controller.PlayfieldId, controller.Index)] = director;
                _raidInitiators[director.Identity.Instance] = initiatorId;
                _logger.Info("City raid started playfield=" + controller.PlayfieldId + " controller=" + controller.Index
                    + " director=" + director.Identity.Instance);
            });
        }

        /// <summary>The city controllers standing on <paramref name="playfield"/> (its districts' CityController dynels).</summary>
        IEnumerable<(CityControllerDefinition Controller, Dynel Dynel)> ControllersOn(PlayfieldInstance playfield)
        {
            if (!Enabled || playfield.IsProxyInstance || !_catalog.TryGetPlayfield(playfield.Identity.Instance, out CityPlayfieldDefinition city))
                yield break;
            foreach (CityControllerDefinition controller in city.Controllers)
                if (ControllerDynel(playfield, controller) is { } dynel)
                    yield return (controller, dynel);
        }

        (CityControllerDefinition Controller, Dynel Dynel)? NearestController(PlayfieldInstance playfield, Vector3 at, float range = float.MaxValue)
        {
            (CityControllerDefinition, Dynel)? best = null;
            double bestDistance = range * (double)range;
            foreach ((CityControllerDefinition controller, Dynel dynel) in ControllersOn(playfield))
            {
                double dx = dynel.Position.xf - at.xf, dz = dynel.Position.zf - at.zf;
                double d = dx * dx + dz * dz;
                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = (controller, dynel);
                }
            }
            return best;
        }

        /// <summary>The position of the city controller nearest <paramref name="at"/> (NPC script move target "Controller").</summary>
        public Vector3? ControllerNear(PlayfieldInstance playfield, Vector3 at)
            => NearestController(playfield, at) is { } found
                ? new Vector3(found.Dynel.Position.xf, found.Dynel.Position.yf, found.Dynel.Position.zf)
                : null;

        /// <summary>The charge (0..1) of the city controller nearest <paramref name="at"/>, less what is drained and not
        /// written yet; null with none (NPC script condition ControllerChargeBelow).</summary>
        public float? ChargeNear(PlayfieldInstance playfield, Vector3 at)
        {
            if (NearestController(playfield, at) is not { } found)
                return null;
            var key = (found.Controller.PlayfieldId, found.Controller.Index);
            float pending = _drains.TryGetValue(key, out Drain? drain) ? drain.Pending : 0f;
            return Plot(key.PlayfieldId, key.Index).ControllerCharge - pending;
        }

        sealed class Drain
        {
            public float Pending;
            public DateTime NextWriteUtc;
        }

        readonly ConcurrentDictionary<(int, int), Drain> _drains = new();

        /// <summary>
        /// Takes <paramref name="amount"/> (0..1) off the charge of the city controller within <paramref name="range"/>
        /// metres of <paramref name="at"/> (NPC script DrainController, an alien hacker's). Written at most every few
        /// seconds; on the playfield's tick.
        /// </summary>
        public void DrainNear(PlayfieldInstance playfield, Vector3 at, float range, float amount)
        {
            if (amount <= 0 || NearestController(playfield, at, range) is not { } found
                || !Plot(found.Controller.PlayfieldId, found.Controller.Index).IsOwned)
                return;
            var key = (found.Controller.PlayfieldId, found.Controller.Index);
            Drain drain = _drains.GetOrAdd(key, _ => new Drain());
            drain.Pending += amount;
            DateTime now = DateTime.UtcNow;
            if (now < drain.NextWriteUtc)
                return;
            float take = drain.Pending;
            drain.Pending = 0;
            drain.NextWriteUtc = now.AddSeconds(5);
            Queue(key, () =>
            {
                CityPlotState? state = _store.AdjustCharge(key.PlayfieldId, key.Index, -take);
                if (state != null)
                    Publish(state);
            });
        }

        /// <summary>
        /// A Controller Recompiler Unit used on a city controller (UseItemOnItem): the controller's charge rises by the
        /// item's CityTerminalRechargePercent (QL100 Efficient: 100), at most full, and the item is used up. Anyone may
        /// charge any owned district's controller (the guide). False when the item or target is not one.
        /// </summary>
        public bool TryRecharge(Player player, PlayfieldInstance playfield, Identity sourceSlot, Identity target)
        {
            if (!Enabled || target.Type != IdentityType.CityController || !player.Inventory.IsHydrated
                || !player.Inventory.TryGetUseItem(sourceSlot, out Inventory.Item item) || item.Locked
                || item.Definition?.Stats.TryGetValue(CharacterStat.CityTerminalRechargePercent, out int percent) != true || percent <= 0)
                return false;
            if (!playfield.GetRequiredService<DynelRegistry>().TryGet(target, out Dynel? dynel) || dynel == null
                || !TryGetController(playfield, dynel.Identity.Instance, out CityControllerDefinition controller))
                return false;
            double dx = dynel.Position.xf - player.Position.xf, dz = dynel.Position.zf - player.Position.zf;
            if (Math.Sqrt(dx * dx + dz * dz) > Rules.InteractionRange)
                return false;
            if (!Plot(controller.PlayfieldId, controller.Index).IsOwned)
                return false;
            if (!playfield.GetRequiredService<Inventory.InventoryActionService>().TryDelete(player, sourceSlot, item.InstanceId))
                return false;
            Queue((controller.PlayfieldId, controller.Index), () =>
            {
                CityPlotState? state = _store.AdjustCharge(controller.PlayfieldId, controller.Index, percent / 100f);
                if (state != null)
                    Publish(state);
            });
            return true;
        }
    }
}
