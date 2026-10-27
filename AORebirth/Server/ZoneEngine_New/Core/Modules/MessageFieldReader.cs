namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq.Expressions;
    using System.Reflection;

    using AORebirth.Modules.Network;
    using AORebirth.Modules.World;

    using SmokeLounge.AOtomation.Messaging.Messages;

    using ZoneEngine_New.Core.Network;

    using Identity = SmokeLounge.AOtomation.Messaging.GameData.Identity;

    /// <summary>
    /// <see cref="IMessageFields"/> over one parsed message. One reader per connection direction is reused for every
    /// message, so a read allocates nothing: a path the declared types resolve compiles once per (message type, path,
    /// value kind) into a typed accessor. A path through a base type whose member only a subtype has
    /// (<c>FollowTarget</c>'s <c>Info</c>) falls back to reflection on the runtime types.
    /// </summary>
    internal sealed class MessageFieldReader : IMessageFields
    {
        enum Kind
        {
            Int,
            Float,
            Identity,
            String
        }

        static readonly ConcurrentDictionary<(Type Root, string Path, Kind Kind), Delegate?> Compiled = new();
        static readonly ConcurrentDictionary<(Type Type, string Member), MemberInfo?> Members = new();

        object? _body;
        byte[]? _packet;
        ZoneMessageCodec? _codec;

        /// <summary>Reads <paramref name="body"/> until the next reset.</summary>
        public void Reset(object? body)
        {
            _body = body;
            _packet = null;
            _codec = null;
        }

        /// <summary>Reads <paramref name="packet"/>, parsed with <paramref name="codec"/> only when a field is first read.</summary>
        public void ResetDeferred(byte[] packet, ZoneMessageCodec codec)
        {
            _body = null;
            _packet = packet;
            _codec = codec;
        }

        public bool TryGetInt(string name, out int value) => TryRead(name, Kind.Int, out value);

        public bool TryGetFloat(string name, out float value) => TryRead(name, Kind.Float, out value);

        public bool TryGetIdentity(string name, out EntityRef value) => TryRead(name, Kind.Identity, out value);

        public bool TryGetString(string name, out string value)
        {
            bool found = TryRead(name, Kind.String, out string? text);
            value = found && text != null ? text : string.Empty;
            return found && text != null;
        }

        object? Body()
        {
            if (_body == null && _packet != null && _codec != null)
            {
                byte[] packet = _packet;
                _packet = null;
                try
                {
                    _body = _codec.Deserialize(packet)?.Body;
                }
                catch (Exception)
                {
                    // A message the codec cannot read back has no fields.
                    _body = null;
                }
            }

            return _body;
        }

        bool TryRead<T>(string name, Kind kind, out T value)
        {
            value = default!;
            if (string.IsNullOrEmpty(name) || Body() is not object body)
                return false;

            Type root = body.GetType();
            Delegate? accessor = Compiled.GetOrAdd((root, name, kind), key => Compile(key.Root, key.Path, key.Kind));
            try
            {
                if (accessor is Func<object, T> typed)
                {
                    value = typed(body);
                    return true;
                }

                return TryReadDynamic(body, name, kind, out value);
            }
            catch (Exception exception) when (exception is NullReferenceException or IndexOutOfRangeException
                or ArgumentOutOfRangeException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        /// <summary>A typed accessor when every segment resolves on the declared types, else null.</summary>
        static Delegate? Compile(Type root, string path, Kind kind)
        {
            ParameterExpression parameter = Expression.Parameter(typeof(object), "body");
            Expression current = Expression.Convert(parameter, root);
            foreach (Segment segment in Parse(path))
            {
                if (segment.Member.Length > 0)
                {
                    if (FindMember(current.Type, segment.Member) is not MemberInfo member)
                        return null;
                    current = Expression.MakeMemberAccess(current, member);
                }

                if (segment.Index is int index)
                {
                    if (current.Type.IsArray)
                        current = Expression.ArrayIndex(current, Expression.Constant(index));
                    else if (current.Type.GetProperty("Item", [typeof(int)]) is PropertyInfo indexer)
                        current = Expression.MakeIndex(current, indexer, [Expression.Constant(index)]);
                    else
                        return null;
                }
            }

            Expression? leaf = Convert(current, kind);
            if (leaf == null)
                return null;
            Type delegateType = typeof(Func<,>).MakeGenericType(typeof(object), leaf.Type);
            return Expression.Lambda(delegateType, leaf, parameter).Compile();
        }

        static Expression? Convert(Expression value, Kind kind)
        {
            Type type = value.Type;
            Type plain = type.IsEnum ? Enum.GetUnderlyingType(type) : type;
            Expression source = type.IsEnum ? Expression.Convert(value, plain) : value;
            switch (kind)
            {
                case Kind.Int:
                    if (plain == typeof(bool))
                        return Expression.Condition(source, Expression.Constant(1), Expression.Constant(0));
                    return plain == typeof(int) ? source
                        : IsInteger(plain) ? Expression.Convert(source, typeof(int)) : null;
                case Kind.Float:
                    return plain == typeof(float) ? source
                        : plain == typeof(double) || IsInteger(plain) ? Expression.Convert(source, typeof(float)) : null;
                case Kind.Identity:
                    if (type != typeof(Identity))
                        return null;
                    return Expression.New(
                        typeof(EntityRef).GetConstructor([typeof(int), typeof(int)])!,
                        Expression.Convert(Expression.Convert(Expression.Property(value, nameof(Identity.Type)),
                            Enum.GetUnderlyingType(typeof(SmokeLounge.AOtomation.Messaging.GameData.IdentityType))), typeof(int)),
                        Expression.Property(value, nameof(Identity.Instance)));
                case Kind.String:
                    return type == typeof(string) ? value : null;
                default:
                    return null;
            }
        }

        static bool IsInteger(Type type)
            => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

        /// <summary>The slow path: each segment on the runtime type of the value before it.</summary>
        static bool TryReadDynamic<T>(object body, string path, Kind kind, out T value)
        {
            value = default!;
            object? current = body;
            foreach (Segment segment in Parse(path))
            {
                if (current == null)
                    return false;
                if (segment.Member.Length > 0)
                {
                    switch (FindMember(current.GetType(), segment.Member))
                    {
                        case PropertyInfo property:
                            current = property.GetValue(current);
                            break;
                        case FieldInfo field:
                            current = field.GetValue(current);
                            break;
                        default:
                            return false;
                    }
                }

                if (segment.Index is int index)
                {
                    if (current is not IList list || index < 0 || index >= list.Count)
                        return false;
                    current = list[index];
                }
            }

            object? converted = current switch
            {
                null => null,
                _ when kind == Kind.Int && (current is bool || current is Enum || IsInteger(current.GetType()))
                    => current is bool flag ? (flag ? 1 : 0) : System.Convert.ToInt32(current, System.Globalization.CultureInfo.InvariantCulture),
                _ when kind == Kind.Float && (current is float || current is double || IsInteger(current.GetType()))
                    => System.Convert.ToSingle(current, System.Globalization.CultureInfo.InvariantCulture),
                Identity identity when kind == Kind.Identity => new EntityRef((int)identity.Type, identity.Instance),
                string text when kind == Kind.String => text,
                _ => null
            };
            if (converted is not T typed)
                return false;
            value = typed;
            return true;
        }

        static MemberInfo? FindMember(Type type, string name)
            => Members.GetOrAdd((type, name), key =>
                (MemberInfo?)key.Type.GetProperty(key.Member, BindingFlags.Public | BindingFlags.Instance)
                ?? key.Type.GetField(key.Member, BindingFlags.Public | BindingFlags.Instance));

        readonly record struct Segment(string Member, int? Index);

        static List<Segment> Parse(string path)
        {
            var segments = new List<Segment>();
            foreach (string part in path.Split('.'))
            {
                int open = part.IndexOf('[');
                if (open < 0)
                {
                    segments.Add(new Segment(part, null));
                    continue;
                }

                int close = part.IndexOf(']', open);
                int index = close > open && int.TryParse(part.AsSpan(open + 1, close - open - 1), out int parsed) ? parsed : -1;
                segments.Add(new Segment(part[..open], index));
            }

            return segments;
        }
    }
}
