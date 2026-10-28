namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.online</c>: every player online, grouped by real profession (not VisualProfession), highest level first.
    /// Open to everyone, so each caller is held to a short cooldown. A caller who may use .tp gets a [TP] link beside
    /// every other named player that says <c>.tp to &lt;name&gt;</c>.
    /// </summary>
    public sealed class OnlineCommand : IGmCommand
    {
        static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

        readonly Lazy<PlayfieldManager> _playfields;
        readonly ZoneEngine_New.Core.World.WorldLink? _world;
        readonly ConcurrentDictionary<int, DateTime> _lastUseUtc = new();

        /// <summary>The GM level that runs .tp, so the list offers teleport links only to those who can use them.</summary>
        readonly int _teleportGmLevel;

        public OnlineCommand(Lazy<PlayfieldManager> playfields, ZoneEngine_New.Core.World.WorldLink? world = null,
            Configuration.GmPermissions? permissions = null)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            _playfields = playfields;
            _world = world;
            _teleportGmLevel = permissions?.CommandLevel("tp", TeleportCommand.GmLevel) ?? TeleportCommand.GmLevel;
        }

        sealed record Entry(string Name, bool Teleportable, int Level, int Profession);

        public string Name => "online";

        public int RequiredGmLevel => 0;

        public string Usage => ".online";

        public GmCommandHelp Help { get; } = new(
            "Lists every player online, grouped by profession, highest level first.",
            [
                new(".online", "The online list."),
            ],
            [
                new(".online", "Who is on."),
            ],
            "Open to everyone, with a short cooldown. Callers who may use .tp get a [TP] link beside each player.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            DateTime nowUtc = DateTime.UtcNow;
            int callerId = context.Player.Identity.Instance;
            if (_lastUseUtc.TryGetValue(callerId, out DateTime lastUtc) && nowUtc - lastUtc < Cooldown)
            {
                GmCommandFeedback.Send(context.Session, context.Player, "Please wait a moment before using .online again.");
                return;
            }

            _lastUseUtc[callerId] = nowUtc;
            bool canTeleport = context.Player.Stats.GetOrZero(CharacterStat.GmLevel) >= _teleportGmLevel;

            // Characters hidden with .visibility off are left off the list for players; GMs still see them.
            bool showHidden = context.Player.IsGm;

            if (_world != null)
            {
                // Every zone instance: World's directory. The reply is sent from the caller's own tick.
                Player caller = context.Player;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    List<Entry> online;
                    try
                    {
                        AORebirth.World.Contracts.V1.ListOnlineReply reply = await _world.ListOnlineAsync().ConfigureAwait(false);
                        online = reply.Characters
                            .Where(character => showHidden || !Player.IsHiddenCharacter(character.CharacterId))
                            .Select(character => new Entry(
                            string.IsNullOrEmpty(character.Name) ? character.CharacterId.ToString(CultureInfo.InvariantCulture) : character.Name,
                            canTeleport && !string.IsNullOrEmpty(character.Name) && character.CharacterId != callerId,
                            character.Level,
                            character.Profession)).ToList();
                    }
                    catch (Exception)
                    {
                        online = LocalEntries(canTeleport, callerId, showHidden);
                    }

                    caller.Playfield?.DispatchPlayerProjection(caller, () => Send(context, online));
                });
                return;
            }

            Send(context, LocalEntries(canTeleport, callerId, showHidden));
        }

        List<Entry> LocalEntries(bool canTeleport, int callerId, bool showHidden)
            => _playfields.Value.SnapshotPlayers()
                .Where(player => showHidden || !player.HiddenFromPlayers)
                .Select(player => new Entry(
                    string.IsNullOrEmpty(player.Name)
                        ? player.Identity.Instance.ToString(CultureInfo.InvariantCulture)
                        : player.Name,
                    canTeleport && !string.IsNullOrEmpty(player.Name) && player.Identity.Instance != callerId,
                    player.Stats.GetOrZero(CharacterStat.Level),
                    player.Stats.GetOrZero(CharacterStat.Profession)))
                .ToList();

        static void Send(GmCommandContext context, List<Entry> entries)
        {

            // One block per profession so a page break never separates a heading from its players.
            var blocks = new List<string>();
            foreach (var group in entries
                .GroupBy(entry => entry.Profession)
                .OrderBy(group => ProfessionName(group.Key), StringComparer.Ordinal))
            {
                var block = new System.Text.StringBuilder();
                block.Append(DiagAoml.Section(ProfessionName(group.Key)))
                    .Append(' ')
                    .Append(DiagAoml.Muted("(" + group.Count().ToString(CultureInfo.InvariantCulture) + ")"));

                foreach (var entry in group
                    .OrderByDescending(entry => entry.Level)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    block.Append("<br>")
                        .Append(DiagAoml.Indent)
                        .Append(DiagAoml.Name(DiagAoml.Safe(entry.Name)))
                        .Append("&#160;&#160;")
                        .Append(DiagAoml.Label("Lvl"))
                        .Append(' ')
                        .Append(DiagAoml.Color(LevelColor(entry.Level), entry.Level.ToString(CultureInfo.InvariantCulture)));
                    if (entry.Teleportable)
                        block.Append("&#160;&#160;").Append(DiagAoml.Link(".tp to " + DiagAoml.Safe(entry.Name), "[TP]"));
                }

                blocks.Add(block.ToString());
            }

            string title = string.Format(CultureInfo.InvariantCulture, "Players Online ({0})", entries.Count);
            GmCommandFeedback.SendLines(context.Session, context.Player, DiagAoml.Pages(title, header: null, blocks));
        }

        /// <summary>Level tiers at a glance: grey under 50, green under 100, cyan under 150, yellow under 200, orange 200+.</summary>
        static string LevelColor(int level) => level switch
        {
            >= 200 => DiagAoml.Orange,
            >= 150 => DiagAoml.Yellow,
            >= 100 => DiagAoml.Cyan,
            >= 50 => DiagAoml.Green,
            _ => DiagAoml.Grey
        };

        static string ProfessionName(int profession)
        {
            if (profession <= 0 || !Enum.IsDefined(typeof(Profession), profession))
                return string.Format(CultureInfo.InvariantCulture, "Unknown ({0})", profession);

            string raw = ((Profession)profession).ToString();
            var spaced = new System.Text.StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && char.IsUpper(raw[i]))
                    spaced.Append(' ');
                spaced.Append(raw[i]);
            }

            return spaced.ToString();
        }
    }
}
