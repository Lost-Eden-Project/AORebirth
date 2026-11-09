namespace AORebirth.ChatModules.Channels
{
    /// <summary>
    /// One chat channel. <see cref="Type"/> and <see cref="Id"/> together are its identity on the wire and in
    /// <see cref="IChatChannels.Post"/>; <see cref="Name"/> is what clients show and may repeat across types.
    /// </summary>
    public sealed class ChatChannelInfo
    {
        public ChatChannelInfo(byte type, string typeName, uint id, string name)
        {
            Type = type;
            TypeName = typeName ?? string.Empty;
            Id = id;
            Name = name ?? string.Empty;
        }

        /// <summary>The channel type byte (for example 3 organization, 0x82 team, 0x87 general).</summary>
        public byte Type { get; }

        /// <summary>The engine's name for <see cref="Type"/>, for logs and configuration; not unique.</summary>
        public string TypeName { get; }

        public uint Id { get; }

        public string Name { get; }

        public override string ToString() => Name + " (" + TypeName + " " + Id + ")";
    }
}
