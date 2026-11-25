namespace AORebirth.ChatModules.Players
{
    using AORebirth.ChatModules.Channels;

    /// <summary>A character logged in to chat (1.2). A snapshot; immutable.</summary>
    public sealed class ChatPlayerInfo
    {
        public ChatPlayerInfo(uint characterId, string name, int gmLevel)
        {
            CharacterId = characterId;
            Name = name ?? string.Empty;
            GmLevel = gmLevel;
        }

        public uint CharacterId { get; }

        public string Name { get; }

        /// <summary>0 for a player.</summary>
        public int GmLevel { get; }

        public override string ToString() => Name + " (" + CharacterId + ")";
    }

    /// <summary>
    /// The characters logged in to chat (1.2). Every member is safe from any thread, including inside an
    /// <see cref="Moderation.IChatLineFilter"/>. BotService bots are not players and are never returned.
    /// </summary>
    public interface IChatPlayers
    {
        /// <summary>The online character with this id, or null.</summary>
        ChatPlayerInfo? Find(uint characterId);

        /// <summary>The online character with this name, ignoring case, or null.</summary>
        ChatPlayerInfo? FindByName(string name);

        /// <summary>
        /// Shows <paramref name="text"/> to one online character as a system line (1 to
        /// <see cref="IChatChannels.MaxTextLength"/> characters). Returns <see cref="ChatPostResult.RecipientOffline"/>
        /// when the character is not online. Needs <c>message-players</c> with a Capabilities entry.
        /// </summary>
        ChatPostResult SendSystemMessage(uint characterId, string text);
    }
}
