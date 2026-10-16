namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>One way to call a command: its syntax and what it does.</summary>
    public sealed record GmHelpForm(string Syntax, string Description);

    /// <summary>A literal command line and what it does.</summary>
    public sealed record GmHelpExample(string Command, string Description);

    /// <summary>
    /// Verbose help for a GM command, shown as a popup with <c>-h</c> / <c>--help</c>, after a malformed call, or when the
    /// command throws. <see cref="Notes"/> is free text such as targeting rules.
    /// </summary>
    public sealed record GmCommandHelp(
        string Summary,
        IReadOnlyList<GmHelpForm> Forms,
        IReadOnlyList<GmHelpExample> Examples,
        string? Notes = null);

    /// <summary>Builds the help popups. Colors follow <see cref="DiagAoml"/>.</summary>
    public static class GmCommandHelpAoml
    {
        /// <summary>The help switch: <c>.give -h</c> or <c>.give --help</c>, anywhere in the arguments.</summary>
        public static bool IsHelpRequest(string[] args)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>A chat line opening <paramref name="command"/>'s help popup.</summary>
        public static string BuildLink(IGmCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return GetStatsAomlBuilder.BuildLink(BuildBody(command), "." + command.Name + " help");
        }

        /// <summary>A chat line opening the list of commands the player's GM level can run.</summary>
        public static string BuildIndexLink(IEnumerable<IGmCommand> commands, int gmLevel)
        {
            ArgumentNullException.ThrowIfNull(commands);
            var body = new StringBuilder(DiagAoml.Title("GM commands"));
            body.Append("<br>").Append(DiagAoml.Muted("Add -h to any command for its full help, e.g. .give -h"));
            var available = new List<IGmCommand>();
            foreach (IGmCommand command in commands)
            {
                if (gmLevel >= command.RequiredGmLevel)
                    available.Add(command);
            }

            available.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (IGmCommand command in available)
            {
                body.Append("<br><br>").Append(DiagAoml.Color(DiagAoml.Gold, "." + command.Name))
                    .Append("<br>").Append(DiagAoml.Indent).Append(DiagAoml.Name(Escape(command.Help.Summary)));
            }

            return GetStatsAomlBuilder.BuildLink(body.ToString(), "GM commands");
        }

        static string BuildBody(IGmCommand command)
        {
            GmCommandHelp help = command.Help;
            var body = new StringBuilder();
            body.Append(DiagAoml.Title("." + command.Name));
            body.Append("<br>").Append(DiagAoml.Name(Escape(help.Summary)));
            body.Append("<br>").Append(DiagAoml.Muted(string.Format(CultureInfo.InvariantCulture,
                "Requires GM level {0}.", command.RequiredGmLevel)));

            body.Append("<br>").Append(DiagAoml.Section("Usage"));
            foreach (GmHelpForm form in help.Forms)
            {
                body.Append("<br>").Append(Syntax(form.Syntax));
                body.Append("<br>").Append(DiagAoml.Indent).Append(DiagAoml.Muted(Escape(form.Description)));
            }

            if (help.Examples.Count > 0)
            {
                body.Append("<br>").Append(DiagAoml.Section("Examples"));
                foreach (GmHelpExample example in help.Examples)
                {
                    body.Append("<br>").Append(DiagAoml.Color(DiagAoml.Green, Escape(example.Command)));
                    body.Append("<br>").Append(DiagAoml.Indent).Append(DiagAoml.Muted(Escape(example.Description)));
                }
            }

            if (!string.IsNullOrWhiteSpace(help.Notes))
            {
                body.Append("<br>").Append(DiagAoml.Section("Notes"));
                body.Append("<br>").Append(DiagAoml.Name(Escape(help.Notes)));
            }

            body.Append("<br>").Append(DiagAoml.Section("Legend"));
            body.Append("<br>").Append(DiagAoml.Color(DiagAoml.Orange, "&lt;required&gt;")).Append(DiagAoml.Indent)
                .Append(DiagAoml.Color(DiagAoml.Yellow, "[optional]")).Append(DiagAoml.Indent)
                .Append(DiagAoml.Color(DiagAoml.Cyan, "a|b")).Append(DiagAoml.Muted(" one of"));
            return body.ToString();
        }

        /// <summary>Command words white, &lt;required&gt; arguments orange, [optional] ones yellow.</summary>
        static string Syntax(string syntax)
        {
            var result = new StringBuilder();
            foreach (string word in syntax.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (result.Length > 0)
                    result.Append(' ');
                string color = word.StartsWith('<') ? DiagAoml.Orange
                    : word.StartsWith('[') || word.EndsWith(']') ? DiagAoml.Yellow
                    : word.Contains('|') ? DiagAoml.Cyan
                    : DiagAoml.White;
                result.Append(DiagAoml.Color(color, Escape(word)));
            }

            return result.ToString();
        }

        /// <summary>Help text as AOML: angle brackets shown literally, no double quote to end the popup's href.</summary>
        static string Escape(string text)
            => text.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace('"', '\'');
    }
}
