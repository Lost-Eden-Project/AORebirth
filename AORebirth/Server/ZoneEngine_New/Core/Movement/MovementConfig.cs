namespace ZoneEngine_New.Core.Movement
{
    /// <summary>
    /// Server-side movement values. The vehicle's own constants (gravity, speed curves, step height,
    /// probe sizes) live in <c>N3Lite</c>; these cover what only the server does.
    /// </summary>
    public static class MovementConfig
    {
        /// <summary><c>N3CharVehicle.BuildSim</c>: the player / NPC factory body mass.</summary>
        public const float Mass = 50f;

        /// <summary><c>N3CharVehicle.BuildSim</c>: the body radius, used as the near-probe offset.</summary>
        public const float BodyRadius = 0.5f;

        /// <summary>Unported Lost-Eden glue: a player-body path waypoint counts as reached inside this XZ radius.</summary>
        public const float WaypointArrivalRadius = 0.5f;

        /// <summary>Two path starts this close (XZ) count as the same place.</summary>
        public const float PathArrivalRadius = 0.25f;

        public const float SpeedStopEpsilon = 0.05f;

        /// <summary>Server floor snap: probes start this far above the feet so a small rise still reads as floor.</summary>
        public const float GroundProbeLift = 0.5f;

        /// <summary>Server floor snap: floor this far below the feet still counts as support.</summary>
        public const float GroundSnapTolerance = 2.5f;

        /// <summary>Torso height the void probe starts from, above any floor the feet could be under.</summary>
        public const float VoidProbeLift = 1.8f;

        /// <summary>Probe used only to tell "standing above a drop" apart from "no geometry here at all".</summary>
        public const float VoidProbeDepth = 4096f;

        public const float LineOfSightEyeHeight = 1.6f;
    }
}
