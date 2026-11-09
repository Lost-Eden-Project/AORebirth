namespace SmokeLounge.AOtomation.Messaging.Serialization.Serializers.Custom
{
    using System;
    using System.Collections.Generic;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Text;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    /// <summary>
    /// Writes <see cref="ApplySpellsMessage"/> in the layout the client's ApplySpellsIIR_t reads (see that class).
    /// Reading back decodes only the function types whose argument layout is listed in <see cref="KnownFormats"/>;
    /// the first other spell ends the list, because its length cannot be known without the client's format table.
    /// </summary>
    public class ApplySpellsMessageSerializer : ISerializer
    {
        private const int X3F1Factor = 0x03F1;

        /// <summary>The client rejects a count of 1000 or more (Gamecode.dll 0x100a6fcb).</summary>
        private const int MaxSpells = 999;

        private const int MaxCriteria = 1000;

        private const int MaxStringBytes = 0x10000;

        /// <summary>Function type and version to argument kinds in wire order (true = string).</summary>
        private static readonly Dictionary<long, bool[]> KnownFormats = new Dictionary<long, bool[]>
        {
            // 53247 center-screen notification: stats 3, 4, 32, 35, string 0 (text), stat 11 (range).
            { FormatKey(53247, 4), new[] { false, false, false, false, true, false } }
        };

        private readonly Type type = typeof(ApplySpellsMessage);

        public Type Type
        {
            get { return this.type; }
        }

        public object Deserialize(
            StreamReader streamReader,
            SerializationContext serializationContext,
            PropertyMetaData propertyMetaData = null)
        {
            var message = new ApplySpellsMessage();
            message.N3MessageType = (N3MessageType)streamReader.ReadInt32();
            message.Identity = streamReader.ReadIdentity();
            message.Unknown = streamReader.ReadByte();

            int encoded = streamReader.ReadInt32();
            int count = encoded > 0 && encoded % X3F1Factor == 0 ? (encoded / X3F1Factor) - 1 : 0;
            if (count > MaxSpells)
            {
                count = 0;
            }

            var spells = new List<InlineSpell>(count);
            bool complete = true;
            for (int i = 0; i < count; i++)
            {
                InlineSpell spell = ReadSpell(streamReader);
                if (spell == null)
                {
                    complete = false;
                    break;
                }

                spells.Add(spell);
            }

            message.Spells = spells.ToArray();
            if (complete && streamReader.Remaining > 0)
            {
                message.Execute = streamReader.ReadByte();
            }

            return message;
        }

        public Expression DeserializerExpression(
            ParameterExpression streamReaderExpression,
            ParameterExpression serializationContextExpression,
            Expression assignmentTargetExpression,
            PropertyMetaData propertyMetaData)
        {
            MethodInfo deserializerMethodInfo =
                ReflectionHelper
                    .GetMethodInfo
                    <ApplySpellsMessageSerializer, Func<StreamReader, SerializationContext, PropertyMetaData, object>>(
                        o => o.Deserialize);
            NewExpression serializerExp = Expression.New(this.GetType());
            MethodCallExpression callExp = Expression.Call(
                serializerExp,
                deserializerMethodInfo,
                new Expression[]
                    {
                        streamReaderExpression,
                        serializationContextExpression,
                        Expression.Constant(propertyMetaData, typeof(PropertyMetaData))
                    });

            return Expression.Assign(
                assignmentTargetExpression,
                Expression.TypeAs(callExp, assignmentTargetExpression.Type));
        }

        public void Serialize(
            StreamWriter streamWriter,
            SerializationContext serializationContext,
            object value,
            PropertyMetaData propertyMetaData = null)
        {
            var message = (ApplySpellsMessage)value;
            InlineSpell[] spells = message.Spells ?? new InlineSpell[0];
            if (spells.Length > MaxSpells)
            {
                throw new InvalidOperationException("ApplySpells carries at most " + MaxSpells + " spells.");
            }

            streamWriter.WriteInt32((int)message.N3MessageType);
            streamWriter.WriteIdentity(message.Identity);
            streamWriter.WriteByte(message.Unknown);
            streamWriter.WriteInt32((spells.Length + 1) * X3F1Factor);
            foreach (InlineSpell spell in spells)
            {
                if (spell == null)
                {
                    throw new InvalidOperationException("ApplySpells has a null spell.");
                }

                WriteSpell(streamWriter, spell);
            }

            streamWriter.WriteByte(message.Execute);
        }

        public Expression SerializerExpression(
            ParameterExpression streamWriterExpression,
            ParameterExpression serializationContextExpression,
            Expression valueExpression,
            PropertyMetaData propertyMetaData)
        {
            MethodInfo serializeMethodInfo =
                ReflectionHelper
                    .GetMethodInfo
                    <ApplySpellsMessageSerializer,
                        Action<StreamWriter, SerializationContext, object, PropertyMetaData>>(o => o.Serialize);
            NewExpression serializerExp = Expression.New(this.GetType());
            return Expression.Call(
                serializerExp,
                serializeMethodInfo,
                streamWriterExpression,
                serializationContextExpression,
                Expression.Convert(valueExpression, typeof(object)),
                Expression.Constant(propertyMetaData, typeof(PropertyMetaData)));
        }

        private static long FormatKey(int functionType, int version)
        {
            return ((long)functionType << 32) | (uint)version;
        }

        private static void WriteSpell(StreamWriter writer, InlineSpell spell)
        {
            InlineSpellCriterion[] criteria = spell.Criteria ?? new InlineSpellCriterion[0];
            InlineSpellArgument[] arguments = spell.Arguments ?? new InlineSpellArgument[0];
            if (criteria.Length > MaxCriteria)
            {
                throw new InvalidOperationException("A spell carries at most " + MaxCriteria + " criteria.");
            }

            writer.WriteInt32(spell.FunctionType);
            writer.WriteInt32(spell.Header2);
            writer.WriteInt32(spell.Version);
            writer.WriteInt32(criteria.Length);
            foreach (InlineSpellCriterion criterion in criteria)
            {
                writer.WriteInt32(criterion.Value1);
                writer.WriteInt32(criterion.Value2);
                writer.WriteInt32(criterion.Operator);
            }

            foreach (InlineSpellArgument argument in arguments)
            {
                if (!argument.IsText)
                {
                    writer.WriteInt32(argument.Number);
                    continue;
                }

                byte[] bytes = Encoding.Latin1.GetBytes(argument.Text);
                if (bytes.Length > MaxStringBytes)
                {
                    throw new InvalidOperationException("A spell string is at most " + MaxStringBytes + " bytes.");
                }

                writer.WriteInt32(bytes.Length);
                writer.WriteBytes(bytes);
            }
        }

        private static InlineSpell ReadSpell(StreamReader reader)
        {
            if (reader.Remaining < 16)
            {
                return null;
            }

            var spell = new InlineSpell
            {
                FunctionType = reader.ReadInt32(),
                Header2 = reader.ReadInt32(),
                Version = reader.ReadInt32()
            };
            int criteriaCount = reader.ReadInt32();
            if (criteriaCount < 0 || criteriaCount > MaxCriteria || reader.Remaining < (long)criteriaCount * 12)
            {
                return null;
            }

            var criteria = new InlineSpellCriterion[criteriaCount];
            for (int i = 0; i < criteriaCount; i++)
            {
                int value1 = reader.ReadInt32();
                int value2 = reader.ReadInt32();
                criteria[i] = new InlineSpellCriterion(value1, value2, reader.ReadInt32());
            }

            spell.Criteria = criteria;
            bool[] kinds;
            if (!KnownFormats.TryGetValue(FormatKey(spell.FunctionType, spell.Version), out kinds))
            {
                return null;
            }

            var arguments = new InlineSpellArgument[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
            {
                if (reader.Remaining < 4)
                {
                    return null;
                }

                if (!kinds[i])
                {
                    arguments[i] = InlineSpellArgument.Of(reader.ReadInt32());
                    continue;
                }

                int length = reader.ReadInt32();
                if (length < 0 || length > MaxStringBytes || reader.Remaining < length)
                {
                    return null;
                }

                arguments[i] = InlineSpellArgument.Of(Encoding.Latin1.GetString(reader.ReadBytes(length)));
            }

            spell.Arguments = arguments;
            return spell;
        }
    }
}
