namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;

    using ZoneEngine_New.Core.Entities;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    public static class WallZoneLandingResolver
    {
        /// <summary>
        /// A destination line shorter than this is a landing point, and its own height is the landing height: the
        /// Shadowlands stack zones over each other (4872 Adonis at Y 113 drops into 4873 Adonis Abyss's water, whose
        /// line sits at Y 102), so the height the character left at is wrong there. Longer lines are zone edges whose
        /// two end heights say nothing about the terrain between them (655/670: a blend lands 16-31 units underground),
        /// so a crossing there keeps the character's own height, which neighbouring edges share.
        /// </summary>
        public const float PointLineMaxLength = 20f;

        public static bool TryResolve(
            DestinationsCatalog destinations,
            ZoneTriggerVolume wall,
            float factor,
            Vector3 currentPosition,
            out int destPlayfieldId,
            out Vector3 landing)
        {
            destPlayfieldId = wall.DestPlayfieldId;
            landing = currentPosition;
            if (destPlayfieldId <= 0)
                return false;

            if (!destinations.TryGetDestination(destPlayfieldId, wall.DestIndex, out PlayfieldDestination? dest)
                || dest == null)
                return false;

            // The factor is how far along this side's segment (A to B) the crossing was. The other side's line is
            // usually that playfield's own wall segment, wound the other way (655 (200,204)->(2763,193) lands on 670
            // (2772,4520)->(231,4528)): when the two run opposite, the same spot is the same distance from the far end.
            float sourceX = wall.SegBx - wall.SegAx;
            float sourceZ = wall.SegBz - wall.SegAz;
            if ((sourceX * (dest.EndX - dest.StartX)) + (sourceZ * (dest.EndZ - dest.StartZ)) < 0f)
                factor = 1f - factor;

            float newX = ((dest.EndX - dest.StartX) * factor) + dest.StartX;
            float newZ = ((dest.EndZ - dest.StartZ) * factor) + dest.StartZ;
            float dist = MathF.Sqrt(
                ((dest.EndX - dest.StartX) * (dest.EndX - dest.StartX))
                + ((dest.EndZ - dest.StartZ) * (dest.EndZ - dest.StartZ)));
            if (dist > 1e-4f)
            {
                float headX = (dest.EndX - dest.StartX) / dist;
                float headZ = (dest.EndZ - dest.StartZ) / dist;
                newX -= headZ * 8f;
                newZ += headX * 8f;
            }

            float newY = dist < PointLineMaxLength
                ? ((dest.EndY - dest.StartY) * factor) + dest.StartY
                : (float)currentPosition.y;
            landing = new Vector3(newX, newY, newZ);
            return true;
        }
    }
}
