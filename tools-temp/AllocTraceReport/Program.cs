using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// args: trace.nettrace [topN]
// Aggregates sampled allocation events (AllocationTick, ~every 100 KB) by type and by call stack, and lists GC pauses.
string path = args[0];
int top = args.Length > 1 ? int.Parse(args[1]) : 30;
string etlx = TraceLog.CreateFromEventPipeDataFile(path);
using var log = new TraceLog(etlx);
var byType = new Dictionary<string, (long bytes, int n)>();
var byStack = new Dictionary<string, (long bytes, int n)>();
var byFrame = new Dictionary<string, long>();
long total = 0;
var gcs = new List<(double startMs, double pauseMs, int gen, string reason, long promoted)>();
double gcStart = 0; int gcGen = 0; string gcReason = "";
var clr = log.Events.GetSource();
var parser = new ClrTraceEventParser(clr);
parser.GCAllocationTick += data =>
{
    long bytes = data.AllocationAmount64;
    total += bytes;
    string type = data.TypeName ?? "?";
    byType[type] = byType.TryGetValue(type, out var t) ? (t.bytes + bytes, t.n + 1) : (bytes, 1);
    var cs = data.CallStack();
    var frames = new List<string>();
    var seen = new HashSet<string>();
    while (cs != null && frames.Count < 14)
    {
        string f = cs.CodeAddress.FullMethodName;
        if (string.IsNullOrEmpty(f)) f = cs.CodeAddress.ModuleName + "!?";
        if (!f.StartsWith("System.", StringComparison.Ordinal) || frames.Count > 0 && !frames[^1].StartsWith("System.", StringComparison.Ordinal))
            frames.Add(f);
        if (seen.Add(f)) byFrame[f] = byFrame.TryGetValue(f, out var b) ? b + bytes : bytes;
        cs = cs.Caller;
    }
    string key = type + "\n      " + string.Join("\n      ", frames.Take(7));
    byStack[key] = byStack.TryGetValue(key, out var s) ? (s.bytes + bytes, s.n + 1) : (bytes, 1);
};
parser.GCStart += d => { gcStart = d.TimeStampRelativeMSec; gcGen = d.Depth; gcReason = d.Reason.ToString(); };
parser.GCHeapStats += d => { };
parser.GCStop += d => gcs.Add((gcStart, d.TimeStampRelativeMSec - gcStart, gcGen, gcReason, 0));
clr.Process();

Console.WriteLine($"Sampled allocation bytes: {total / 1048576.0:F1} MB over {log.SessionDuration.TotalSeconds:F0}s  (~{total / 1048576.0 / log.SessionDuration.TotalSeconds:F2} MB/s)");
Console.WriteLine($"\nGCs: {gcs.Count}  gen0={gcs.Count(g => g.gen == 0)} gen1={gcs.Count(g => g.gen == 1)} gen2={gcs.Count(g => g.gen == 2)}");
foreach (var g in gcs.OrderByDescending(g => g.pauseMs).Take(12))
    Console.WriteLine($"  t={g.startMs / 1000:F1}s gen{g.gen} {g.pauseMs:F1}ms {g.reason}");
Console.WriteLine($"\nTop types:");
foreach (var kv in byType.OrderByDescending(k => k.Value.bytes).Take(top))
    Console.WriteLine($"  {kv.Value.bytes / 1048576.0,8:F1} MB {100.0 * kv.Value.bytes / total,5:F1}%  {kv.Key}");
Console.WriteLine($"\nTop AORebirth/ZoneEngine frames (inclusive):");
foreach (var kv in byFrame.Where(k => k.Key.StartsWith("ZoneEngine_New") || k.Key.StartsWith("AORebirth")).OrderByDescending(k => k.Value).Take(top))
    Console.WriteLine($"  {kv.Value / 1048576.0,8:F1} MB {100.0 * kv.Value / total,5:F1}%  {kv.Key}");
Console.WriteLine($"\nTop stacks:");
foreach (var kv in byStack.OrderByDescending(k => k.Value.bytes).Take(top))
    Console.WriteLine($"  {kv.Value.bytes / 1048576.0,8:F1} MB {100.0 * kv.Value.bytes / total,5:F1}%  {kv.Key}");
