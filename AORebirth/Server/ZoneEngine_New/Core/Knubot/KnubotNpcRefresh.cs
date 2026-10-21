namespace ZoneEngine_New.Core.Knubot;

using System.Collections.Generic;

using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

/// <summary>
/// HasKnubotData is only sent when an NPC enters a client's visibility, so a reload that gives an already visible NPC
/// a script (or takes one away) has to tell its observers again. Each playfield does this on its own tick; open
/// conversations keep the script they started with.
/// </summary>
internal static class KnubotNpcRefresh
{
    /// <summary>Queues the refresh for every playfield; called on the reloading thread.</summary>
    public static void Queue(PlayfieldManager manager, KnubotMapChange change)
    {
        foreach (ZoneEngine_New.Core.Playfield.Playfield playfield in manager.SnapshotPlayfields())
        {
            ZoneEngine_New.Core.Playfield.Playfield owner = playfield;
            owner.Dispatch(() => RefreshChanged(owner, change));
        }
    }

    /// <summary>Refreshes the spawned NPCs whose script presence differs between the two maps. Runs on the tick.</summary>
    public static int RefreshChanged(ZoneEngine_New.Core.Playfield.Playfield playfield, KnubotMapChange change)
    {
        playfield.RequireTick();
        var changed = new List<NpcCharacter>();
        foreach (Dynel dynel in playfield.GetRequiredService<DynelRegistry>().Dynels())
        {
            if (dynel is not NpcCharacter npc || npc.IsDead || !ReferenceEquals(npc.Playfield, playfield))
                continue;
            if (KnubotCatalog.Resolve(change.Before, npc, out _) != KnubotCatalog.Resolve(change.After, npc, out _))
                changed.Add(npc);
        }

        foreach (NpcCharacter npc in changed)
            Refresh(playfield, npc);
        return changed.Count;
    }

    /// <summary>
    /// Sends <paramref name="npc"/>'s spawn extras again to everyone who sees it, with an explicit HasKnubotData 0 when it
    /// no longer talks through a script. Runs on the tick.
    /// </summary>
    public static void Refresh(ZoneEngine_New.Core.Playfield.Playfield playfield, NpcCharacter npc)
    {
        PlayfieldLocality locality = playfield.GetRequiredService<PlayfieldLocality>();
        bool knubotSent = false;
        foreach (MessageBody message in npc.BuildSpawnCompanionMessages())
        {
            knubotSent |= message is StatMessage;
            locality.Announce(npc, message);
        }

        if (!knubotSent)
            locality.Announce(npc, npc.BuildKnubotDataMessage(0));
    }
}
