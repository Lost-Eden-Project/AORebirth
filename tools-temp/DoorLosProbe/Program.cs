using System;
using System.Numerics;
using AORebirth.World.Pathfinding;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.WorldSimulation;

class Log : IZoneLogger
{
    public void Debug(string m) { }
    public void Info(string m) { }
    public void Warn(string m) { }
    public void Error(string m) => Console.WriteLine("ERR " + m);
    public void Error(Exception e, string m) => Console.WriteLine("ERR " + m + " " + e.Message);
    public IZoneLogger CreateForPlayfield(int id) => this;
}

class P
{
    static void Main()
    {
        Environment.SetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH", @"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData");
        var log = new Log();
        var data = new GameDataStore(log);
        var world = PlayfieldWorldSimulation.Create(127, data.GetPlayfieldGeometry(127), data.GetPlayfieldMetaData(127), DestinationsCatalog.Instance, data, log);
        if (!NavMeshPathfinder.TryLoad(data.RootPath, 127, out NavMeshPathfinder finder, out string failure)) { Console.WriteLine("no navmesh " + failure); return; }
        var target = new Vector3(122.879f, 77.0148f, 119.3519f);
        var npc = new Vector3(113.444f, 74.1219f, 133.8678f);
        ReachMap map = finder.BuildReachMap(target, 60f, 4096);
        Console.WriteLine($"map kind={map.Kind} end={map.EndPoint}");
        Console.WriteLine("reaches=" + finder.Reaches(map, npc));
        var e = map.EndPoint;
        Console.WriteLine("LOS end->target=" + world.HasLineOfSight(e.X, e.Y + 1.6f, e.Z, target.X, target.Y + 1.6f, target.Z));
        Console.WriteLine("LOS npc->target=" + world.HasLineOfSight(npc.X, npc.Y + 1.6f, npc.Z, target.X, target.Y + 1.6f, target.Z) + " doorBlocks=" + world.Doors.Blocks(new N3Lite.Vec3(npc.X, npc.Y + 1.6f, npc.Z), new N3Lite.Vec3(target.X, target.Y + 1.6f, target.Z)));
        Console.WriteLine("surfaces under npc x,z:"); Extra.Floors(world, npc.X, npc.Z);
        Console.WriteLine("surfaces under home 116.97,126.85:"); Extra.Floors(world, 116.97f, 126.85f);
        ReachMap home = finder.BuildReachMap(target, 60f, 4096); Console.WriteLine("home reaches=" + finder.Reaches(home, new Vector3(116.97f, 77.0f, 126.85f)));
    }
}
static class Extra
{
    public static void Floors(PlayfieldWorldSimulation world, float x, float z)
    {
        float top = 90f;
        for (int i = 0; i < 6; i++)
        {
            if (!world.TryRaycastDown(new AORebirth.Core.Vector.Vector3(x, top, z), 40f, out var hit)) break;
            Console.WriteLine($"  surface at y={hit.y:0.00}");
            top = (float)hit.y - 0.05f;
        }
    }
}
