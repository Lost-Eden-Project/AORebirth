using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Lists every portal (TeleportProxy / TeleportProxy2 / LineTeleport) dynel of a playfield with its raw destination,
// then the teleports DB routes for that playfield when AO_REBIRTH_MYSQL_CONNECTION is set.
// Usage: ProxyDoorProbe <playfieldId|all> [gamedata root]
class P
{
    static object Get(object o, string n)
    {
        if (o == null) return null;
        var t = o.GetType();
        var p = t.GetProperty(n);
        if (p != null) return p.GetValue(o);
        var f = t.GetField(n);
        return f?.GetValue(o);
    }

    static void Main(string[] a)
    {
        string mode = a.Length > 0 ? a[0] : "800";
        string root = a.Length > 1 ? a[1] : Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH") ?? "../../AORebirth/GameData";
        string dir = Path.GetFullPath("../../AORebirth/Built/Debug/ZoneEngine_New");
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var f = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
        var asm = Assembly.LoadFrom(Path.Combine(dir, "ZoneEngine_New.dll"));
        var store = asm.GetTypes().First(t => t.Name == "GameDataStore");
        var deserialize = store.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(q => q.Name.Contains("DeserializeRdbObject") && q.GetParameters().Length == 2 && q.GetParameters()[0].ParameterType == typeof(byte[]));
        var dynelsType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } })
            .First(t => t.Name == "PlayfieldDynels");
        var resolver = asm.GetTypes().First(t => t.Name == "PortalDoorLandingResolver");
        var tryRead = resolver.GetMethod("TryReadPortal", BindingFlags.Public | BindingFlags.Static);

        // (sourcePf, doorInstance) -> (destPf, destDoor, kind)
        var rdb = new Dictionary<(int, uint), (int, int, string)>();
        var destCount = new Dictionary<int, List<string>>();
        IEnumerable<int> playfields = mode == "all"
            ? Directory.GetDirectories(Path.Combine(root, "Playfields")).Select(Path.GetFileName).Select(n => int.TryParse(n, out int v) ? v : -1).Where(v => v > 0)
            : new[] { int.Parse(mode) };
        foreach (int pf in playfields)
        {
            string path = Path.Combine(root, "Playfields", pf.ToString(), "Dynels.dat");
            if (!File.Exists(path)) continue;
            object d;
            try { d = deserialize.MakeGenericMethod(dynelsType).Invoke(null, new object[] { File.ReadAllBytes(path), path }); }
            catch { continue; }
            if (mode != "all") Console.WriteLine($"pf={pf} from {path}");
            foreach (var x in (IEnumerable)Get(d, "Dynels") ?? new object[0])
            {
                var args = new object[] { x, null };
                if (!(bool)tryRead.Invoke(null, args)) continue;
                object dest = args[1];
                int type = Convert.ToInt32(Get(x, "IdentityType"));
                uint inst = unchecked((uint)Convert.ToInt32(Get(x, "IdentityInstance")));
                int destPf = Convert.ToInt32(Get(dest, "PlayfieldId"));
                int destDoor = Convert.ToInt32(Get(dest, "DoorInstance"));
                string kind = Get(dest, "Kind").ToString();
                rdb[(pf, inst)] = (destPf, destDoor, kind);
                if (kind == "DoorDynel")
                {
                    if (!destCount.TryGetValue(destPf, out var list)) destCount[destPf] = list = new List<string>();
                    list.Add($"{pf}:0x{inst:X8}");
                }
                if (mode != "all")
                    Console.WriteLine($"  door type={type} inst=0x{inst:X8} template={Get(x, "TemplateId")} -> pf={destPf} kind={kind} door=0x{destDoor:X8} line={Get(dest, "DestinationIndex")} returns={Get(dest, "RecordsReturn")}");
            }
        }

        if (mode == "all")
        {
            Console.WriteLine("Destinations of walk-in doors that record a return (RecordsReturn + DoorDynel), with metadata:");
            var returning = new Dictionary<int, int>();
            foreach (int pf in Directory.GetDirectories(Path.Combine(root, "Playfields")).Select(Path.GetFileName).Select(n => int.TryParse(n, out int v) ? v : -1).Where(v => v > 0))
            {
                string path = Path.Combine(root, "Playfields", pf.ToString(), "Dynels.dat");
                if (!File.Exists(path)) continue;
                object d;
                try { d = deserialize.MakeGenericMethod(dynelsType).Invoke(null, new object[] { File.ReadAllBytes(path), path }); }
                catch { continue; }
                foreach (var x in (IEnumerable)Get(d, "Dynels") ?? new object[0])
                {
                    var args = new object[] { x, null };
                    if (!(bool)tryRead.Invoke(null, args)) continue;
                    object dest = args[1];
                    if (Get(dest, "Kind").ToString() != "DoorDynel" || !(bool)Get(dest, "RecordsReturn")) continue;
                    int destPf = Convert.ToInt32(Get(dest, "PlayfieldId"));
                    returning[destPf] = returning.TryGetValue(destPf, out int c) ? c + 1 : 1;
                }
            }
            foreach (var pair in returning.OrderBy(p => p.Key))
            {
                string meta = Path.Combine(root, "Playfields", pair.Key.ToString(), "metadata.json");
                string name = "", indoor = "?", zones = "?";
                if (File.Exists(meta))
                {
                    foreach (var line in File.ReadLines(meta))
                    {
                        if (line.Contains("\"playfieldName\"")) name = line.Split(':')[1].Trim().Trim(',', '"');
                        else if (line.Contains("\"isIndoor\"")) indoor = line.Split(':')[1].Trim().Trim(',');
                        else if (line.Contains("\"zoneCount\"")) zones = line.Split(':')[1].Trim().Trim(',');
                    }
                }
                Console.WriteLine($"  dest pf {pair.Key} doors={pair.Value} indoor={indoor} zones={zones} \"{name}\"");
            }

            Console.WriteLine("Door destinations reached from more than one source door (shared interiors):");
            foreach (var pair in destCount.Where(p => p.Value.Count > 1).OrderByDescending(p => p.Value.Count).Take(40))
            {
                string name = "";
                string meta = Path.Combine(root, "Playfields", pair.Key.ToString(), "metadata.json");
                if (File.Exists(meta))
                {
                    var line = File.ReadLines(meta).FirstOrDefault(l => l.Contains("\"playfieldName\""));
                    if (line != null) name = line.Split(':')[1].Trim().Trim(',', '"');
                }
                Console.WriteLine($"  pf {pair.Key} \"{name}\" <- {pair.Value.Count} doors: {string.Join(" ", pair.Value.Take(8))}{(pair.Value.Count > 8 ? " ..." : "")}");
            }
        }

        string conn = Environment.GetEnvironmentVariable("AO_REBIRTH_MYSQL_CONNECTION");
        if (string.IsNullOrEmpty(conn)) { Console.WriteLine("(no AO_REBIRTH_MYSQL_CONNECTION; routes skipped)"); return; }
        var connType = AppDomain.CurrentDomain.GetAssemblies().Concat(new[] { Assembly.LoadFrom(Path.Combine(dir, "MySqlConnector.dll")) })
            .SelectMany(x => { try { return x.GetTypes(); } catch { return new Type[0]; } }).First(t => t.Name == "MySqlConnection");
        using var connection = (IDisposable)Activator.CreateInstance(connType, conn);
        connType.GetMethod("Open").Invoke(connection, null);
        var dao = asm.GetTypes().First(t => t.Name == "TeleportRoutingDao");
        var rows = (IEnumerable)dao.GetMethod("ReadSnapshot").Invoke(null, new object[] { connection });
        Console.WriteLine("teleports routes (DB overrides) with the RDB destination they replace:");
        int total = 0, differing = 0;
        foreach (var r in rows)
        {
            total++;
            int rpf = Convert.ToInt32(Get(r, "Playfield"));
            uint sinst = Convert.ToUInt32(Get(r, "StatelInstance"));
            int dpf = Convert.ToInt32(Get(r, "DestinationPlayfield"));
            uint dinst = Convert.ToUInt32(Get(r, "DestinationInstance"));
            if (mode != "all" && rpf != int.Parse(mode)) continue;
            string rdbText = rdb.TryGetValue((rpf, sinst), out var rd) ? $"rdb pf={rd.Item1} door=0x{rd.Item2:X8} {rd.Item3}" : "rdb: no portal on that door";
            bool differs = !rdb.TryGetValue((rpf, sinst), out var rd2) || rd2.Item1 != dpf;
            if (differs) differing++;
            if (mode != "all" || differs)
                Console.WriteLine($"  route pf={rpf} door=0x{sinst:X8} -> pf={dpf} door=0x{dinst:X8}   [{rdbText}]{(differs ? "  DIFFERENT PLAYFIELD" : "")}");
        }
        Console.WriteLine($"routes total={total} toDifferentPlayfieldThanRdb={differing}");
    }
}
