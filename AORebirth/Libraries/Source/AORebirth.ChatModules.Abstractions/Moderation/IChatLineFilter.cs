namespace AORebirth.ChatModules.Moderation
{
    /// <summary>What an <see cref="IChatLineFilter"/> decides for a line (1.2).</summary>
    public enum ChatFilterAction
    {
        /// <summary>The line goes on unchanged.</summary>
        Pass = 0,

        /// <summary>The line goes on with <see cref="ChatFilterResult.Text"/> instead.</summary>
        Replace = 1,

        /// <summary>Nobody receives the line; the speaker gets <see cref="ChatFilterResult.Notice"/> when there is one.</summary>
        Block = 2,
    }

    /// <summary>The answer of <see cref="IChatLineFilter.Filter"/> (1.2). Use <see cref="Pass"/>, <see cref="Replace"/> or <see cref="Block"/>.</summary>
    public sealed class ChatFilterResult
    {
        /// <summary>Leave the line as it is. Shared; costs nothing.</summary>
        public static readonly ChatFilterResult Pass = new ChatFilterResult(ChatFilterAction.Pass, null, null);

        private ChatFilterResult(ChatFilterAction action, string? text, string? notice)
        {
            Action = action;
            Text = text;
            Notice = notice;
        }

        public ChatFilterAction Action { get; }

        /// <summary>The replacement text for <see cref="ChatFilterAction.Replace"/>; null otherwise.</summary>
        public string? Text { get; }

        /// <summary>The system line the speaker sees for <see cref="ChatFilterAction.Block"/>; null for none.</summary>
        public string? Notice { get; }

        /// <summary>
        /// Send <paramref name="text"/> in place of the line. It must be 1 to <see cref="Channels.IChatChannels.MaxTextLength"/>
        /// characters; anything else is ignored (the line goes on as it was) and logged.
        /// </summary>
        public static ChatFilterResult Replace(string text) => new ChatFilterResult(ChatFilterAction.Replace, text, null);

        /// <summary>
        /// Deliver the line to nobody: no channel member, vicinity listener, bot or module sees it. It is still written to
        /// the chat log, marked blocked. <paramref name="notice"/>, when given, is shown to the speaker as a system line.
        /// </summary>
        public static ChatFilterResult Block(string? notice = null) => new ChatFilterResult(ChatFilterAction.Block, null, notice);
    }

    /// <summary>
    /// Sees every player line in a channel and every player say, whisper and shout before anyone receives it, and may
    /// replace or block it (1.2). Registered with <see cref="IChatModuleContext.RegisterLineFilter"/>.
    /// <para>
    /// <see cref="Filter"/> runs synchronously on the thread delivering the line (a client's network thread for a channel,
    /// the World link for vicinity), so it must be fast and in memory only: no I/O, no waiting, no long locks. Keep state
    /// such as gags in memory and persist it from another thread.
    /// </para>
    /// <para>
    /// Filters run in the order their modules load (Config/ChatModules.json <c>Enabled</c>); each sees the text the
    /// previous one left in <see cref="ChatLine.Text"/>, and the first <see cref="ChatFilterAction.Block"/> ends the
    /// chain. A filter that throws is logged and the line goes on as it was. Bot lines, module posts, NPC speech, chat
    /// commands ('.', '/' and registered module commands) and tells are never filtered.
    /// </para>
    /// </summary>
    public interface IChatLineFilter
    {
        ChatFilterResult Filter(ChatLine line);
    }
}
