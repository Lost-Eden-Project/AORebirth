namespace AORebirth.World.Pathfinding
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    using AORebirth.Core.GameData;

    using DotRecast.Recast;

    /// <summary>Navmesh polygon area ids. The query filter prices each one (<see cref="NavMeshBuildSettings.WaterAreaCost"/>).</summary>
    public static class NavMeshAreas
    {
        /// <summary>Dry ground: Recast's default walkable area.</summary>
        public const int Ground = RcRecast.RC_WALKABLE_AREA;

        /// <summary>Ground under a water surface (lake and river beds, shallows).</summary>
        public const int Water = 1;

        /// <summary>Per-area costs for a Detour query filter: ground 1, water <paramref name="waterCost"/>.</summary>
        public static float[] Costs(float waterCost)
        {
            var costs = new float[64];
            Array.Fill(costs, 1f);
            costs[Water] = waterCost;
            return costs;
        }

        /// <summary>
        /// The playfield-wide water meshes in the playfield's Water.dat (RDBPlayfield.GlobalWaterData), in world space.
        /// Room water is template-local and is left out. Empty when the playfield has no Water.dat.
        /// </summary>
        public static IReadOnlyList<PlayfieldWaterEntry> LoadPlayfieldWater(string gameDataRoot, int playfieldId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            string path = Path.Combine(gameDataRoot, GameDataPaths.PlayfieldWaterRelativePath(playfieldId));
            if (!File.Exists(path))
                return Array.Empty<PlayfieldWaterEntry>();

            List<PlayfieldWaterEntry> entries = PlayfieldWaterDat.Parse(File.ReadAllBytes(path), out _);
            entries.RemoveAll(entry => entry.RoomIndex >= 0);
            return entries;
        }
    }
}
