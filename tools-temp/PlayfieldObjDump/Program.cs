namespace PlayfieldObjDump
{
    using System;
    using System.Globalization;
    using System.IO;

    using AORebirth.Core.GameData;
    using AORebirth.World.Collision;
    using AORebirth.World.Pathfinding;

    internal static class Program
    {
        static int Main(string[] args)
        {
            int playfieldId = 800;
            if (args.Length > 0
                && (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out playfieldId)
                    || playfieldId <= 0))
            {
                Console.Error.WriteLine("Expected a positive playfield id.");
                return 2;
            }

            string repoRoot = FindRepoRoot();
            string gameDataRoot = Path.Combine(repoRoot, "AORebirth", "GameData");
            string outPath = Path.Combine(
                repoRoot,
                "artifacts",
                "playfield-" + playfieldId.ToString(CultureInfo.InvariantCulture) + ".obj");

            PlayfieldCollisionSet set = PlayfieldCollisionResolver.Resolve(gameDataRoot, playfieldId);
            CollisionTriangleMesh? terrain = set.Terrain != null
                ? TerrainHeightfieldMesher.TryBuild(set.Terrain)
                : null;
            int groundMeshes = 0;
            int groundTris = 0;
            for (int i = 0; i < set.SurfaceMeshes.Count; i++)
            {
                CollisionTriangleMesh mesh = set.SurfaceMeshes[i];
                if (mesh.Source == null || !mesh.Source.StartsWith("ground", StringComparison.Ordinal))
                    continue;

                groundMeshes++;
                groundTris += mesh.Triangles.Length;
            }

            CollisionObjDumper.DumpObj(set, outPath);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pf={playfieldId} terrain={(terrain != null)} terrainTris={terrain?.Triangles.Length ?? 0} surfaces={set.SurfaceMeshes.Count} groundMeshes={groundMeshes} groundTris={groundTris} obj={outPath}"));
            return 0;
        }

        static string FindRepoRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "AGENTS.md")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException(
                "Could not locate repository root from " + AppContext.BaseDirectory);
        }
    }
}
