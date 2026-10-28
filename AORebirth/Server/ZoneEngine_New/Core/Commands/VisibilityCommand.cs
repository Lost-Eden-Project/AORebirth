namespace ZoneEngine_New.Core.Commands
{
    using System;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// <c>.visibility [on|off]</c>: with off, the GM is not spawned on players' clients (see
    /// <see cref="Player.HiddenFromPlayers"/>); other GMs still see them. Players who see the GM now get a despawn.
    /// </summary>
    public sealed class VisibilityCommand : IGmCommand
    {
        public string Name => "visibility";

        public int RequiredGmLevel => 100;

        public string Usage => ".visibility [on|off]";

        public GmCommandHelp Help { get; } = new(
            "Shows or hides you from players. While hidden you are not spawned on their clients at all and are left off "
            + "their .online list; GMs still see you.",
            [
                new(".visibility", "Whether you are visible to players now."),
                new(".visibility off", "Hide from players. Anyone who sees you now loses you."),
                new(".visibility on", "Be seen again."),
            ],
            [
                new(".visibility off", "Watch an area without players knowing you are there."),
            ],
            "Hiding lasts through zoning and relogging until you turn it on or the zone engine restarts. NPCs ignore you "
            + "while hidden: they do not aggro or attack, and any fighting you let you go.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            Player player = context.Player;
            string[] args = context.Args;
            if (args.Length == 0)
            {
                Report(context, player.HiddenFromPlayers ? "You are hidden from players." : "You are visible to players.");
                return;
            }

            bool hide;
            switch (args.Length == 1 ? args[0].ToLowerInvariant() : string.Empty)
            {
                case "off":
                    hide = true;
                    break;
                case "on":
                    hide = false;
                    break;
                default:
                    context.SendUsage();
                    return;
            }

            if (player.HiddenFromPlayers == hide)
            {
                Report(context, hide ? "You are already hidden from players." : "You are already visible to players.");
                return;
            }

            player.HiddenFromPlayers = hide;
            if (player.Playfield is Playfield playfield)
                playfield.GetRequiredService<PlayfieldLocality>().ReconcileVisibility(player);

            Report(context, hide ? "You are now hidden from players." : "You are now visible to players.");
        }

        static void Report(GmCommandContext context, string text)
            => GmCommandFeedback.Send(context.Session, context.Player, text);
    }
}
