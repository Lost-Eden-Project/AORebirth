namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.announce</c>: sends a center-screen announcement (<see cref="CenterScreenAnnouncement"/>) to every online
    /// player, to the GM's playfield, or to the GM only. Each recipient's send runs on that recipient's playfield
    /// (<see cref="Playfield.DispatchPlayerProjection"/>), the same way the shutdown countdown reaches everyone.
    /// The retail client ignores it; the AORebirth client shows it.
    /// </summary>
    public sealed class AnnounceCommand : IGmCommand
    {
        readonly Lazy<PlayfieldManager> _playfields;

        public AnnounceCommand(Lazy<PlayfieldManager> playfields)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            _playfields = playfields;
        }

        public string Name => "announce";

        public int RequiredGmLevel => 1;

        public string Usage => ".announce [me|pf] <text>";

        public GmCommandHelp Help { get; } = new(
            "Shows text in the center-screen announcement window of every online player, sent as an ApplySpells "
            + "message with one inline 53247 notification spell. HTML such as <font color=#ff0000> is passed through.",
            [
                new(".announce <text>", "Send it to every online player."),
                new(".announce pf <text>", "Send it to every player on your playfield."),
                new(".announce me <text>", "Send it to yourself only."),
            ],
            [
                new(".announce Server restart in 5 minutes", "Every online player sees the line."),
            ],
            "Only the AORebirth client shows it. The retail client's ApplySpells handler ignores server messages.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string[] args = context.Args;
            string scope = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            bool onlyMe = scope == "me";
            bool playfieldOnly = scope == "pf";
            int first = onlyMe || playfieldOnly ? 1 : 0;
            if (args.Length <= first)
            {
                context.SendUsage();
                return;
            }

            string text = string.Join(" ", args, first, args.Length - first);
            if (text.Length > CenterScreenAnnouncement.MaxTextLength)
            {
                context.SendUsage(
                    "The text is longer than "
                    + CenterScreenAnnouncement.MaxTextLength.ToString(CultureInfo.InvariantCulture)
                    + " characters.");
                return;
            }

            Player gm = context.Player;
            if (onlyMe)
            {
                context.Session.Send(CenterScreenAnnouncement.Build(gm.Identity, text));
                GmCommandFeedback.Send(context.Session, gm, "Announcement sent to you.");
                return;
            }

            if (playfieldOnly && gm.Playfield is not Playfield)
            {
                GmCommandFeedback.SendError(context.Session, gm, "You are not on a playfield.");
                return;
            }

            int sent = 0;
            foreach (Player player in _playfields.Value.SnapshotPlayers())
            {
                Playfield? playfield = player.Playfield;
                if (player.Session == null || playfield == null)
                    continue;
                if (playfieldOnly && !ReferenceEquals(playfield, gm.Playfield))
                    continue;

                playfield.DispatchPlayerProjection(
                    player,
                    () => player.Session?.Send(CenterScreenAnnouncement.Build(player.Identity, text)));
                sent++;
            }

            GmCommandFeedback.Send(
                context.Session,
                gm,
                "Announcement sent to " + sent.ToString(CultureInfo.InvariantCulture) + " player(s)"
                + (playfieldOnly ? " on your playfield." : "."));
        }
    }
}
