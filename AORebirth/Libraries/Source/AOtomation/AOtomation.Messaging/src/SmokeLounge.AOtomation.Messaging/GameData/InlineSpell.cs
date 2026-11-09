namespace SmokeLounge.AOtomation.Messaging.GameData
{
    using System;

    /// <summary>
    /// One spell function as the client's GameData::SpellData_t reads it from a stream (GameData.dll operator&gt;&gt;
    /// 0x1000d686): function type, a second header int, the format version, the criteria, then the format's
    /// arguments in order. Integer arguments are int32; string arguments are an int32 byte length then that many
    /// Latin-1 bytes with no terminator. The arguments must match the client's format for <see cref="FunctionType"/>
    /// and <see cref="Version"/> exactly, or the client fails the whole stream.
    /// </summary>
    public sealed class InlineSpell
    {
        public InlineSpell()
        {
            this.Criteria = new InlineSpellCriterion[0];
            this.Arguments = new InlineSpellArgument[0];
        }

        public int FunctionType { get; set; }

        public int Header2 { get; set; }

        public int Version { get; set; }

        public InlineSpellCriterion[] Criteria { get; set; }

        public InlineSpellArgument[] Arguments { get; set; }
    }

    /// <summary>One spell criterion: three int32 values in wire order.</summary>
    public struct InlineSpellCriterion
    {
        public InlineSpellCriterion(int value1, int value2, int @operator)
        {
            this.Value1 = value1;
            this.Value2 = value2;
            this.Operator = @operator;
        }

        public int Value1 { get; }

        public int Value2 { get; }

        public int Operator { get; }
    }

    /// <summary>An inline spell argument: an int32, or a string when <see cref="Text"/> is set.</summary>
    public struct InlineSpellArgument
    {
        private InlineSpellArgument(int number, string text)
        {
            this.Number = number;
            this.Text = text;
        }

        public int Number { get; }

        public string Text { get; }

        public bool IsText
        {
            get { return this.Text != null; }
        }

        public static InlineSpellArgument Of(int number)
        {
            return new InlineSpellArgument(number, null);
        }

        public static InlineSpellArgument Of(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException("text");
            }

            return new InlineSpellArgument(0, text);
        }
    }
}
