namespace ZoneEngine_New.Core.Ai
{
    using System;
    using ZoneEngine_New.Core.Entities;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Best-effort separation for NPCs that share a destination or a target. Nothing is enforced and nobody is
    /// queried: each NPC derives a stable offset from its own identity, so a group sent to one spot fans out over a
    /// small disc around it and a pack chasing one target fans across an arc around it instead of stacking on one
    /// point. Costs a few multiplications per call; two NPCs may still overlap, which is accepted.
    /// </summary>
    public static class NpcSpread
    {
        const double Golden = 0.6180339887498949;
        const double Second = 0.7548776662466927;

        /// <summary>Width of the arc a pack approaching one target is spread across, centred on its approach bearing.</summary>
        public const double ArcDegrees = 120.0;

        /// <summary>Largest offset a shared destination is spread by (a big-bodied NPC gets up to this).</summary>
        public const double MaxSpreadMeters = 4.0;

        public const double MinSpreadMeters = 1.0;

        /// <summary>The longest weapon reach the approach point stands off at; longer-ranged NPCs stop once in range anyway.</summary>
        public const double ApproachReachCap = 4.0;

        /// <summary>Fraction of the shortest weapon range the approach point stands inside, so every weapon can swing there.</summary>
        public const double ApproachReachFactor = 0.75;

        /// <summary>
        /// <paramref name="point"/> shifted by this NPC's stable offset: a direction and a distance (even over the disc)
        /// from its identity, the disc sized from its body up to <see cref="MaxSpreadMeters"/>.
        /// </summary>
        public static Vector3 Destination(NpcCharacter npc, Vector3 point)
        {
            ArgumentNullException.ThrowIfNull(npc);
            ArgumentNullException.ThrowIfNull(point);
            double radius = Math.Clamp(npc.GetCollisionRadius() * 1.5, MinSpreadMeters, MaxSpreadMeters);
            (double a, double b) = Fractions(npc.Identity.Instance);
            double angle = a * 2.0 * Math.PI;
            double distance = radius * Math.Sqrt(b);
            return new Vector3(point.x + (Math.Sin(angle) * distance), point.y, point.z + (Math.Cos(angle) * distance));
        }

        /// <summary>
        /// Where this NPC walks to fight <paramref name="target"/>: <paramref name="reach"/> meters from it, on the
        /// <paramref name="baseAngle"/> bearing turned by the NPC's stable share of <see cref="ArcDegrees"/>.
        /// </summary>
        public static Vector3 ApproachPoint(NpcCharacter npc, Character target, double baseAngle, double reach)
        {
            ArgumentNullException.ThrowIfNull(npc);
            ArgumentNullException.ThrowIfNull(target);
            (double a, _) = Fractions(npc.Identity.Instance);
            double angle = baseAngle + ((a - 0.5) * ArcDegrees * Math.PI / 180.0);
            Vector3 at = target.Position;
            return new Vector3(at.x + (Math.Sin(angle) * reach), at.y, at.z + (Math.Cos(angle) * reach));
        }

        /// <summary>A stable fraction in [0, 1) for this NPC, for per-NPC variety in timings.</summary>
        public static double Unit(NpcCharacter npc)
        {
            ArgumentNullException.ThrowIfNull(npc);
            return Fractions(npc.Identity.Instance).B;
        }

        /// <summary>Bearing (radians, Sin = x, Cos = z) from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static double Bearing(Vector3 from, Vector3 to) => Math.Atan2(to.x - from.x, to.z - from.z);

        /// <summary>Two stable, well-spread fractions in [0, 1) for an identity (multiplicative hashing).</summary>
        static (double A, double B) Fractions(int instance)
        {
            double id = Math.Abs((long)instance);
            return (Frac(id * Golden), Frac(id * Second));
        }

        static double Frac(double value) => value - Math.Floor(value);
    }
}
