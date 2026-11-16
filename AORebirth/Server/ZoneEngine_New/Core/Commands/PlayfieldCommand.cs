namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Globalization;
    using System.Threading.Tasks;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.playfield reload [id]</c>: rebuilds a running playfield from its GameData read again from disk. Everyone on it
    /// is saved, the old instance is torn down, and they zone into the new one where they stood
    /// (<see cref="PlayfieldManager.TryStartReload"/>).
    /// </summary>
    public sealed class PlayfieldCommand : IGmCommand
    {
        /// <summary>How long the result waits for the issuer to finish zoning back in before it is only logged.</summary>
        static readonly TimeSpan ReportWait = TimeSpan.FromSeconds(30);

        readonly Lazy<PlayfieldManager> _playfields;

        public PlayfieldCommand(Lazy<PlayfieldManager> playfields)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            _playfields = playfields;
        }

        public string Name => "playfield";

        public int RequiredGmLevel => 1;

        public string Usage => ".playfield reload [playfieldId]";

        public GmCommandHelp Help { get; } = new(
            "Rebuilds a running playfield from GameData.",
            [
                new(".playfield reload", "Saves everyone on your playfield, tears it down, builds a new one from its GameData files and zones them back in where they stood."),
                new(".playfield reload <playfieldId>", "The same for another playfield running on this zone instance."),
            ],
            [
                new(".playfield reload", "Pick up edits to this playfield's Spawns.json, Npcs or other files."),
                new(".playfield reload 566", "Reload playfield 566."),
            ],
            "Quest dungeons cannot be reloaded. Anyone who cannot be moved within 30 seconds (link-dead, already zoning) is saved and logged out.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Args.Length == 0 || !string.Equals(context.Args[0], "reload", StringComparison.OrdinalIgnoreCase)
                || context.Args.Length > 2)
            {
                context.SendUsage();
                return;
            }

            Player gm = context.Player;
            Playfield? target;
            if (context.Args.Length == 2)
            {
                if (!int.TryParse(context.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
                    || playfieldId <= 0)
                {
                    context.SendUsage();
                    return;
                }

                if (!_playfields.Value.TryGet(playfieldId, out target) || target == null)
                {
                    GmCommandFeedback.Send(context.Session, gm, "Playfield "
                        + playfieldId.ToString(CultureInfo.InvariantCulture) + " is not running on this zone instance.");
                    return;
                }
            }
            else
            {
                target = gm.Playfield;
                if (target == null)
                {
                    GmCommandFeedback.Send(context.Session, gm, "Not on a playfield.");
                    return;
                }
            }

            int id = target.Identity.Instance;
            string? refused = _playfields.Value.TryStartReload(target, outcome => Report(gm, outcome));
            if (refused != null)
            {
                GmCommandFeedback.SendError(context.Session, gm, refused);
                return;
            }

            GmCommandFeedback.Send(context.Session, gm, "Reloading playfield " + id.ToString(CultureInfo.InvariantCulture)
                + " from GameData; everyone on it will zone back in.");
        }

        /// <summary>The issuer may still be zoning into the new instance: wait for them to be back in play.</summary>
        static void Report(Player gm, string outcome)
        {
            _ = Task.Run(async () =>
            {
                DateTime deadline = DateTime.UtcNow + ReportWait;
                while (DateTime.UtcNow < deadline)
                {
                    if (gm.Session is IZoneSession session && session.State == SessionState.InPlay && gm.Playfield is Playfield here)
                    {
                        here.DispatchPlayerProjection(gm, () =>
                        {
                            if (gm.Session is IZoneSession current)
                                GmCommandFeedback.Send(current, gm, outcome);
                        });
                        return;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }
            });
        }
    }
}
