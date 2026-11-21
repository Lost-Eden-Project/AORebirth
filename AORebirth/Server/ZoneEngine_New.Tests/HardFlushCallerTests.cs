namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// <c>InventoryFlushService.HardFlush</c> waits on the player's write-behind lane, and a lane writer takes the player's
/// persistence gate to finish, so it throws when its caller holds that gate (lane first, then gate). Knubot lines,
/// quest completion and NextStep gives all run under the gate. A dungeon give called from them shipped and broke the
/// Unicorn Recruiter. This test lists every reviewed HardFlush call site, so a new one fails here until someone
/// confirms it cannot run under the gate and adds a test that drives it.
/// </summary>
[TestClass]
public sealed class HardFlushCallerTests
{
    /// <summary>HardFlush calls per source file (relative to ZoneEngine_New), each reviewed as never under the gate.</summary>
    static readonly Dictionary<string, int> Reviewed = new(StringComparer.Ordinal)
    {
        // The daily login claim action handler.
        ["Core/DailyLogin/DailyLoginRewardService.cs"] = 1,
        // Opening a mission chest.
        ["Core/Entities/Chest.cs"] = 1,
        // The logout drain.
        ["Core/Inventory/InventoryFlushService.cs"] = 1,
        // Logout and zone transfer.
        ["Core/Playfield/SpawnService.cs"] = 2,
        // Dungeon accept (QuestService defers a give made under the gate: KnubotDungeonQuestGateTests), the teammate
        // join projection, and key duplication.
        ["Core/Quests/Dungeons/QuestDungeonService.cs"] = 3,
        // Cross-instance transfer.
        ["Core/World/CrossInstanceTransfer.cs"] = 1,
    };

    static readonly Regex Call = new(@"(?<!\bvoid\s+)\bHardFlush\s*\(", RegexOptions.CultureInvariant);

    [TestMethod]
    public void EveryHardFlushCallerIsReviewedForThePersistenceGate()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "ZoneEngine_New"));
        if (!Directory.Exists(root))
            Assert.Inconclusive("No ZoneEngine_New source at " + root + ".");

        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            int count = Call.Matches(File.ReadAllText(path)).Count;
            if (count > 0)
                found[relative] = count;
        }

        var changed = found.Keys.Union(Reviewed.Keys)
            .Where(file => found.GetValueOrDefault(file) != Reviewed.GetValueOrDefault(file))
            .Select(file => file + ": " + found.GetValueOrDefault(file) + " call(s), " + Reviewed.GetValueOrDefault(file) + " reviewed")
            .ToArray();
        Assert.AreEqual(0, changed.Length,
            "HardFlush call sites changed. HardFlush throws when the caller holds Player.PersistenceGate, as Knubot lines, "
            + "quest completion and NextStep gives do. Confirm each new caller never runs under the gate (or defers, as "
            + "QuestService.TryAssignTemplate does), add a test that drives it, then update Reviewed:" + Environment.NewLine
            + string.Join(Environment.NewLine, changed));
    }

    static string ThisFile([CallerFilePath] string path = "") => path;
}
