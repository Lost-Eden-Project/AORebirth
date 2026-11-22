namespace AORebirth.World.Pathfinding
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// Passages the baked navmesh cannot know are shut: a locked, closed door leaves its doorway on the mesh, but no
    /// one walks through it. <see cref="NavMeshPathfinder"/> refuses a route that crosses one.
    /// </summary>
    public interface INavPassageGate
    {
        /// <summary>True while at least one passage is shut; routes need no further test otherwise.</summary>
        bool AnyShut { get; }

        /// <summary>True when a shut passage lies within <paramref name="radius"/> (XZ) of <paramref name="at"/>.</summary>
        bool AnyShutWithin(Vector3 at, float radius);

        /// <summary>
        /// True when walking from <paramref name="start"/> through <paramref name="waypoints"/> (feet on the floor)
        /// goes through a shut passage.
        /// </summary>
        bool BlocksRoute(Vector3 start, IReadOnlyList<Vector3> waypoints);
    }
}
