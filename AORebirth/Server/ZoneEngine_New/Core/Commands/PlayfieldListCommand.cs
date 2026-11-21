namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Linq;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// <c>/list</c>: a popup link of the players in the caller's own playfield, laid out like <c>.online</c>. A player
    /// the caller cannot see (hidden with .visibility off, or sneaking past the caller's Perception) is left off, so the
    /// list never reveals more than the world does. Callers who may use .tp get a [TP] link beside each other player.
    /// </summary>
    public sealed class PlayfieldListCommand
    {
        static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

        readonly ConcurrentDictionary<int, DateTime> _lastUseUtc = new();
        readonly int _teleportGmLevel;

        public PlayfieldListCommand(Configuration.GmPermissions? permissions = null)
            => _teleportGmLevel = permissions?.CommandLevel("tp", TeleportCommand.GmLevel) ?? TeleportCommand.GmLevel;

        public void Execute(Player caller, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(session);

            DateTime nowUtc = DateTime.UtcNow;
            int callerId = caller.Identity.Instance;
            if (_lastUseUtc.TryGetValue(callerId, out DateTime lastUtc) && nowUtc - lastUtc < Cooldown)
            {
                GmCommandFeedback.Send(session, caller, "Please wait a moment before using /list again.");
                return;
            }

            DynelRegistry? registry = caller.Playfield?.GetService<DynelRegistry>();
            if (registry == null)
                return;

            _lastUseUtc[callerId] = nowUtc;
            bool canTeleport = caller.Stats.GetOrZero(CharacterStat.GmLevel) >= _teleportGmLevel;
            var entries = registry.PlayerEntities()
                .Where(player => !string.IsNullOrEmpty(player.Name))
                .Where(player => player.Identity.Instance == callerId || !LocalityVisibility.IsHiddenFrom(caller, player))
                .Select(player => new OnlineCommand.Entry(
                    player.Name!,
                    canTeleport && player.Identity.Instance != callerId,
                    player.Stats.GetOrZero(CharacterStat.Level),
                    player.Stats.GetOrZero(CharacterStat.Profession)))
                .ToList();

            string title = string.Format(CultureInfo.InvariantCulture, "Players in Playfield ({0})", entries.Count);
            GmCommandFeedback.SendLines(session, caller, OnlineCommand.Pages(title, entries));
        }
    }
}
