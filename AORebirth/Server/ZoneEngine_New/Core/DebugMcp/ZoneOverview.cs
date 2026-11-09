namespace ZoneEngine_New.Core.DebugMcp
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;

    using ModelContextProtocol.Server;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Metrics;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// One read-only snapshot of the whole zone engine: process memory, then every loaded playfield with its players,
    /// dynels, cell heat and navmesh memory. Served as the <c>zone_overview</c> MCP tool and as plain JSON at
    /// <c>GET /overview</c> on the debug listener, so a dashboard can poll it without speaking MCP.
    /// </summary>
    public static class ZoneOverview
    {
        /// <summary>Path of the plain-JSON endpoint on the debug listener.</summary>
        public const string Route = "/overview";

        const int TickWindowSeconds = 5;
        const int DefaultTickRate = 32;

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        static readonly DateTime StartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        public static string BuildJson(PlayfieldManager playfields, IPlayfieldMetricsRegistry metrics)
            => JsonSerializer.Serialize(Build(playfields, metrics), JsonOptions);

        public static object Build(PlayfieldManager playfields, IPlayfieldMetricsRegistry metrics)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            ArgumentNullException.ThrowIfNull(metrics);

            var tickById = new Dictionary<int, PlayfieldMetricsSnapshot>();
            foreach (PlayfieldMetricsSnapshot snapshot in metrics.GetAllSnapshots(TickWindowSeconds))
                tickById[snapshot.PlayfieldId] = snapshot;

            DateTime now = DateTime.UtcNow;
            var rows = new List<PlayfieldRow>();
            var dynels = new List<Dynel>();
            foreach (Playfield playfield in playfields.SnapshotPlayfields())
                rows.Add(Row(playfield, tickById, dynels, now));

            rows.Sort(static (a, b) => b.Players != a.Players ? b.Players.CompareTo(a.Players) : b.Dynels.CompareTo(a.Dynels));

            using Process process = Process.GetCurrentProcess();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            return new
            {
                generatedUtc = now,
                uptimeSeconds = (long)(now - StartedUtc).TotalSeconds,
                process = new
                {
                    workingSetMb = Mb(process.WorkingSet64),
                    privateMb = Mb(process.PrivateMemorySize64),
                    managedHeapMb = Mb(GC.GetTotalMemory(false)),
                    gcCommittedMb = Mb(gc.TotalCommittedBytes),
                    gcHeapSizeMb = Mb(gc.HeapSizeBytes),
                    machineAvailableMb = Mb(gc.TotalAvailableMemoryBytes),
                    threads = process.Threads.Count
                },
                totals = new
                {
                    playfields = rows.Count,
                    dungeons = rows.Count(static r => r.Kind == "quest-dungeon"),
                    players = rows.Sum(static r => r.Players),
                    dynels = rows.Sum(static r => r.Dynels),
                    npcs = rows.Sum(static r => r.Npcs),
                    cellsHot = rows.Sum(static r => r.Cells.Hot),
                    cellsWarm = rows.Sum(static r => r.Cells.Warm),
                    cellsCold = rows.Sum(static r => r.Cells.Cold),
                    cellsAsleep = rows.Sum(static r => r.Cells.Asleep),
                    navmeshMb = Mb(rows.Sum(static r => r.Navmesh?.EstimatedBytes ?? 0))
                },
                playfields = rows
            };
        }

        static PlayfieldRow Row(
            Playfield playfield,
            Dictionary<int, PlayfieldMetricsSnapshot> tickById,
            List<Dynel> dynels,
            DateTime now)
        {
            int id = playfield.Identity.Instance;
            playfield.GetRequiredService<DynelRegistry>().CopyDynels(dynels);
            int players = 0;
            int npcs = 0;
            int npcsDead = 0;
            for (int i = 0; i < dynels.Count; i++)
            {
                switch (dynels[i])
                {
                    case Player:
                        players++;
                        break;
                    case NpcCharacter { IsDead: true }:
                        npcsDead++;
                        break;
                    case NpcCharacter:
                        npcs++;
                        break;
                }
            }

            CellHeatCounts heat = playfield.GetRequiredService<PlayfieldLocality>().HeatCounts;
            var cells = new CellsView(
                heat.UpdatedUtc == default ? "not-ticked-yet" : heat.Scheduled ? "heat" : "every-heartbeat",
                heat.TotalCells,
                heat.Hot,
                heat.Warm,
                heat.Cold,
                heat.Asleep,
                heat.TickedLastHeartbeat,
                heat.UpdatedUtc == default ? null : Math.Round((now - heat.UpdatedUtc).TotalSeconds, 1));

            NavmeshView? navmesh = null;
            if (playfield.Pathfinder is { } finder)
            {
                AORebirth.World.Pathfinding.NavMeshResidency r = finder.Residency;
                navmesh = new NavmeshView(
                    r.IndexedTiles,
                    r.LoadedTiles,
                    r.WindowCapacity,
                    Mb(r.ResidentTileBytes),
                    Mb(r.InMemoryMeshBytes),
                    r.ResidentTileBytes + r.InMemoryMeshBytes);
            }

            TickView? tick = null;
            if (tickById.TryGetValue(id, out PlayfieldMetricsSnapshot? snapshot))
            {
                int rate = playfield.TickRate > 0 ? playfield.TickRate : DefaultTickRate;
                tick = new TickView(
                    R(snapshot.TickAverageMs),
                    R(snapshot.TickMaxMs),
                    Math.Round(1000.0 / rate, 2),
                    Math.Round(snapshot.TickSampleCount / (double)TickWindowSeconds, 1),
                    rate);
            }

            int count = dynels.Count;
            dynels.Clear();
            return new PlayfieldRow(
                id,
                playfield is QuestDungeonPlayfield ? "quest-dungeon" : "ordinary",
                players,
                count,
                npcs,
                npcsDead,
                cells,
                navmesh,
                tick);
        }

        static double Mb(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 1);

        static double? R(double? value) => value.HasValue ? Math.Round(value.Value, 2) : null;

        sealed record PlayfieldRow(
            int Id,
            string Kind,
            int Players,
            int Dynels,
            int Npcs,
            int NpcsDead,
            CellsView Cells,
            NavmeshView? Navmesh,
            TickView? Tick);

        sealed record CellsView(
            string Mode,
            int Total,
            int Hot,
            int Warm,
            int Cold,
            int Asleep,
            int TickedLastHeartbeat,
            double? AgeSeconds);

        sealed record NavmeshView(
            int IndexedTiles,
            int ResidentTiles,
            int WindowCapacity,
            double ResidentTileMb,
            double InMemoryBakeMb,
            [property: System.Text.Json.Serialization.JsonIgnore] long EstimatedBytes);

        sealed record TickView(double? AvgMs, double? MaxMs, double BudgetMs, double TicksPerSecond, int ConfiguredRate);
    }

    /// <summary>MCP face of <see cref="ZoneOverview"/>.</summary>
    [McpServerToolType]
    public sealed class ZoneOverviewTools
    {
        readonly PlayfieldManager _playfields;
        readonly IPlayfieldMetricsRegistry _metrics;

        public ZoneOverviewTools(PlayfieldManager playfields, IPlayfieldMetricsRegistry metrics)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            ArgumentNullException.ThrowIfNull(metrics);
            _playfields = playfields;
            _metrics = metrics;
        }

        [McpServerTool(Name = "zone_overview")]
        [System.ComponentModel.Description("Whole-engine snapshot: process memory (working set, private, managed heap, GC committed), totals, and every loaded playfield with players, dynels, live/dead NPCs, cells Hot/Warm/Cold/Asleep and how many ticked last heartbeat, navmesh tiles and MB resident, and 5 s tick timing. Most players first. Read-only. Same JSON as GET /overview.")]
        public string Overview() => ZoneOverview.BuildJson(_playfields, _metrics);
    }
}
