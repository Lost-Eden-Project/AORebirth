namespace ZoneEngine_New.Core.Chat
{
    /// <summary>
    /// How far a line of vicinity chat carries: the chat server's vicinity message type byte, which also picks the
    /// range the relay resolves it against.
    /// </summary>
    public enum ChatMessageType : byte
    {
        Say = 0x00,

        Whisper = 0x01,

        Shout = 0x02
    }
}
