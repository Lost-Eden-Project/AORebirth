namespace ZoneEngine_New.Core.WorldSimulation
{
    using System;

    using ZoneEngine_New.Core.Entities;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    public static class WallZoneLandingResolver
    {
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

            landing = new Vector3(newX, (float)currentPosition.y, newZ);
            return true;
        }
    }
}
