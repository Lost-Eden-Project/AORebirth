namespace ZoneEngine_New.Core.Quests.Dungeons.Static
{
    /// <summary>
    /// A static dungeon instance takes one number from the durable item-instance allocator, like a quest dungeon, and
    /// its Playfield2 id is that number above <see cref="FirstPlayfield"/>: the upper half of the dungeon range, so a
    /// stored playfield tells a static instance from a quest dungeon without any lookup
    /// (<see cref="QuestDungeonIds.IsDungeonPlayfield"/> still covers both: neither is ever built as an ordinary playfield).
    /// </summary>
    public static class StaticDungeonIds
    {
        /// <summary>First static instance Playfield2 id; quest dungeons stay below it.</summary>
        public const int FirstPlayfield = 0x40000000;

        public static bool IsStaticDungeonPlayfield(int playfieldId)
            => playfieldId >= FirstPlayfield && playfieldId < ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.First;

        public static bool TryCreate(int number, out int playfieldId)
        {
            playfieldId = 0;
            if (number <= 0 || number >= ZoneEngine_New.Core.Playfield.ProxyPlayfieldIds.First - FirstPlayfield)
                return false;
            playfieldId = FirstPlayfield + number;
            return true;
        }
    }
}
