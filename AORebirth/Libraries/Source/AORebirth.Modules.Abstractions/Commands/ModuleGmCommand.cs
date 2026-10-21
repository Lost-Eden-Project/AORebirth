namespace AORebirth.Modules.Commands
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>
    /// A ".name args" GM command a module adds (module API 1.2), registered with
    /// <see cref="IZoneModuleContext.RegisterGmCommand"/> during <see cref="IZoneModule.Initialize"/>. It runs exactly like a
    /// built-in one: typed in vicinity chat or relayed from the chat server, with the same GM level check and the same
    /// "-h" help popup. A name a built-in command or another module already uses fails this module's startup.
    /// </summary>
    public sealed class ModuleGmCommand
    {
        /// <summary>The name typed after the dot: letters, digits, '-' and '_', compared ignoring case.</summary>
        public required string Name { get; init; }

        /// <summary>The lowest GmLevel that may run it; 0 lets every player run it.</summary>
        public int RequiredGmLevel { get; init; } = 1;

        /// <summary>One line of syntax, e.g. ".knubotreload [scriptId]".</summary>
        public string Usage { get; init; } = string.Empty;

        /// <summary>What the command does, for the help popup and the command index.</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// The command. It runs synchronously on the issuing player's playfield tick, so like an event handler it must be
        /// fast and never block; hand slow work to another thread and answer with <see cref="World.IZoneActions"/>. If it
        /// throws, the player sees "Command failed" with the message and the exception is logged.
        /// </summary>
        public required Action<GmCommandInvocation> Execute { get; init; }
    }

    /// <summary>One run of a <see cref="ModuleGmCommand"/>. Valid only while the handler runs.</summary>
    public abstract class GmCommandInvocation
    {
        /// <summary>The issuing player's character id.</summary>
        public abstract int CharacterId { get; }

        /// <summary>The playfield the issuer is on.</summary>
        public abstract int PlayfieldId { get; }

        /// <summary>The issuer's GmLevel.</summary>
        public abstract int GmLevel { get; }

        /// <summary>The words after the command name, split on whitespace.</summary>
        public abstract IReadOnlyList<string> Args { get; }

        /// <summary>The issuer's selected target, or default when none.</summary>
        public abstract EntityRef Target { get; }

        /// <summary>A line of text in the issuer's chat window.</summary>
        public abstract void Reply(string text);

        /// <summary>A red error line in the issuer's chat window.</summary>
        public abstract void ReplyError(string text);

        /// <summary>The red "invalid arguments" line and a link to the command's help popup.</summary>
        public abstract void ReplyUsage(string? problem = null);
    }
}
