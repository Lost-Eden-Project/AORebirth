namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    public sealed class GmCommandContext
    {
        public GmCommandContext(IZoneSession session, Player player, string[] args, IGmCommand? command = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Player = player ?? throw new ArgumentNullException(nameof(player));
            Args = args ?? Array.Empty<string>();
            Command = command;
        }

        public IZoneSession Session { get; }

        public Player Player { get; }

        public string[] Args { get; }

        /// <summary>The command being run; null when a context is built outside the dispatcher.</summary>
        public IGmCommand? Command { get; }

        /// <summary>
        /// A malformed call: <paramref name="problem"/> in red (default: invalid arguments), then a link to the
        /// command's help popup.
        /// </summary>
        public void SendUsage(string? problem = null)
        {
            if (Command == null)
            {
                GmCommandFeedback.Send(Session, Player, problem ?? "Invalid arguments.");
                return;
            }

            GmCommandFeedback.SendError(Session, Player, problem ?? "Invalid arguments for ." + Command.Name + ".");
            GmCommandFeedback.Send(Session, Player, GmCommandHelpAoml.BuildLink(Command));
        }

        /// <summary>Look-at player if selected, otherwise the command issuer.</summary>
        public Player ResolveSubject()
        {
            if (TryResolveSubject(out Player subject, requirePlayerTarget: false))
                return subject;
            return Player;
        }

        /// <summary>
        /// Look-at character (player or NPC) if selected, otherwise the command issuer.
        /// Sends feedback and returns false when a look-at target is set but is not a character.
        /// </summary>
        public bool TryResolveCharacter(out Character subject)
        {
            subject = Player;
            Identity target = Player.Target;
            if (target == Identity.None || target.Instance == 0 || target == Player.Identity)
                return true;

            Playfield? playfield = Player.Playfield;
            if (playfield == null)
            {
                GmCommandFeedback.Send(Session, Player, "Not on a playfield.");
                return false;
            }

            if (TryFindTargetCharacter(playfield, target, out subject))
                return true;

            subject = Player;
            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            Identity canonical = CanbeAffected(target.Instance);
            if (registry.TryGet(target, out _) || registry.TryGet(canonical, out _))
            {
                GmCommandFeedback.Send(Session, Player, "Target is not a character.");
                return false;
            }

            GmCommandFeedback.Send(Session, Player, "Unknown target.");
            return false;
        }

        /// <summary>
        /// Resolves the look-at player when one is selected.
        /// When <paramref name="requirePlayerTarget"/> is true and a look-at target is set but is
        /// not a player, sends feedback and returns false (does not fall back to self).
        /// </summary>
        public bool TryResolveSubject(out Player subject, bool requirePlayerTarget = true)
        {
            subject = Player;
            Identity target = Player.Target;
            if (target == Identity.None || target.Instance == 0 || target == Player.Identity)
                return true;

            Playfield? playfield = Player.Playfield;
            if (playfield == null)
            {
                if (requirePlayerTarget)
                    GmCommandFeedback.Send(Session, Player, "Not on a playfield.");
                return !requirePlayerTarget;
            }

            if (TryFindTargetPlayer(playfield, target, out subject))
                return true;

            subject = Player;
            if (!requirePlayerTarget)
                return true;

            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            Identity canonical = CanbeAffected(target.Instance);
            if (registry.TryGet(target, out _) || registry.TryGet(canonical, out _))
            {
                GmCommandFeedback.Send(Session, Player, "Target is not a player.");
                return false;
            }

            GmCommandFeedback.Send(Session, Player, "Unknown target.");
            return false;
        }

        static bool TryFindTargetPlayer(Playfield playfield, Identity target, out Player subject)
        {
            if (TryFindTargetCharacter(playfield, target, out Character character) && character is Player player)
            {
                subject = player;
                return true;
            }

            subject = null!;
            return false;
        }

        static bool TryFindTargetCharacter(Playfield playfield, Identity target, out Character subject)
        {
            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            if (registry.TryGet(target, out Dynel? dynel) && dynel is Character direct)
            {
                subject = direct;
                return true;
            }

            // Look-at sometimes carries a mismatched Type; characters are CanbeAffected.
            Identity canonical = CanbeAffected(target.Instance);
            if (target != canonical
                && registry.TryGet(canonical, out dynel)
                && dynel is Character byType)
            {
                subject = byType;
                return true;
            }

            if (playfield.GetRequiredService<PlayfieldManager>().FindPlayer(target.Instance, out Player found))
            {
                subject = found;
                return true;
            }

            subject = null!;
            return false;
        }

        static Identity CanbeAffected(int instance)
            => new Identity { Type = IdentityType.CanbeAffected, Instance = instance };
    }

    public interface IGmCommand
    {
        string Name { get; }

        int RequiredGmLevel { get; }

        string Usage { get; }

        /// <summary>Verbose help: the popup for <c>-h</c>, a malformed call, or a failure.</summary>
        GmCommandHelp Help { get; }

        void Execute(GmCommandContext context);
    }

    public static class GmCommandFeedback
    {
        public static void Send(IZoneSession session, Player player, string text)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(text);

            session.Send(
                new ChatTextMessage
                {
                    Identity = player.Identity,
                    Text = text,
                    Unknown1 = 0,
                    Unknown2 = 0,
                    Unknown3 = 0
                });
        }

        /// <summary>A command error line in red.</summary>
        public static void SendError(IZoneSession session, Player player, string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            Send(session, player, DiagAoml.Color(DiagAoml.Red, text.Replace("<", "(", StringComparison.Ordinal)
                .Replace(">", ")", StringComparison.Ordinal)));
        }

        public static void SendLines(
            IZoneSession session,
            Player player,
            IEnumerable<string> lines,
            int maxLines = 200)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(lines);

            int count = 0;
            foreach (string line in lines)
            {
                if (count >= maxLines)
                {
                    Send(session, player, "... truncated");
                    return;
                }

                Send(session, player, line);
                count++;
            }
        }
    }

    public sealed class GmCommandDispatcher
    {
        private readonly Dictionary<string, IGmCommand> _commands;

        public GmCommandDispatcher(
            IEnumerable<IGmCommand> commands,
            ZoneEngine_New.Core.Modules.ZoneModuleHost? modules = null,
            ZoneEngine_New.Core.Logging.IZoneLogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(commands);
            _commands = new Dictionary<string, IGmCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (IGmCommand command in commands)
            {
                ArgumentNullException.ThrowIfNull(command);
                _commands[command.Name] = command;
            }

            // Module commands never replace a built-in: a clash unloads that module (BindGmCommands).
            if (modules != null)
            {
                foreach ((string moduleId, AORebirth.Modules.Commands.ModuleGmCommand command) in modules.BindGmCommands(_commands.Keys.ToArray()))
                    _commands.Add(command.Name, new ZoneEngine_New.Core.Modules.ModuleGmCommandAdapter(moduleId, command, logger));
            }
        }

        /// <summary>
        /// Runs <paramref name="text"/> when it is a ".name args" command line (vicinity chat, or a chat-server channel
        /// or tell relayed through World). False when it is not a command.
        /// </summary>
        public bool TryExecuteText(IZoneSession session, Player player, string? text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '.')
                return false;

            string trimmed = text.TrimStart('.');
            string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return true;

            TryExecute(session, player, parts[0], parts.Length > 1 ? parts[1..] : Array.Empty<string>());
            return true;
        }

        public void TryExecute(IZoneSession session, Player player, string name, string[] args)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(name);
            args ??= Array.Empty<string>();

            // A player with no GmLevel stat is level 0, which still runs RequiredGmLevel 0 commands such as .online.
            int gmLevel = player.Stats.GetOrZero(CharacterStat.GmLevel);
            if (!_commands.TryGetValue(name, out IGmCommand? command))
            {
                GmCommandFeedback.SendError(session, player, "Unknown command: ." + name);
                GmCommandFeedback.Send(session, player, GmCommandHelpAoml.BuildIndexLink(_commands.Values, gmLevel));
                return;
            }

            if (gmLevel < command.RequiredGmLevel)
            {
                GmCommandFeedback.SendError(session, player, "Insufficient GM level.");
                return;
            }

            var context = new GmCommandContext(session, player, args, command);
            if (GmCommandHelpAoml.IsHelpRequest(args))
            {
                GmCommandFeedback.Send(session, player, GmCommandHelpAoml.BuildLink(command));
                return;
            }

            try
            {
                command.Execute(context);
            }
            catch (Exception exception)
            {
                context.SendUsage(string.Format(CultureInfo.InvariantCulture, "Command failed: {0}", exception.Message));
            }
        }
    }
}
