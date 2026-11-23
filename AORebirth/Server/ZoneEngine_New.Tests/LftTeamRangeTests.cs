using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using AORebirth.Core.GameData;
using ChatEngine.PacketHandlers;
using ChatEngine.Packets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZoneEngine_New.Tests;

/// <summary>
/// LFT "Team range only": the search answers only candidates inside the searcher's team range when the client
/// ticks the box (filter3 == 1), and every answer's clear row carries the searcher's range for the client's marks.
/// </summary>
[TestClass]
public sealed class LftTeamRangeTests
{
    const uint StockFilter3 = 0xFFFFFFFF;

    [TestMethod]
    public void SearcherRangeIsTheLevelEligibilityWindow()
    {
        (int min, int max) = EligibilityRange(50);

        Assert.IsTrue(LftSearch.TryGetTeamRange(50, out uint minimum, out uint maximum));

        Assert.AreEqual((uint)min, minimum);
        Assert.AreEqual((uint)max, maximum);
    }

    [TestMethod]
    public void TickedBoxHidesCandidatesOutsideTheRangeAndKeepsTheEdges()
    {
        LftSearch.TryGetTeamRange(50, out uint minimum, out uint maximum);

        Assert.IsFalse(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, 200), "A level 200 was listed to a level 50.");
        Assert.IsFalse(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, 1), "A level 1 was listed to a level 50.");
        Assert.IsFalse(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, (int)minimum - 1));
        Assert.IsFalse(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, (int)maximum + 1));
        Assert.IsTrue(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, (int)minimum));
        Assert.IsTrue(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, (int)maximum));
        Assert.IsTrue(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, 50));
    }

    [TestMethod]
    public void StockOrUntickedSearchListsEveryLevel()
    {
        LftSearch.TryGetTeamRange(50, out uint minimum, out uint maximum);

        foreach (uint filter3 in new[] { StockFilter3, 0u })
        {
            Assert.IsTrue(LftSearch.PassesTeamRange(filter3, minimum, maximum, 200));
            Assert.IsTrue(LftSearch.PassesTeamRange(filter3, minimum, maximum, 1));
        }
    }

    [TestMethod]
    public void UnknownSearcherLevelSendsNoRangeAndFiltersNothing()
    {
        Assert.IsFalse(LftSearch.TryGetTeamRange(0, out uint minimum, out uint maximum));
        Assert.AreEqual(0u, minimum);
        Assert.AreEqual(0u, maximum);

        Assert.IsTrue(LftSearch.PassesTeamRange(LftSearch.TeamRangeOnly, minimum, maximum, 200));
    }

    [TestMethod]
    public void ClearRowCarriesTheSearcherRangeInItsLevelAndPlayfieldFields()
    {
        LftSearch.TryGetTeamRange(50, out uint minimum, out uint maximum);

        byte[] row = LftQueryReply.CreateClear(minimum, maximum);

        // Frame: u16 type, u16 length | B mode, I id, S name (u16 0), I level, I playfield, B side, B prof, S comment.
        Assert.AreEqual((ushort)1501, BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(0, 2)));
        Assert.AreEqual(LftQueryReply.ModeClear, row[4]);
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(5, 4)));
        Assert.AreEqual(minimum, BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(11, 4)));
        Assert.AreEqual(maximum, BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(15, 4)));
        Assert.AreEqual(row.Length - 4, (int)BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(2, 2)));
    }

    [TestMethod]
    public void ClearRowWithoutARangeIsTheCapturedEmptyReply()
    {
        // Capture 20260727-104625: type 1501, 19-byte body, mode 2, every other field zero.
        byte[] captured = new byte[4 + 19];
        captured[0] = 0x05;
        captured[1] = 0xDD;
        captured[3] = 19;
        captured[4] = LftQueryReply.ModeClear;

        CollectionAssert.AreEqual(captured, LftQueryReply.CreateClear());
        CollectionAssert.AreEqual(captured, LftQueryReply.CreateClear(0, 0));
    }

    static (int Min, int Max) EligibilityRange(int level)
    {
        string path = Path.Combine(GameDataPaths.ResolveRuntimeRoot(), "Teams", "LevelEligibility.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement range in document.RootElement.GetProperty("Ranges").EnumerateArray())
        {
            if (range.GetProperty("Level").GetInt32() == level)
            {
                return (range.GetProperty("Minimum").GetInt32(), range.GetProperty("Maximum").GetInt32());
            }
        }

        throw new InvalidDataException("LevelEligibility.json has no range for level " + level);
    }
}
