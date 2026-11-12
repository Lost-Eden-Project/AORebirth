namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// A <see cref="Playfield"/> built without its constructor: identity, services, the rebase queue, its lifecycle
    /// lock and a uniform Normal-gas fight-mode table, so combat rules and stat flushes run on it.
    /// </summary>
    internal static class BarePlayfield
    {
        public static Playfield Create(int playfieldId, IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            Set(playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = playfieldId });
            Set(playfield, "_serviceProvider", services);
            Set(playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Set(playfield, "_lifecycleSync", new System.Threading.Lock());
            Set(playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
            return playfield;
        }

        static void Set(Playfield playfield, string field, object value)
            => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);
    }
}
