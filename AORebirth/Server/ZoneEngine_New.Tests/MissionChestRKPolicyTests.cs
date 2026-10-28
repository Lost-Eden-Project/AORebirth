namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AORebirth.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Commands;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Quests.Dungeons;

[TestClass]
public sealed class MissionChestRKPolicyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SeasonalExclusionOverridesOrdinaryApprovalAtEitherEndpoint()
    {
        MissionChestLootExclusions exclusions = MissionChestLootExclusions.Parse("""
            {"Version":1,"Entries":[{"TemplateIds":[2],"Category":"Seasonal.Halloween",
              "Evidence":["GameData:Playfields/event/Spawns.json#Events/Halloween"]}]}
            """);
        MissionChestRKAllowlist allowlist = Approval(1, 2, 1, 250, exclusions);
        Assert.IsFalse(allowlist.IsAllowed(1, 2, 50));
        Assert.AreEqual("Unapproved", allowlist.ClassificationFor(1, 2, 50));
        Assert.AreEqual("Seasonal.Halloween", allowlist.ExclusionReasons(1, 2).Single());
        Assert.AreEqual(0, allowlist.EvidenceFor(1, 2, 50).Length);
        SyntheticCatalog catalog = Catalog();
        var builder = new CountingBuilder(new ItemBuilder(catalog, new CountingIds(), new StubLogger()));
        var service = new MissionChestLootService(catalog, Data(), builder, Policy(), allowlist);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RollPreview(50, new Random(1)));
        Assert.AreEqual(0, builder.TemplateCalls);
        Assert.AreEqual(0, builder.MintCalls);
    }

    [TestMethod]
    public void SeasonalFamilyExclusionsCoverEveryCanonicalMemberWithoutAnEventEnablementFlag()
    {
        MissionChestLootExclusions exclusions = MissionChestLootExclusions.Parse("""
            {"Version":1,"Entries":[{"TemplateIds":[],"FamilyHash":"SYNTHETIC","Category":"Seasonal.Winter",
              "Evidence":["GameData:event-loot-binding"]}]}
            """, Data().ItemFamilies);
        Assert.IsTrue(exclusions.IsExcluded(1, 1));
        Assert.IsTrue(exclusions.IsExcluded(2, 2));
        Assert.IsFalse(exclusions.IsExcluded(3, 3));
        Assert.ThrowsExactly<InvalidDataException>(() => MissionChestLootExclusions.Parse("""
            {"Version":1,"Entries":[{"TemplateIds":[],"FamilyHash":"MISSING","Category":"Seasonal.Other",
              "Evidence":["GameData:event-loot-binding"]}]}
            """, Data().ItemFamilies));
    }

    [TestMethod]
    public void SpecialPurposeVetoWinsEvenWhenAnExactPairWasApproved()
    {
        foreach (string category in new[] { "ExpansionExclusive.Shadowlands", "ExpansionExclusive.AlienInvasion",
            "ExpansionExclusive.LostEden", "ExpansionExclusive.Xan", "QuestProgression", "BossExclusive",
            "Promotional", "GmTest", "Seasonal.Anniversary" })
        {
            MissionChestLootExclusions exclusions = MissionChestLootExclusions.Parse(JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new[] { new { TemplateIds = new[] { 1 }, Category = category,
                    Evidence = new[] { "GameData:synthetic-exclusion-proof" } } }
            }));
            Assert.IsFalse(Approval(1, 2, 1, 250, exclusions).IsAllowed(1, 2, 50), category);
        }
    }

    [TestMethod]
    public void ExactPairAndDocumentedQualityAreRequired()
    {
        MissionChestRKAllowlist allowlist = Approval(1, 2, 40, 60);
        Assert.IsTrue(allowlist.IsAllowed(1, 2, 50));
        Assert.IsFalse(allowlist.IsAllowed(1, 1, 50));
        Assert.IsFalse(allowlist.IsAllowed(2, 1, 50));
        Assert.IsFalse(allowlist.IsAllowed(1, 3, 50));
        Assert.IsFalse(allowlist.IsAllowed(1, 2, 39));
        Assert.IsFalse(allowlist.IsAllowed(1, 2, 61));
        Assert.AreEqual("https://example.test/reviewed-rk-chest", allowlist.EvidenceFor(1, 2, 50).Single());
    }

    [TestMethod]
    public void SourceRangesRemainCompleteWhileExistingMissionItemWindowIsPreserved()
    {
        MissionChestRKAllowlist allowlist = Approval(1, 2, 1, 300);
        Assert.IsTrue(allowlist.IsAllowed(1, 2, 300), "Source bounds are not the mission-quality ceiling.");
        SyntheticCatalog catalog = new(
            new ItemTemplate { Id = 1, Name = "Synthetic low", Quality = 1, DynelType = 51005,
                Stats = new() { [CharacterStat.Can] = (int)CanFlags.Carry } },
            new ItemTemplate { Id = 2, Name = "Synthetic high", Quality = 300, DynelType = 51005,
                Stats = new() { [CharacterStat.Can] = (int)CanFlags.Carry } });
        var service = new MissionChestLootService(catalog, Data(), new ItemBuilder(catalog, new CountingIds(), new StubLogger()),
            Policy(), allowlist);
        var random = new Random(7919);
        for (int index = 0; index < 100; index++)
            foreach (MissionChestLootPreview item in service.RollPreview(250, random))
                Assert.IsTrue(item.Quality is >= 245 and <= 250);
    }

    [TestMethod]
    public void MissingEmptyAndInvalidPoliciesNeverApproveAnything()
    {
        Assert.AreEqual(0, MissionChestRKAllowlist.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Count);
        Assert.IsFalse(MissionChestRKAllowlist.Parse("""{"Version":1,"Entries":[]}""").IsAllowed(1, 1, 1));
        foreach (string json in new[]
        {
            "null", "{}", """{"Version":2,"Entries":[]}""",
            """{"Version":1,"Entries":[{"LowId":1,"HighId":2,"MinQuality":1,"MaxQuality":250,"Evidence":[]}]}""",
            """{"Version":1,"Entries":[{"LowId":1,"HighId":2,"MinQuality":2,"MaxQuality":1,"Evidence":["https://example.test/source"],"Classification":"OrdinaryRubiKa"}]}"""
        })
            Assert.ThrowsExactly<InvalidDataException>(() => MissionChestRKAllowlist.Parse(json));
    }

    [TestMethod]
    public void UnauthorizedPairIsRejectedBeforeInterpolation()
    {
        SyntheticCatalog catalog = Catalog();
        var builder = new CountingBuilder(new ItemBuilder(catalog, new CountingIds(), new StubLogger()));
        var service = new MissionChestLootService(catalog, Data(), builder, Policy(), Approval(999, 999, 50, 50));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RollItem(50, new Random(1)));
        Assert.AreEqual(0, builder.TemplateCalls);
        Assert.AreEqual(0, builder.MintCalls);
        Assert.AreEqual(0, service.GetDiagnostics(50).EligibleChoices);
    }

    [TestMethod]
    public void ApprovedFixedEndpointDoesNotAuthorizeItsUnreviewedFamilyPartner()
    {
        SyntheticCatalog catalog = Catalog();
        var service = new MissionChestLootService(catalog, Data(), new ItemBuilder(catalog, new CountingIds(), new StubLogger()),
            Policy(), Approval(1, 1, 1, 1));
        Item item = service.RollItem(1, new Random(1));
        Assert.AreEqual(1, item.LowId);
        Assert.AreEqual(1, item.HighId);
        Assert.AreEqual(1, item.Quality);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RollItem(50, new Random(1)));
    }

    [TestMethod]
    public void ReviewedExistingPairDoesNotRequireAnUnrelatedLootHashLeaf()
    {
        SyntheticCatalog catalog = Catalog();
        var service = new MissionChestLootService(catalog, new StubGameData(HashItemCatalog.Parse("{}")),
            new ItemBuilder(catalog, new CountingIds(), new StubLogger()), Policy(), Approval(1, 2, 1, 250));
        var random = new Random(7919);
        for (int index = 0; index < 100; index++)
            foreach (MissionChestLootPreview item in service.RollPreview(50, random))
            {
                Assert.AreEqual(1, item.LowId);
                Assert.AreEqual(2, item.HighId);
                Assert.IsTrue(item.Quality is >= 45 and <= 55);
            }
    }

    [TestMethod]
    public void TenThousandSyntheticChestPopulationsCoverAllMissionQualitiesWithNoUnauthorizedItems()
    {
        SyntheticCatalog catalog = Catalog();
        MissionChestRKAllowlist allowlist = Approval(1, 2, 1, 250);
        var service = new MissionChestLootService(catalog, Data(), new ItemBuilder(catalog, new CountingIds(), new StubLogger()),
            Policy(), allowlist);
        var random = new Random(104729);
        int populations = 0;
        for (int quality = 1; quality <= 250; quality++)
        {
            Assert.IsTrue(service.GetDiagnostics(quality).EligibleChoices > 0);
            for (int index = 0; index < 40; index++)
            {
                IReadOnlyList<Item> items = service.Roll(quality, random);
                Assert.IsTrue(items.Count is >= 1 and <= 3);
                foreach (Item item in items)
                {
                    Assert.IsTrue(MissionChestLootService.IsApproved(item, quality, allowlist));
                    Assert.AreEqual(1, item.LowId);
                    Assert.AreEqual(2, item.HighId);
                    Assert.IsFalse(item.IsPersisted);
                    Assert.AreEqual(ItemSource.Loot, item.Source);
                }
                populations++;
            }
        }
        Assert.AreEqual(10000, populations);
    }

    [TestMethod]
    public void PreviewUsesProductionSelectionsWithoutAllocatingInstances()
    {
        SyntheticCatalog catalog = Catalog();
        var ids = new CountingIds();
        var service = new MissionChestLootService(catalog, Data(), new ItemBuilder(catalog, ids, new StubLogger()),
            Policy(), Approval(1, 2, 1, 250));
        IReadOnlyList<MissionChestLootPreview> preview = service.RollPreview(50, new Random(7919));
        Assert.AreEqual(0, ids.Calls);
        IReadOnlyList<Item> items = service.Roll(50, new Random(7919));
        Assert.AreEqual(items.Count, preview.Count);
        Assert.AreEqual(items.Count, ids.Calls);
        for (int index = 0; index < items.Count; index++)
        {
            Assert.AreEqual(items[index].LowId, preview[index].LowId);
            Assert.AreEqual(items[index].HighId, preview[index].HighId);
            Assert.AreEqual(items[index].Quality, preview[index].Quality);
            Assert.AreEqual(items[index].Name, preview[index].Name);
            Assert.AreEqual("Missing", preview[index].Rarity);
            Assert.AreEqual(1, preview[index].Evidence.Length);
        }
    }

    [TestMethod]
    [TestCategory("MissionChestRKDeploymentCoverage")]
    public void ActualPrivateAllowlistMustSupportTenThousandNormalChestPopulations()
    {
        // This is a release-quality test, not an instruction to expand the policy to make it pass.
        // Supply the candidate data root explicitly; never substitute synthetic approvals for deployment.
        GameDataStore data = MissionChestPlacementTests.Data.Value;
        ItemTemplateCatalog catalog = MissionChestPlacementTests.Templates.Value;
        string policyRoot = Environment.GetEnvironmentVariable("AO_REBIRTH_CHEST_RK_POLICY_CANDIDATE_ROOT") ?? data.RootPath;
        MissionChestRKAllowlist allowlist = MissionChestRKAllowlist.Load(policyRoot, data.ItemFamilies);
        Assert.IsTrue(allowlist.Count > 0, allowlist.UnavailableReason);
        string? reviewPath = Environment.GetEnvironmentVariable("AO_REBIRTH_CHEST_RK_REVIEW_RESULTS_PATH");
        Assert.IsFalse(string.IsNullOrWhiteSpace(reviewPath), "Provide the existing approved private review results; do not rebuild the catalog for this test.");
        byte[] reviewedBytes = File.ReadAllBytes(reviewPath!);
        using JsonDocument evidenceManifest = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(Path.GetDirectoryName(reviewPath!)!, "policy-candidate-manifest.json")));
        JsonElement reviewedDescriptor = evidenceManifest.RootElement.GetProperty("Files").EnumerateArray()
            .Single(row => row.GetProperty("File").GetString() == Path.GetFileName(reviewPath));
        string reviewedHash = Convert.ToHexString(SHA256.HashData(reviewedBytes)).ToLowerInvariant();
        Assert.AreEqual(reviewedDescriptor.GetProperty("Sha256").GetString(), reviewedHash);
        foreach (string file in new[] { "MissionChestRKAllowlist.json", "MissionChestSeasonalExclusions.json", "MissionChestSpecialExclusions.json" })
        {
            string expected = evidenceManifest.RootElement.GetProperty("Files").EnumerateArray()
                .Single(row => row.GetProperty("File").GetString() == file).GetProperty("Sha256").GetString()!;
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(policyRoot, "Loot", file)))).ToLowerInvariant();
            Assert.AreEqual(expected, actual, "Use the reviewed private runtime policy: " + file);
        }
        using JsonDocument review = JsonDocument.Parse(reviewedBytes);
        Dictionary<int, string> categories = review.RootElement.GetProperty("ApprovedDefinitions").EnumerateArray()
            .ToDictionary(row => row.GetProperty("Id").GetInt32(), row => row.GetProperty("Category").GetString()!);
        int[] approvedIds = allowlist.ApprovedPairs.SelectMany(pair => new[] { pair.LowId, pair.HighId }).Distinct().ToArray();
        CollectionAssert.AreEquivalent(categories.Keys.ToArray(), approvedIds,
            "Every reviewed definition must remain eligible; do not reduce implants or rebalance categories.");
        Dictionary<string, int> approvedCategories = categories.Values.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.AreEqual(review.RootElement.GetProperty("ExpectedApprovedDefinitionCount").GetInt32(), categories.Count);
        Dictionary<string, int> expectedCategories = review.RootElement.GetProperty("ExpectedApprovedCategoryCounts")
            .EnumerateObject().ToDictionary(row => row.Name, row => row.Value.GetInt32(), StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(expectedCategories.ToArray(), approvedCategories.ToArray(),
            "Reviewed category eligibility must remain complete; do not reduce implants or rebalance categories.");
        int[] previousIds = review.RootElement.GetProperty("PreviousApprovedDefinitionIds").EnumerateArray()
            .Select(row => row.GetInt32()).ToArray();
        Assert.IsTrue(previousIds.Length > 0);
        Assert.IsTrue(previousIds.All(categories.ContainsKey), "Previously approved definitions must remain reviewed and eligible.");
        MissionChestRKAllowlist.Entry[] previousBands = ReadReviewedBands(review.RootElement, "PreviousApprovedBands");
        foreach (MissionChestRKAllowlist.Entry band in previousBands)
            for (int quality = band.MinQuality; quality <= band.MaxQuality; quality++)
                Assert.IsTrue(allowlist.IsAllowed(band.LowId, band.HighId, quality),
                    $"Previously approved band was reduced: {band.LowId}/{band.HighId} QL {quality}.");
        MissionChestPolicy policy = MissionChestPlacementTests.ReadPolicy();
        Assert.AreEqual(1, policy.MinItems);
        Assert.AreEqual(3, policy.MaxItems);
        CollectionAssert.AreEquivalent(Policy().RarityWeights.ToArray(), policy.RarityWeights.ToArray(), "Existing rarity weights must be unchanged.");
        var ids = new CountingIds();
        var service = new MissionChestLootService(catalog, data, new ItemBuilder(catalog, ids, new StubLogger()),
            policy, allowlist);
        int[] gaps = Enumerable.Range(1, 250).Where(quality => service.GetDiagnostics(quality).EligibleChoices == 0).ToArray();
        Assert.AreEqual(0, gaps.Length, "Uncovered mission QLs: " + string.Join(",", gaps));
        MissionChestRKAllowlist.Entry[] newBands = ReadReviewedBands(review.RootElement, "NewlyApprovedBands");
        Assert.IsTrue(newBands.Length > 0, "This integration must prove its reviewed additions reach the production selector.");
        var newBandItemCounts = newBands.ToDictionary(
            band => (band.LowId, band.HighId, band.MinQuality, band.MaxQuality), _ => 0);
        var random = new Random(15485863);
        int populations = 0;
        int totalItems = 0;
        var categoryCounts = new SortedDictionary<string, int>(approvedCategories.ToDictionary(pair => pair.Key, _ => 0), StringComparer.Ordinal);
        var rarityCounts = new SortedDictionary<string, int>(policy.RarityWeights.ToDictionary(pair => pair.Key, _ => 0), StringComparer.Ordinal);
        var qualityCounts = new SortedDictionary<int, int>();
        for (int quality = 1; quality <= 250; quality++)
        {
            qualityCounts[quality] = 0;
            for (int index = 0; index < 40; index++)
            {
                IReadOnlyList<MissionChestLootPreview> items = service.RollPreview(quality, random);
                Assert.IsTrue(items.Count is >= 1 and <= 3);
                foreach (MissionChestLootPreview item in items)
                {
                    Assert.IsTrue(allowlist.IsAllowed(item.LowId, item.HighId, item.Quality));
                    Assert.AreEqual(0, allowlist.ExclusionReasons(item.LowId, item.HighId).Length,
                        "Expansion, seasonal, special-purpose and progression vetoes always override approval.");
                    Assert.AreEqual(MissionChestRKAllowlist.OrdinaryClassification, allowlist.ClassificationFor(item.LowId, item.HighId, item.Quality));
                    Assert.IsTrue(categories.TryGetValue(item.LowId, out string? category));
                    Assert.AreEqual(category, categories[item.HighId]);
                    Assert.IsTrue(item.Quality >= Math.Max(1, quality - 5) && item.Quality <= Math.Min(250, quality + 5));
                    Assert.IsTrue(item.Evidence.Length > 0);
                    Assert.IsTrue(policy.RarityWeights.ContainsKey(item.Rarity));
                    categoryCounts[category!]++;
                    rarityCounts[item.Rarity]++;
                    qualityCounts[quality]++;
                    foreach (MissionChestRKAllowlist.Entry band in newBands)
                        if (item.LowId == band.LowId && item.HighId == band.HighId
                            && item.Quality >= band.MinQuality && item.Quality <= band.MaxQuality)
                        {
                            newBandItemCounts[(band.LowId, band.HighId, band.MinQuality, band.MaxQuality)]++;
                        }
                    totalItems++;
                }
                populations++;
            }
        }
        Assert.AreEqual(10000, populations);
        Assert.AreEqual(0, ids.Calls, "Read-only diagnostics must never allocate durable item instances.");
        Assert.IsTrue(qualityCounts.Values.All(count => count is >= 40 and <= 120));
        MissionChestLootExclusions exclusions = MissionChestLootExclusions.Load(policyRoot, data.ItemFamilies);
        var newBandProbes = new List<object>();
        foreach (MissionChestRKAllowlist.Entry band in newBands)
        {
            int minimum = Math.Max(1, band.MinQuality);
            int maximum = Math.Min(250, band.MaxQuality);
            Assert.IsTrue(minimum <= maximum, $"New band has no supported mission quality: {band.LowId}/{band.HighId}.");
            int[] checkpoints = new[] { minimum, minimum + (maximum - minimum) / 2, maximum }.Distinct().ToArray();
            foreach (int quality in Enumerable.Range(band.MinQuality, band.MaxQuality - band.MinQuality + 1))
                Assert.IsTrue(allowlist.IsAllowed(band.LowId, band.HighId, quality), "New reviewed band is absent from the installed policy.");
            band.Evidence = allowlist.EvidenceFor(band.LowId, band.HighId, minimum);
            band.Classification = MissionChestRKAllowlist.OrdinaryClassification;
            MissionChestRKAllowlist isolatedApproval = MissionChestRKAllowlist.Parse(JsonSerializer.Serialize(
                new MissionChestRKAllowlist.Document { Version = 1, Entries = [band] }), exclusions);
            var probe = new MissionChestLootService(catalog, data, new ItemBuilder(catalog, ids, new StubLogger()),
                policy, isolatedApproval);
            foreach (int quality in checkpoints)
            {
                IReadOnlyList<MissionChestLootPreview> selected = probe.RollPreview(quality, new Random(7919));
                Assert.IsTrue(selected.Count is >= 1 and <= 3);
                foreach (MissionChestLootPreview item in selected)
                {
                    Assert.AreEqual(band.LowId, item.LowId);
                    Assert.AreEqual(band.HighId, item.HighId);
                    Assert.IsTrue(item.Quality >= minimum && item.Quality <= maximum);
                    Assert.IsTrue(item.Quality >= Math.Max(1, quality - 5) && item.Quality <= Math.Min(250, quality + 5));
                    Assert.IsTrue(allowlist.IsAllowed(item.LowId, item.HighId, item.Quality));
                    Assert.AreEqual(0, allowlist.ExclusionReasons(item.LowId, item.HighId).Length);
                }
                string command = string.Join("\n", ChestCommand.Simulate(probe, quality,
                    "reviewed new-band probe", 1, new Random(7919)));
                Assert.IsTrue(command.Contains($"ID {band.LowId}/{band.HighId}; QL ", StringComparison.Ordinal));
                Assert.IsTrue(command.Contains("Chest populations: 1", StringComparison.Ordinal));
                Assert.IsTrue(command.Contains("classification OrdinaryRubiKa", StringComparison.Ordinal));
                newBandProbes.Add(new { band.LowId, band.HighId, band.MinQuality, band.MaxQuality,
                    MissionQuality = quality, Selected = selected, ChestCommandReportsPair = true });
            }
        }
        Assert.AreEqual(0, ids.Calls, "New-band selector and command probes must never allocate item instances.");
        string result = JsonSerializer.Serialize(new
        {
            Campaign = "ProductionSelectorRKChestPopulations", Populations = populations, TotalItems = totalItems,
            Seed = 15485863, MissionQualities = 250, PopulationsPerQuality = 40,
            ApprovedDefinitions = categories.Count, ApprovedBands = allowlist.Count,
            ApprovedCategoryDefinitions = approvedCategories, CategoryItemCounts = categoryCounts,
            CategoryPercentages = categoryCounts.ToDictionary(pair => pair.Key, pair => 100d * pair.Value / totalItems),
            RarityItemCounts = rarityCounts, RarityWeights = policy.RarityWeights,
            ItemsPerMissionQuality = qualityCounts, DurableInstancesAllocated = ids.Calls,
            UnauthorizedItems = 0, ExcludedItems = 0, ReviewSha256 = reviewedHash,
            PreviousDefinitionsRetained = previousIds.Length, PreviousBandsRetained = previousBands.Length,
            NewlyApprovedBands = newBands.Length, NewBandProbes = newBandProbes,
            NewBandNormalCampaignItemCounts = newBands.Select(band => new
            {
                band.LowId, band.HighId, band.MinQuality, band.MaxQuality,
                ItemCount = newBandItemCounts[(band.LowId, band.HighId, band.MinQuality, band.MaxQuality)]
            }).ToArray(),
            NewBandProbeMethod = "Unchanged production selector with each reviewed new band isolated; minimum/midpoint/maximum supported mission QLs; unchanged vetoes; shared chest command preview",
            Selection = "Production RollPreview; the shared production selector; no inventory or database writes",
            CategoryDistributionPolicy = "Diagnostic only; no balancing or eligibility reduction"
        });
        TestContext.WriteLine("MISSION_CHEST_RK_CAMPAIGN " + result);
        string? output = Environment.GetEnvironmentVariable("AO_REBIRTH_CHEST_VALIDATION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            string path = Path.ChangeExtension(Path.GetFullPath(output), ".rk-loot-campaign.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, result + Environment.NewLine);
        }
    }

    static MissionChestRKAllowlist.Entry[] ReadReviewedBands(JsonElement review, string property)
    {
        MissionChestRKAllowlist.Entry[] bands = review.GetProperty(property).EnumerateArray().Select(row =>
            new MissionChestRKAllowlist.Entry
            {
                LowId = row.GetProperty("LowId").GetInt32(), HighId = row.GetProperty("HighId").GetInt32(),
                MinQuality = row.GetProperty("MinQuality").GetInt32(), MaxQuality = row.GetProperty("MaxQuality").GetInt32()
            }).ToArray();
        Assert.IsTrue(bands.All(band => band.LowId > 0 && band.HighId > 0 && band.MinQuality > 0
            && band.MaxQuality >= band.MinQuality), "Reviewed bands must retain valid identities and QL bounds.");
        return bands;
    }

    internal static MissionChestRKAllowlist Approval(int low, int high, int minimum, int maximum,
        MissionChestLootExclusions? exclusions = null)
        => MissionChestRKAllowlist.Parse(JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[] { new { LowId = low, HighId = high, MinQuality = minimum, MaxQuality = maximum,
                Evidence = new[] { "https://example.test/reviewed-rk-chest" },
                Classification = MissionChestRKAllowlist.OrdinaryClassification } }
        }), exclusions);

    static MissionChestPolicy Policy() => new()
    {
        MinItems = 1, MaxItems = 3,
        RarityWeights = new Dictionary<string, double>
        {
            ["Missing"] = 70, ["1"] = 20, ["2"] = 6, ["3"] = 2.5, ["4"] = 1, ["5"] = 0.5
        }
    };

    static StubGameData Data() => new(HashItemCatalog.Parse("""{"SYNTHETIC":{"Templates":[1,2]}}"""));

    static SyntheticCatalog Catalog() => new(
        new ItemTemplate { Id = 1, Name = "Reviewed synthetic", Quality = 1, DynelType = 51005,
            Stats = new() { [CharacterStat.Can] = (int)CanFlags.Carry } },
        new ItemTemplate { Id = 2, Name = "Reviewed synthetic", Quality = 250, DynelType = 51005,
            Stats = new() { [CharacterStat.Can] = (int)CanFlags.Carry } },
        new ItemTemplate { Id = 3, Name = "Unapproved synthetic", Quality = 50, DynelType = 51005,
            Stats = new() { [CharacterStat.Can] = (int)CanFlags.Carry, [CharacterStat.Rarity] = 5 } });

    sealed class SyntheticCatalog(params ItemTemplate[] templates) : IItemTemplateCatalog
    {
        readonly Dictionary<int, ItemTemplate> _items = templates.ToDictionary(item => item.Id);
        public IReadOnlyCollection<ItemTemplate> Templates => _items.Values;
        public bool TryGet(int id, out ItemTemplate template) => _items.TryGetValue(id, out template!);
        public ItemTemplate Require(int id) => _items[id];
    }

    sealed class CountingIds : IItemInstanceIdAllocator
    {
        public int Calls { get; private set; }
        public int Allocate() => ++Calls;
    }

    sealed class CountingBuilder(IItemBuilder inner) : IItemBuilder
    {
        public int TemplateCalls { get; private set; }
        public int MintCalls { get; private set; }
        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
        {
            TemplateCalls++;
            return inner.CreateTemplate(lowId, highId, quality);
        }
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            MintCalls++;
            return inner.CreateWithNewInstance(lowId, highId, quality, source, stackCount);
        }
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1,
            int instanceId = 0, Identity? identity = null, byte[]? statsBlob = null)
            => inner.Create(lowId, highId, quality, source, stackCount, instanceId, identity, statsBlob);
        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => inner.TryFromInstanceRecord(row, out item);
    }
}
