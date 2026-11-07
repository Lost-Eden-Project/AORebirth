using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

using AORebirth.World.Pathfinding;

using DotRecast.Detour;
using DotRecast.Detour.Io;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: NavMeshMemProbe <Navmesh.dat> [api|full|stream <gameDataRoot> <playfieldId>]");
            return 1;
        }

        if (args.Length > 1 && args[1] == "api")
        {
            foreach (Type t in new[] { typeof(DtNavMesh), typeof(DtMeshTile), typeof(DtMeshData), typeof(DtMeshHeader), typeof(DtPoly), typeof(DtBVNode), typeof(DtLink), typeof(DtNavMeshParams), typeof(DtMeshSetReader), typeof(DtMeshDataReader), typeof(NavMeshSetHeader), typeof(DotRecast.Core.RcByteBuffer) })
                DumpApi(t);
            return 0;
        }

        if (args.Length > 1 && args[1] == "stream")
            return Stream(args[2], int.Parse(args[3]));

        string path = args[0];
        long fileBytes = new FileInfo(path).Length;
        long before = Settle();
        DtNavMesh mesh = NavMeshFile.Read(path, out int pf);
        long after = Settle();
        Console.WriteLine($"playfield={pf} file={fileBytes / 1048576.0:F1}MB heapDelta={(after - before) / 1048576.0:F1}MB ratio={(double)(after - before) / fileBytes:F2}x");

        int maxTiles = mesh.GetMaxTiles();
        int tiles = 0, polys = 0, verts = 0, dverts = 0, dtris = 0, bv = 0, links = 0, offmesh = 0, maxPolyTile = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int i = 0; i < maxTiles; i++)
        {
            DtMeshTile t = mesh.GetTile(i);
            if (t?.data?.header == null)
                continue;
            tiles++;
            DtMeshHeader h = t.data.header;
            polys += h.polyCount;
            verts += h.vertCount;
            dverts += h.detailVertCount;
            dtris += h.detailTriCount;
            bv += h.bvNodeCount;
            offmesh += h.offMeshConCount;
            maxPolyTile = Math.Max(maxPolyTile, h.polyCount);
            minX = Math.Min(minX, h.x); maxX = Math.Max(maxX, h.x);
            minY = Math.Min(minY, h.y); maxY = Math.Max(maxY, h.y);
            links += t.links == null ? 0 : ((System.Collections.ICollection)t.links).Count;
        }

        DtNavMeshParams p = mesh.GetParams();
        Console.WriteLine($"maxTiles={maxTiles} tiles={tiles} grid=({minX}..{maxX})x({minY}..{maxY}) tileWidth={p.tileWidth} maxPolysParam={p.maxPolys}");
        Console.WriteLine($"polys={polys} maxPolysInTile={maxPolyTile} verts={verts} detailVerts={dverts} detailTris={dtris} bvNodes={bv} links={links} offMesh={offmesh}");
        Console.WriteLine($"bytesPerPoly={(double)(after - before) / Math.Max(polys, 1):F0} filePerPoly={(double)fileBytes / Math.Max(polys, 1):F0}");
        GC.KeepAlive(mesh);
        return 0;
    }

    /// <summary>Streams the mesh the way the engine does and compares a sample of paths against the full load.</summary>
    static int Stream(string gameDataRoot, int playfieldId)
    {
        long before = Settle();
        var sw = Stopwatch.StartNew();
        if (!NavMeshPathfinder.TryLoad(gameDataRoot, playfieldId, out NavMeshPathfinder? finder, out string? failure) || finder == null)
        {
            Console.WriteLine("LOAD FAILED: " + failure);
            return 1;
        }

        sw.Stop();
        long after = Settle();
        NavMeshResidency r = finder.Residency;
        Console.WriteLine($"stream load: {sw.ElapsedMilliseconds}ms heap={(after - before) / 1048576.0:F1}MB islands={finder.IslandCount} indexed={r.IndexedTiles} loaded={r.LoadedTiles} polys={r.TotalPolys}");

        // Full mesh for ground truth.
        string meshPath = Path.Combine(gameDataRoot, "Playfields", playfieldId.ToString(), "Navmesh.dat");
        DtNavMesh full = NavMeshFile.Read(meshPath, out _);
        var fullQuery = new DtNavMeshQuery(full);
        var filter = new DtQueryDefaultFilter();
        full.ComputeBounds(out var bmin, out var bmax);
        var rng = new Random(1234);

        // Sample points on the mesh.
        var points = new List<Vector3>();
        int tries = 0;
        while (points.Count < 60 && tries++ < 20000)
        {
            var c = new DotRecast.Core.Numerics.RcVec3f(
                bmin.X + (float)rng.NextDouble() * (bmax.X - bmin.X),
                (bmin.Y + bmax.Y) * 0.5f,
                bmin.Z + (float)rng.NextDouble() * (bmax.Z - bmin.Z));
            if (fullQuery.FindNearestPoly(c, new DotRecast.Core.Numerics.RcVec3f(2f, 500f, 2f), filter, out long pr, out var np, out _).Succeeded() && pr != 0)
                points.Add(new Vector3(np.X, np.Y, np.Z));
        }

        int agree = 0, disagree = 0, found = 0, longRoutes = 0, longFound = 0;
        var wp = new List<Vector3>();
        var fullPath = new long[256];
        sw.Restart();
        for (int i = 0; i + 1 < points.Count; i += 1)
        {
            Vector3 a = points[i];
            Vector3 b = points[(i * 7 + 3) % points.Count];
            // Odd pairs: short routes (<=150 m). Even pairs: long routes up to 900 m to exercise the corridor.
            float cap = (i % 2 == 1) ? 150f : 900f;
            if (Vector3.Distance(a, b) > cap)
                b = a + Vector3.Normalize(b - a) * cap;
            if (!finder.TrySnap(b, out b))
                continue;
            bool isLong = Vector3.Distance(a, b) > 300f;

            bool streamed = finder.TryFindPath(a, b, wp);
            PathSearchOutcome outcome = finder.LastOutcome;

            fullQuery.FindNearestPoly(a, new DotRecast.Core.Numerics.RcVec3f(8f, 9f, 8f), filter, out long sr, out var sp, out _);
            fullQuery.FindNearestPoly(b, new DotRecast.Core.Numerics.RcVec3f(8f, 9f, 8f), filter, out long er, out var ep, out _);
            bool fullFound = false;
            if (sr != 0 && er != 0)
            {
                fullQuery.InitSlicedFindPath(sr, er, sp, ep, filter, 0);
                DtStatus st = fullQuery.UpdateSlicedFindPath(4096, out _);
                if (st.Succeeded() && !st.IsPartial() && !st.InProgress())
                {
                    fullQuery.FinalizeSlicedFindPath(fullPath, out int n, fullPath.Length);
                    fullFound = n > 0 && fullPath[n - 1] == er;
                }
            }

            if (streamed) found++;
            if (isLong) { longRoutes++; if (fullFound) longFound++; }
            if (streamed == fullFound) agree++;
            else if (streamed) { agree++; Console.WriteLine($"  streamed found where full mesh hit its cap: dist={Vector3.Distance(a, b):F0}"); }
            else { disagree++; Console.WriteLine($"  mismatch streamed={streamed}({outcome}) full={fullFound} dist={Vector3.Distance(a, b):F0}"); }
        }

        sw.Stop();
        r = finder.Residency;
        long afterQueries = Settle();
        Console.WriteLine($"paths: {agree + disagree} compared, found={found} agree={agree} disagree={disagree} longRoutes(>300m)={longRoutes} fullFoundLong={longFound} in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"residency: loaded={r.LoadedTiles}/{r.WindowCapacity} loads={r.TileLoads} unloads={r.TileUnloads} loadMs={r.LoadMilliseconds:F1} heapNow(with full mesh)={(afterQueries - before) / 1048576.0:F1}MB");
        GC.KeepAlive(full);
        finder.Dispose();
        return disagree == 0 ? 0 : 2;
    }

    static long Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    static void DumpApi(Type type)
    {
        Console.WriteLine("== " + type.FullName + (type.IsValueType ? " (struct)" : " (class)"));
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(f => f.Name))
            Console.WriteLine("  F " + f.FieldType.Name + " " + f.Name + (f.IsLiteral ? " = " + f.GetRawConstantValue() : ""));
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(f => f.Name))
        {
            string ps = string.Join(", ", m.GetParameters().Select(x => (x.IsOut ? "out " : x.ParameterType.IsByRef ? "ref " : "") + x.ParameterType.Name.TrimEnd('&') + " " + x.Name));
            Console.WriteLine("  M " + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
        }
    }
}
