using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using AORebirth.World.Pathfinding;

using DotRecast.Core.Numerics;
using DotRecast.Detour;

static class Program
{
    static int Main(string[] args)
    {
        // usage: <gameDataRoot> <playfieldId> sx sy sz ex ey ez
        string root = args[0];
        int pf = int.Parse(args[1]);
        float F(int i) => float.Parse(args[i], CultureInfo.InvariantCulture);
        var start = new Vector3(F(2), F(3), F(4));
        var end = new Vector3(F(5), F(6), F(7));

        // 1. Engine pathfinder (streaming + island pre-check).
        if (!NavMeshPathfinder.TryLoad(root, pf, out NavMeshPathfinder? pfr, out string? fail) || pfr == null)
        {
            Console.WriteLine("TryLoad failed: " + fail);
            return 1;
        }
        Console.WriteLine($"islandCheck={pfr.IslandCheckEnabled} islands={pfr.IslandCount}");
        var wps = new List<Vector3>();
        bool ok = pfr.TryFindPath(start, end, wps);
        Console.WriteLine($"engine TryFindPath ok={ok} outcome={pfr.LastOutcome} waypoints={wps.Count} iters={pfr.LastIterations}");
        Console.WriteLine($"snapStart={(pfr.TrySnap(start, out Vector3 ss) ? ss.ToString() : "none")} snapEnd={(pfr.TrySnap(end, out Vector3 se) ? se.ToString() : "none")}");

        if (args.Length > 8 && args[8] == "scan")
        {
            // Grid scan: which points around the start reach the destination's island.
            for (float z = 840f; z <= 858f; z += 2f)
            {
                var row = new System.Text.StringBuilder($"z={z,5:F0} ");
                for (float x = 829f; x <= 871f; x += 2f)
                {
                    var p = new Vector3(x, 37f, z);
                    string c = ".";
                    if (pfr.TrySnap(p, 2f, out Vector3 sp))
                    {
                        var w = new List<Vector3>();
                        c = pfr.TryFindPath(sp, end, w) ? "#" : (pfr.LastOutcome == PathSearchOutcome.Disconnected ? "o" : "?");
                        if (c == "#" && sp.Y < 36f) c = "+";
                    }
                    row.Append(c);
                }
                Console.WriteLine(row.ToString());
            }
            Console.WriteLine("x from 829 to 871 step 2 at probe y=37 (snap extent 2): # reaches dest, o other island, . no mesh");
            return 0;
        }

        // 2. Whole-mesh Detour search with no pre-check.
        string meshPath = System.IO.Path.Combine(root, "Playfields", pf.ToString(), "Navmesh.dat");
        DtNavMesh mesh = NavMeshFile.Read(meshPath, out _);
        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        var ext = new RcVec3f(4f, 8f, 4f);
        query.FindNearestPoly(start, ext, filter, out long sRef, out RcVec3f sPt, out _);
        query.FindNearestPoly(end, ext, filter, out long eRef, out RcVec3f ePt, out _);
        Console.WriteLine($"full startRef={sRef} at {sPt} endRef={eRef} at {ePt}");
        if (sRef == 0 || eRef == 0)
            return 0;
        long[] path = new long[2048];
        DtStatus st = query.FindPath(sRef, eRef, sPt, ePt, filter, path, out int pathCount, path.Length);
        Console.WriteLine($"full FindPath status={st} partial={st.IsPartial()} polys={pathCount} lastPolyIsEnd={(pathCount > 0 && path[pathCount - 1] == eRef)}");
        if (pathCount > 0)
        {
            var straight = new DtStraightPath[256];
            query.FindStraightPath(sPt, ePt, path, pathCount, straight, out int sc, straight.Length, 0);
            for (int i = 0; i < sc; i++)
                Console.WriteLine($"  wp {straight[i].pos.X:F1},{straight[i].pos.Y:F1},{straight[i].pos.Z:F1}");
        }
        return 0;
    }
}
