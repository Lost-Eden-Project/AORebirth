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
    /// (<see cref="PlayfieldManager.TryStartReload"/>). <c>.playfield spawns reload [id]</c>: applies its Spawns.json and
    /// Districts.json read again to the running playfield in place, nobody zoned
    /// (<see cref="PlayfieldManager.ReloadRunningPlayfieldSpawns"/>).
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

        public string Usage => ".playfield reload [playfieldId] | .playfield spawns reload [playfieldId]";

        public GmCommandHelp Help { get; } = new(
            "Rebuilds a running playfield from GameData.",
            [
                new(".playfield reload", "Saves everyone on your playfield, tears it down, builds a new one from its GameData files and zones them back in where they stood."),
                new(".playfield reload <playfieldId>", "The same for another playfield running on this zone instance."),
                new(".playfield spawns reload [playfieldId]", "Applies the playfield's Spawns.json and Districts.json read again (staff spawn edits included) without moving anyone: unchanged points keep their NPCs, removed and changed ones despawn, new ones spawn."),
            ],
            [
                new(".playfield reload", "Pick up edits to this playfield's Spawns.json, Npcs or other files."),
                new(".playfield reload 566", "Reload playfield 566."),
                new(".playfield spawns reload", "Pick up a spawn point edit on your playfield."),
            ],
            "Quest dungeons cannot be reloaded. Anyone who cannot be moved within 30 seconds (link-dead, already zoning) is saved and logged out.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            bool spawns = context.Args.Length >= 2
                && string.Equals(context.Args[0], "spawns", StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Args[1], "reload", StringComparison.OrdinalIgnoreCase);
            int idArg = spawns ? 2 : 1;
            if ((!spawns && (context.Args.Length == 0
                    || !string.Equals(context.Args[0], "reload", StringComparison.OrdinalIgnoreCase)))
                || context.Args.Length > idArg + 1)
            {
                context.SendUsage();
                return;
            }

            Player gm = context.Player;
            Playfield? target;
            if (context.Args.Length == idArg + 1)
            {
                if (!int.TryParse(context.Args[idArg], NumberStyles.Integer, CultureInfo.InvariantCulture, out int playfieldId)
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
            if (spawns)
            {
                GmCommandFeedback.Send(context.Session, gm, "Reloading the spawn points of playfield "
                    + id.ToString(CultureInfo.InvariantCulture) + " from GameData...");
                PlayfieldManager manager = _playfields.Value;
                // Off the tick: it reads the files and waits for the playfield's tick (possibly this one) to apply them.
                _ = Task.Run(() => Report(gm, DescribeSpawnReload(manager.ReloadRunningPlayfieldSpawns([id]))));
                return;
            }

            string? refused = _playfields.Value.TryStartReload(target, outcome => Report(gm, outcome));
            if (refused != null)
            {
                GmCommandFeedback.SendError(context.Session, gm, refused);
                return;
            }

            GmCommandFeedback.Send(context.Session, gm, "Reloading playfield " + id.ToString(CultureInfo.InvariantCulture)
                + " from GameData; everyone on it will zone back in.");
        }

        /// <summary>Chat lines, one per playfield: what was kept, added, changed and removed, then its problems (at most five).</summary>
        internal static string[] DescribeSpawnReload(AORebirth.Modules.Content.PlayfieldReloadResult result)
        {
            var lines = new System.Collections.Generic.List<string>(result.Problems);
            foreach (AORebirth.Modules.Content.PlayfieldSpawnReload playfield in result.Playfields)
            {
                string id = playfield.PlayfieldId.ToString(CultureInfo.InvariantCulture);
                lines.Add(playfield.Applied
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Playfield {0} spawn points reloaded: {1} kept, {2} added, {3} changed, {4} removed ({5} despawned).",
                        id, playfield.Kept, playfield.Added, playfield.Changed, playfield.Removed, playfield.Despawned)
                    : "Playfield " + id + " spawn points not reloaded.");
                for (int i = 0; i < playfield.Problems.Count && i < 5; i++)
                    lines.Add("  " + playfield.Problems[i]);
                if (playfield.Problems.Count > 5)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "  ... and {0} more (see the zone log)",
                        playfield.Problems.Count - 5));
            }

            return lines.Count == 0 ? ["No playfield spawn points were reloaded."] :lines.ToArray();
        }

        /// <summary>The issuer may still be zoning into the new instance: wait for them to be back in play.</summary>
        static void Report(Player gm, params string[] lines)
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
                                GmCommandFeedback.SendLines(current, gm, lines);
                        });
                        return;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }
            });
        }
    }
}
