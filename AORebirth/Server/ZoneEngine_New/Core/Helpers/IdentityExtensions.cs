namespace ZoneEngine_New.Core.Helpers
{
    using LostEden.Protocol.GameData;

    /// <summary>What the engine adds to the protocol's <see cref="Identity"/>.</summary>
    public static class IdentityExtensions
    {
        /// <summary>The type in the high half and the instance in the low half: one key for an identity.</summary>
        public static ulong Long(this Identity identity)
            => ((ulong)(uint)(int)identity.Type << 32) | (uint)identity.Instance;
    }
}
