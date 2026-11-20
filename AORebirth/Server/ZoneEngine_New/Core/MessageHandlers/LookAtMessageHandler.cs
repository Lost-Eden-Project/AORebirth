namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.Quests;

    public sealed class LookAtMessageHandler : IMessageHandler<LookAtMessage>
    {
        public Type MessageBodyType => typeof(LookAtMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((LookAtMessage)body, session);
        }

        public void Handle(LookAtMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null)
                return;

            player.SetTarget(message.Target);

            if (message.Target.Instance == 0)
                return;

            Playfield? playfield = player.Playfield;
            if (playfield == null)
                return;

            if (!playfield.GetRequiredService<DynelRegistry>().TryGet(message.Target, out Dynel? dynel))
                return;

            // Find Item completes on selecting the mission's world item, only one this client was actually sent.
            if (dynel is MissionObjectiveItem objective)
            {
                if (playfield.GetService<QuestService>() is QuestService itemQuests
                    && playfield.GetRequiredService<PlayfieldLocality>().IsVisibleTo(objective, player))
                    objective.TryFindByTarget(player, itemQuests.TryEarnTargetedFindItem);
                return;
            }

            if (dynel is not Character target)
                return;

            // Credit only for an NPC this client was actually sent, so a spoofed identity cannot complete it.
            if (target is NpcCharacter npc
                && playfield.GetService<QuestService>() is QuestService quests
                && playfield.GetRequiredService<PlayfieldLocality>().IsVisibleTo(npc, player))
                quests.OnNpcTargeted(player, npc);

            // ReturnInfo=1 means InfoRequest will carry the inspect packet.
            if (message.Unknown1 == 1)
                return;

            session.Send(target.BuildInfoPacket());
        }
    }
}
