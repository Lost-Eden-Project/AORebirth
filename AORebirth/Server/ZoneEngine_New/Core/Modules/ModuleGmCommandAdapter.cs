namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.Commands;
    using AORebirth.Modules.World;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Commands;
    using ZoneEngine_New.Core.Logging;

    /// <summary>A module's <see cref="ModuleGmCommand"/> as one of the dispatcher's commands.</summary>
    internal sealed class ModuleGmCommandAdapter : IGmCommand
    {
        private readonly string _moduleId;
        private readonly ModuleGmCommand _command;
        private readonly IZoneLogger? _logger;

        public ModuleGmCommandAdapter(string moduleId, ModuleGmCommand command, IZoneLogger? logger)
        {
            _moduleId = moduleId;
            _command = command;
            _logger = logger;
            string usage = string.IsNullOrWhiteSpace(command.Usage) ? "." + command.Name : command.Usage;
            Help = new GmCommandHelp(
                string.IsNullOrWhiteSpace(command.Summary) ? "Added by module " + moduleId + "." : command.Summary,
                [new GmHelpForm(usage, command.Summary ?? string.Empty)],
                [],
                "Added by module " + moduleId + ".");
        }

        public string Name => _command.Name;

        public int RequiredGmLevel => _command.RequiredGmLevel;

        public string Usage => Help.Forms[0].Syntax;

        public GmCommandHelp Help { get; }

        public void Execute(GmCommandContext context)
        {
            try
            {
                _command.Execute(new Invocation(context));
            }
            catch (Exception exception)
            {
                _logger?.Error(exception, "[" + _moduleId + "] GM command ." + Name + " failed.");
                throw;
            }
        }

        private sealed class Invocation(GmCommandContext context) : GmCommandInvocation
        {
            public override int CharacterId => context.Player.Identity.Instance;

            public override int PlayfieldId => context.Player.Playfield?.Identity.Instance ?? 0;

            public override int GmLevel => context.Player.Stats.GetOrZero(CharacterStat.GmLevel);

            public override IReadOnlyList<string> Args { get; } = Array.AsReadOnly(context.Args);

            public override EntityRef Target
                => context.Player.Target is { Instance: not 0 } target ? new EntityRef((int)target.Type, target.Instance) : default;

            public override void Reply(string text) => GmCommandFeedback.Send(context.Session, context.Player, text ?? string.Empty);

            public override void ReplyError(string text) => GmCommandFeedback.SendError(context.Session, context.Player, text ?? string.Empty);

            public override void ReplyUsage(string? problem = null) => context.SendUsage(problem);
        }
    }
}
