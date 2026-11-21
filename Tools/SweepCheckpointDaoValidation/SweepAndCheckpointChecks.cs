namespace AORebirth.Tools.SweepCheckpointDaoValidation
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using AORebirth.Database.Domain.Characters;
    using AORebirth.Database.Domain.Quests;
    using AORebirth.Interfaces.Persistence.Characters;
    using MySqlConnector;

    internal static partial class Program
    {
        // Two failures seen live on 2026-11-21, both on the actual persistence sources against real MySQL:
        // the expiry sweep threw on every pass for a mission whose artifact had been sold to a shop, and
        // concurrent character checkpoints deadlocked on the stats table about 140 times an hour.
        private static void ValidateSweepAndCheckpoint(string connectionString)
        {
            ValidateArtifactRetirement(connectionString);
            ValidateConcurrentCheckpoints(connectionString);
        }

        private const int ArtifactOwner = 101;
        private const int ArtifactOtherCharacter = 102;
        private const int ArtifactItemType = 51005;
        private const int ArtifactLowId = 100344;
        private const int ArtifactSource = 2;
        private const int ShopContainerType = 0xC75B;
        private const int NpcContainerType = 0xC350;
        private const int BagContainerType = 0xC749;

        private static void ValidateArtifactRetirement(string connectionString)
        {
            var quests = new MySqlCharacterQuestDao(() => new MySqlConnection(connectionString));

            // Held on the owner's own inventory page: retired with the key.
            ArtifactCase held = SeedArtifactQuest(quests, connectionString, 0, 104, ArtifactOwner);
            IList<RetiredDungeonKey> retired = quests.EndQuestKeys(held.QuestId, held.Artifact);
            Require(retired.Count == 2 && retired.Any(key => key.KeyInstanceId == held.ArtifactInstanceId && key.ContainerType == 104)
                && ArtifactLocation(connectionString, held.ArtifactInstanceId) == "0/0"
                && KeyLinks(connectionString, held.QuestId) == 0,
                "artifact-held-by-owner-is-retired-with-the-key");

            // Sold to a shop: the row was re-homed under the shop. Nothing to retire; the sweep must not throw.
            ArtifactCase sold = SeedArtifactQuest(quests, connectionString, 1, ShopContainerType, 3000038);
            retired = quests.EndQuestKeys(sold.QuestId, sold.Artifact);
            Require(retired.Count == 1 && retired[0].KeyInstanceId == sold.KeyInstanceId
                && ArtifactLocation(connectionString, sold.ArtifactInstanceId) == ShopContainerType + "/3000038"
                && KeyLinks(connectionString, sold.QuestId) == 0,
                "artifact-sold-to-shop-ends-quest-without-touching-the-shop-row");

            // Handed to an NPC (quest turn-in, knubot trade): same.
            ArtifactCase handed = SeedArtifactQuest(quests, connectionString, 2, NpcContainerType, 777);
            retired = quests.EndQuestKeys(handed.QuestId, handed.Artifact);
            Require(retired.Count == 1 && retired[0].KeyInstanceId == handed.KeyInstanceId
                && ArtifactLocation(connectionString, handed.ArtifactInstanceId) == NpcContainerType + "/777"
                && KeyLinks(connectionString, handed.QuestId) == 0,
                "artifact-handed-to-npc-ends-quest-without-touching-the-npc-row");

            // In a bag the owner carries: retired.
            ArtifactCase bagged = SeedArtifactQuest(quests, connectionString, 3, BagContainerType, 0, bagOn: 104, bagOwner: ArtifactOwner);
            retired = quests.EndQuestKeys(bagged.QuestId, bagged.Artifact);
            Require(retired.Count == 2 && retired.Any(key => key.KeyInstanceId == bagged.ArtifactInstanceId && key.ContainerType == BagContainerType
                    && key.ParentContainerType == 104 && key.ParentContainerInstance == ArtifactOwner)
                && ArtifactLocation(connectionString, bagged.ArtifactInstanceId) == "0/0"
                && KeyLinks(connectionString, bagged.QuestId) == 0,
                "artifact-in-owner-bag-is-retired-with-the-key");

            // In a bag that was sold: gone.
            ArtifactCase baggedSold = SeedArtifactQuest(quests, connectionString, 4, BagContainerType, 0, bagOn: ShopContainerType, bagOwner: 3000038);
            retired = quests.EndQuestKeys(baggedSold.QuestId, baggedSold.Artifact);
            Require(retired.Count == 1 && ArtifactLocation(connectionString, baggedSold.ArtifactInstanceId) == BagContainerType + "/" + baggedSold.BagInstanceId
                && KeyLinks(connectionString, baggedSold.QuestId) == 0,
                "artifact-in-sold-bag-ends-quest-without-touching-the-row");

            // On another character's page: that is still refused, and the refusal rolls the whole end back.
            ArtifactCase stolen = SeedArtifactQuest(quests, connectionString, 5, 104, ArtifactOtherCharacter);
            Expect<InvalidOperationException>(() => quests.EndQuestKeys(stolen.QuestId, stolen.Artifact),
                "artifact-on-another-character-is-still-refused");
            Require(KeyLinks(connectionString, stolen.QuestId) == 1
                && ArtifactLocation(connectionString, stolen.KeyInstanceId) == "104/" + ArtifactOwner,
                "artifact-refusal-keeps-key-link-and-key-for-the-retry");

            // Already consumed (container 0): nothing to retire.
            ArtifactCase consumed = SeedArtifactQuest(quests, connectionString, 6, 0, 0);
            retired = quests.EndQuestKeys(consumed.QuestId, consumed.Artifact);
            Require(retired.Count == 1 && KeyLinks(connectionString, consumed.QuestId) == 0,
                "artifact-already-consumed-ends-quest");
        }

        private sealed class ArtifactCase
        {
            internal string QuestId;
            internal int KeyInstanceId;
            internal int ArtifactInstanceId;
            internal int BagInstanceId;
            internal QuestDungeonKeyItemRow Artifact;
        }

        private static ArtifactCase SeedArtifactQuest(MySqlCharacterQuestDao quests, string connectionString, int index,
            int containerType, int containerInstance, int bagOn = 0, int bagOwner = 0)
        {
            var seeded = new ArtifactCase
            {
                QuestId = "artifact.case" + index.ToString(CultureInfo.InvariantCulture),
                KeyInstanceId = 950000 + index * 10,
                ArtifactInstanceId = 950001 + index * 10,
                BagInstanceId = 950002 + index * 10
            };
            quests.SaveGenerated(new GeneratedQuestRow { QuestId = seeded.QuestId, OwnerType = 0, OwnerId = ArtifactOwner,
                DefinitionJson = "{}", AcgBuildingGeneratorJson = "{}", CreatedAtUtcTicks = 1, UpdatedAtUtcTicks = 1, ExpiresAtUtcTicks = 2 });
            using (MySqlConnection connection = Open(connectionString))
            {
                int keyPlacement = 20 + index;
                int artifactPlacement = containerType == 0 ? seeded.ArtifactInstanceId : 40 + index;
                int bagPlacement = 60 + index;
                Execute(connection, "INSERT INTO item_instances (InstanceId,ContainerType,ContainerInstance,ContainerPlacement,ItemType,LowId,HighId,Quality,StackCount,Source) VALUES "
                    + "(" + seeded.KeyInstanceId + ",104," + ArtifactOwner + "," + keyPlacement + ",0xC76D,10,10,1,1,0)");
                Execute(connection, "INSERT INTO questdungeonkeys (KeyInstanceId,QuestId,CreatedAtUtcTicks) VALUES (" + seeded.KeyInstanceId + ",'" + seeded.QuestId + "',1)");
                if (bagOn != 0)
                {
                    Execute(connection, "INSERT INTO item_instances (InstanceId,ContainerType,ContainerInstance,ContainerPlacement,ItemType,LowId,HighId,Quality,StackCount,Source) VALUES "
                        + "(" + seeded.BagInstanceId + "," + bagOn + "," + bagOwner + "," + bagPlacement + ",0xC749,99228,99228,1,1,0)");
                    containerInstance = seeded.BagInstanceId;
                }
                Execute(connection, "INSERT INTO item_instances (InstanceId,ContainerType,ContainerInstance,ContainerPlacement,ItemType,LowId,HighId,Quality,StackCount,Source) VALUES "
                    + "(" + seeded.ArtifactInstanceId + "," + containerType + "," + containerInstance + "," + artifactPlacement + "," + ArtifactItemType + ","
                    + ArtifactLowId + "," + ArtifactLowId + ",1,1," + ArtifactSource + ")");
            }
            seeded.Artifact = new QuestDungeonKeyItemRow { InstanceId = seeded.ArtifactInstanceId, ItemType = ArtifactItemType,
                LowId = ArtifactLowId, HighId = ArtifactLowId, Quality = 1, Source = ArtifactSource, ContainerInstance = ArtifactOwner };
            return seeded;
        }

        private static string ArtifactLocation(string connectionString, int instanceId)
        {
            using (MySqlConnection connection = Open(connectionString))
            using (var command = new MySqlCommand("SELECT CONCAT(ContainerType,'/',ContainerInstance) FROM item_instances WHERE InstanceId=" + instanceId, connection))
                return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static long KeyLinks(string connectionString, string questId)
            => Scalar(connectionString, "SELECT COUNT(*) FROM questdungeonkeys WHERE QuestId='" + questId + "'");

        private static void Execute(MySqlConnection connection, string sql)
        {
            using (var command = new MySqlCommand(sql, connection))
                command.ExecuteNonQuery();
        }

        private const int CheckpointFirst = 201;
        private static readonly int[] CheckpointWriters = { 201, 202, 203, 204, 205, 206 };
        private const int CheckpointIterations = 500;
        /// <summary>The five stats the live deadlock report showed two characters saving at the same moment.</summary>
        private static readonly int[] CheckpointStats = { 397, 180, 525, 526, 587 };
        /// <summary>A checkpoint's usual width: a few dozen dirty stats per save.</summary>
        private static readonly int[] WideStats = Enumerable.Range(1000, 40).ToArray();

        private static void ValidateConcurrentCheckpoints(string connectionString)
        {
            using (MySqlConnection connection = Open(connectionString))
            {
                foreach (int id in CheckpointWriters)
                    Execute(connection, "INSERT INTO characters (Id, Username, Name, FirstName, LastName, playfield, X, Y, Z, HeadingX, HeadingY, HeadingZ, HeadingW, Online) "
                        + "VALUES (" + id + ", 'mission_account', 'Checkpoint" + id + "', '', '', 1, 0, 0, 0, 0, 0, 0, 1, 1)");
                // A populated table, so the index has the shape of a live one rather than a single page.
                for (int character = 300; character < 400; character += 20)
                {
                    var rows = new StringBuilder("INSERT INTO stats (Type,Instance,StatId,StatValue) VALUES ");
                    for (int c = character; c < character + 20; c++)
                        for (int stat = 1000; stat < 1050; stat++)
                            rows.Append(c == character && stat == 1000 ? string.Empty : ",").Append("(50000,").Append(c).Append(',').Append(stat).Append(",1)");
                    Execute(connection, rows.ToString());
                }
            }

            var dao = new MySqlCharacterPersistenceDao(() => Open(connectionString));

            // First checkpoint: every stat is new and is inserted.
            Require(dao.SaveOnlineCheckpoint(CheckpointFirst, null, Stats(0)) && StatRows(connectionString, CheckpointFirst) == CheckpointStats.Length,
                "checkpoint-first-save-inserts-every-stat");
            // Second checkpoint: every stat exists and is updated; a value listed twice keeps the last one.
            var twice = Stats(5).ToList();
            twice.Add(new CharacterStatData { StatId = 397, StatValue = 77 });
            Require(dao.SaveOnlineCheckpoint(CheckpointFirst, null, twice)
                && StatRows(connectionString, CheckpointFirst) == CheckpointStats.Length
                && StatValue(connectionString, CheckpointFirst, 397) == 77 && StatValue(connectionString, CheckpointFirst, 180) == 5,
                "checkpoint-second-save-updates-in-place-last-value-wins");
            // Mixed: one existing, one new.
            Require(dao.SaveOnlineCheckpoint(CheckpointFirst, null, new[] { new CharacterStatData { StatId = 180, StatValue = 9 }, new CharacterStatData { StatId = 600, StatValue = 1 } })
                && StatRows(connectionString, CheckpointFirst) == CheckpointStats.Length + 1
                && StatValue(connectionString, CheckpointFirst, 180) == 9 && StatValue(connectionString, CheckpointFirst, 600) == 1,
                "checkpoint-mixed-save-updates-existing-and-inserts-new");
            // SaveStats (no character lock) shares the writer.
            dao.SaveStats(CheckpointFirst, new[] { new CharacterStatData { StatId = 600, StatValue = 2 }, new CharacterStatData { StatId = 601, StatValue = 3 } });
            Require(StatRows(connectionString, CheckpointFirst) == CheckpointStats.Length + 2
                && StatValue(connectionString, CheckpointFirst, 600) == 2 && StatValue(connectionString, CheckpointFirst, 601) == 3,
                "savestats-updates-and-inserts");

            // Several characters checkpointing at the same moment, as the zone persistence lanes do, each save a few
            // dozen existing stats plus an occasional new one. Live, the multi-row INSERT ... ON DUPLICATE KEY UPDATE
            // writer deadlocked between characters (MySQL error 1213) about 140 times an hour; the writer must never
            // fail and every character's final values must be its own last save.
            var failures = new List<Exception>();
            using (var start = new ManualResetEventSlim(false))
            {
                Task[] writers = CheckpointWriters.Select(id => Task.Run(() =>
                {
                    var own = new MySqlCharacterPersistenceDao(() => Open(connectionString));
                    start.Wait();
                    for (int i = 1; i <= CheckpointIterations; i++)
                    {
                        try
                        {
                            IList<CharacterStatData> stats = Stats(i);
                            foreach (int stat in WideStats) stats.Add(new CharacterStatData { StatId = stat, StatValue = i });
                            if (i % 50 == 0) stats.Add(new CharacterStatData { StatId = 700 + i / 50, StatValue = i });
                            if (!own.SaveOnlineCheckpoint(id, null, stats)) throw new InvalidOperationException("checkpoint-refused-" + id);
                        }
                        catch (Exception exception) { lock (failures) failures.Add(exception); }
                    }
                })).ToArray();
                start.Set();
                Task.WaitAll(writers);
            }
            int deadlocks = failures.Count(f => f is MySqlException m && m.Number == 1213);
            Require(failures.Count == 0, "checkpoint-concurrent-writers-never-fail deadlocks=" + deadlocks + " other=" + (failures.Count - deadlocks)
                + (failures.Count > 0 ? " first=" + failures[0].GetType().Name + ":" + failures[0].Message : string.Empty));
            foreach (int id in CheckpointWriters)
                Require(CheckpointStats.Concat(WideStats).All(stat => StatValue(connectionString, id, stat) == CheckpointIterations)
                    && StatValue(connectionString, id, 700 + CheckpointIterations / 50) == CheckpointIterations
                    && Scalar(connectionString, "SELECT COUNT(*) FROM stats WHERE Type=50000 AND Instance=" + id + " AND StatId BETWEEN 700 AND 799") == CheckpointIterations / 50,
                    "checkpoint-concurrent-final-values-and-row-counts-" + id);
            Require(Scalar(connectionString, "SELECT COUNT(*) FROM (SELECT Type,Instance,StatId FROM stats GROUP BY Type,Instance,StatId HAVING COUNT(*)>1) d") == 0,
                "checkpoint-no-duplicate-stat-rows");
        }

        private static IList<CharacterStatData> Stats(int value)
            => CheckpointStats.Select(stat => new CharacterStatData { StatId = stat, StatValue = value }).ToList();

        private static long StatRows(string connectionString, int characterId)
            => Scalar(connectionString, "SELECT COUNT(*) FROM stats WHERE Type=50000 AND Instance=" + characterId);

        private static long StatValue(string connectionString, int characterId, int statId)
            => Scalar(connectionString, "SELECT StatValue FROM stats WHERE Type=50000 AND Instance=" + characterId + " AND StatId=" + statId);
    }
}
