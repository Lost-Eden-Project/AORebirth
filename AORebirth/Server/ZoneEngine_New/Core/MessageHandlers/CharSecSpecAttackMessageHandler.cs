namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Client special-attack request: attacker, target and the special's stat id. The special is queued against the
    /// target; the request is echoed back only when it fires (SpecialAttacks.TryFire).
    /// </summary>
    public sealed class CharSecSpecAttackMessageHandler : IMessageHandler<CharSecSpecAttackMessage>
    {
        public Type MessageBodyType => typeof(CharSecSpecAttackMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((CharSecSpecAttackMessage)body, session);
        }

        public void Handle(CharSecSpecAttackMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null || player.IsDead || player.IsPersistenceQuarantined
                || !ReferenceEquals(player.Session, session))
                return;

            Playfield? playfield = player.Playfield;
            if (playfield == null)
                return;

            // No echo here: the client holds an echoed special until its SpecialAttackInfo or the fight's end, one at a
            // time, and shows the next special's hit under the held one's name. TryFire echoes it when it fires.
            if (!playfield.GetRequiredService<DynelRegistry>().TryGet(message.Target, out Dynel? dynel)
                || dynel is not Character target || target.IsDead)
            {
                Modules.ModuleHooks.ActionAttempted(player, AORebirth.Modules.Events.PlayerActionKind.SpecialAttack, message.Stat,
                    AORebirth.Modules.Events.PlayerActionOutcome.Refused, AORebirth.Modules.Events.ActionRefusal.Target,
                    target: message.Target, detail: "no living target");
                return;
            }

            // Queued, then tried at once: standing in reach it fires on this tick, as before.
            if (SpecialAttacks.Queue(player, target, (CharacterStat)message.Stat, message.Unknown))
                player.TickQueuedSpecials();
        }
    }
}
