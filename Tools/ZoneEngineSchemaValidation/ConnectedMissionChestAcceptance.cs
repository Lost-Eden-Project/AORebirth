using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using AORebirth.Core.Encryption;
using AORebirth.Core.GameData;
using AORebirth.Database.Domain.Characters;
using AORebirth.Database.Domain.Missions;
using AORebirth.Database.Domain.Quests;
using AORebirth.Interfaces.Persistence.Characters;
using AORebirth.Interfaces.Persistence.Missions;
using MySqlConnector;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Messages.SystemMessages;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.Quests.Dungeons;

static partial class ConnectedAcceptanceSmoke
{
    // A current terminal origin already used by the connected mission fixture.
    const int ChestOriginPlayfield = 710;

    public static void ValidateChests(string zoneBinary, string loginBinary, DisposableSchemaDatabase fixture, MySqlConnection connection)
    {
        string? configuredGameData = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        string gameDataRoot = string.IsNullOrWhiteSpace(configuredGameData)
            ? GameDataPaths.Resolve(Path.GetDirectoryName(Path.GetFullPath(zoneBinary))!)
            : Path.GetFullPath(configuredGameData.Trim());
        Require(Directory.Exists(gameDataRoot), "chest-gamedata-root-missing");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        string? previousConnection = Environment.GetEnvironmentVariable("AO_REBIRTH_MYSQL_CONNECTION");
        string? previousGameData = Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH");
        try
        {
            Environment.SetEnvironmentVariable("AO_REBIRTH_MYSQL_CONNECTION", fixture.ConnectionString);
            Environment.SetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH", gameDataRoot);
            var data = new GameDataStore(new SilentLogger());
            var terminal = data.GetPlayfieldGeometry(ChestOriginPlayfield).Dynels?.Dynels
                .FirstOrDefault(dynel => dynel.IdentityType == MissionTerminal.LiveIdentityType)
                ?? throw new FixtureFailure("connected-chest-real-terminal-missing");
            var xml = XDocument.Load(fixture.ConfigPath);
            xml.Root!.SetElementValue("LoginPort", fixture.LoginPort);
            xml.Save(fixture.ConfigPath);
            FixtureSql.Execute(connection, File.ReadAllText(Path.Combine(RepositoryRoot(),
                "AORebirth/Libraries/Source/AORebirth.Database/SqlTables/login.sql")));
            FixtureSql.Execute(connection, File.ReadAllText(Path.Combine(RepositoryRoot(),
                "AORebirth/Libraries/Source/AORebirth.Database/SqlTables/charactersperks.sql")));
            // Activation reads these current catalog tables even when the fixture has no database vendors.
            // Keep their canonical definitions without importing the unrelated vendor/stock seed corpus.
            foreach (string table in new[] { "vendors", "vendortemplate", "shopinventorytemplates" })
            {
                string sql = File.ReadAllText(Path.Combine(RepositoryRoot(),
                    "AORebirth/Libraries/Source/AORebirth.Database/SqlTables/" + table + ".sql"));
                FixtureSql.Execute(connection, sql.Substring(0, sql.IndexOf(';') + 1));
            }
            using (var command = new MySqlCommand("INSERT INTO login (Id,CreationDate,Email,FirstName,LastName,Username,Password,AllowedCharacters,Flags,AccountFlags,Expansions,GM) VALUES (9901,'2026-10-17','fixture@invalid','','',@account,@hash,6,0,0,2047,1)", connection))
            {
                command.Parameters.AddWithValue("@account", Account);
                command.Parameters.AddWithValue("@hash", PasswordHash.CreateHash(password));
                command.ExecuteNonQuery();
            }
            using (var command = new MySqlCommand("INSERT INTO characters (Id,Username,Name,FirstName,LastName,Playfield,X,Y,Z,HeadingW,HeadingX,HeadingY,HeadingZ,Online) VALUES (9901,@account,'ChestConnectedFixture','','',@pf,@x,@y,@z,1,0,0,0,0)", connection))
            {
                command.Parameters.AddWithValue("@account", Account);
                command.Parameters.AddWithValue("@pf", ChestOriginPlayfield);
                command.Parameters.AddWithValue("@x", terminal.Position.X);
                command.Parameters.AddWithValue("@y", terminal.Position.Y);
                command.Parameters.AddWithValue("@z", terminal.Position.Z);
                command.ExecuteNonQuery();
            }
            var stats = LifecycleSpawnFixture.Stats();
            stats[CharacterStat.Level] = 60;
            stats[CharacterStat.Side] = (int)Side.Omni;
            stats[CharacterStat.GmLevel] = 1;
            stats[CharacterStat.Health] = stats[CharacterStat.MaxHealth] = 1000000;
            stats[CharacterStat.ExternalPlayfieldInstance] = stats[CharacterStat.ExternalDoorInstance] = 0;
            foreach (var stat in stats)
                FixtureSql.Execute(connection, $"INSERT INTO stats (Type,Instance,StatId,StatValue) VALUES (50000,{Owner},{(int)stat.Key},{stat.Value})");
            var repository = new MySqlInventoryRepository(new SilentLogger());
            int retainedItem = repository.LeaseInstanceIdBlock(1);
            repository.Insert(new ItemInstanceRecord { InstanceId = retainedItem, ContainerType = 104, ContainerInstance = Owner,
                ContainerPlacement = 90, ItemType = 0xC76D, LowId = 43384, HighId = 43384,
                Quality = 1, StackCount = 3, Source = AORebirth.Enums.ItemSource.Loot });
            var quests = new MySqlCharacterQuestDao(() => fixture.Open());
            var missions = new MySqlMissionDao(() => fixture.Open());
            var inventory = new MySqlCharacterPersistenceDao(() => fixture.Open());
            string retainedBefore = JsonSerializer.Serialize(inventory.LoadCarriedItems(Owner).Single(row => row.InstanceId == retainedItem));
            Console.WriteLine("SOURCE_SHA=" + Git("rev-parse", "HEAD"));
            Console.WriteLine("CONNECTED_CHEST_FIXTURE=LABELLED_DISPOSABLE_ONLY POST_START_WRITES=NETWORK_ONLY");
            using var login = new ConnectedEngineProcess(loginBinary, true, fixture, gameDataRoot);
            string questId;
            QuestDungeonParameters parameters;
            string[] frozenFlags;
            MissionChestSnapshot chest;
            PersistedItemData taken;
            PersistedItemData[] remainder;
            int firstProcess;
            using (var zone = new ConnectedEngineProcess(zoneBinary, false, fixture, gameDataRoot))
            {
                firstProcess = zone.Id;
                Console.WriteLine("CONNECTED_CHEST_ENGINE_PID_BEFORE=" + firstProcess + " BINARY_SHA256=" + zone.BinarySha256);
                ZoneLoginMessage ticket;
                using (var terminalClient = EnterChest(fixture, password, out ticket))
                {
                    terminalClient.Send(new QuestAlternativeMessage { Identity = Character, VersionId = 4,
                        MissionTerminalIdentity = new Identity { Type = (IdentityType)terminal.IdentityType, Instance = terminal.IdentityInstance },
                        LevelSlider = 6, GoodBadSlider = 0, OrderChaosSlider = 0, OpenHiddenSlider = 0,
                        PhysicalMysticalSlider = 0, HeadOnStealthSlider = 0, MoneyExperienceSlider = 0, QuestInfos = [] }, Owner);
                    terminalClient.Wait<QuestAlternativeMessage>(message => message.QuestInfos.Length == 5);
                    GeneratedMissionOffer offer = missions.ReadOffers(Owner).OrderBy(value => value.MissionType == 2 ? 0 : 1)
                        .First(value => value.MissionType != 4);
                    terminalClient.Send(new CreateQuestMessage { Identity = Character,
                        QuestIdentity = new Identity { Type = (IdentityType)offer.OfferType, Instance = offer.OfferInstance } }, Owner);
                    terminalClient.Wait<QuestFullUpdateMessage>(message => message.Quests.Length > 0);
                    GeneratedQuestRow generatedQuest = quests.LoadGeneratedForCharacter(Owner).Single();
                    questId = generatedQuest.QuestId;
                    Require(QuestDungeonParameters.TryParse(generatedQuest.AcgBuildingGeneratorJson, out parameters), "chest-accepted-current-frozen-layout");
                    Require(quests.LoadKeyQuests(inventory.LoadCarriedItems(Owner).Where(row => row.LowId == QuestDungeonService.KeyLowId)
                        .Select(row => row.InstanceId).ToArray()).Values.Contains(questId), "chest-acceptance-key-link");
                    Console.WriteLine("CONNECTED_CHEST_GENERATE_ACCEPT=PASS VIA=TCP_CURRENT_QUEST_DUNGEON");
                    Command(terminalClient, FormattableString.Invariant($".tp {parameters.EntranceX:R} {parameters.EntranceY:R} {parameters.EntranceZ:R} {parameters.EntrancePlayfield}"));
                    terminalClient.Wait<N3TeleportMessage>();
                    if (parameters.EntrancePlayfield == ChestOriginPlayfield)
                    {
                        SendChestUse(terminalClient, new Identity { Type = IdentityType.MissionEntrance, Instance = parameters.EntranceInstance });
                        terminalClient.Wait<N3TeleportMessage>();
                    }
                    terminalClient.Wait<ZoneRedirectionMessage>();
                }
                if (parameters.EntrancePlayfield != ChestOriginPlayfield)
                {
                    using var entranceClient = EnterChestTicket(fixture, ticket);
                    SendChestUse(entranceClient, new Identity { Type = IdentityType.MissionEntrance, Instance = parameters.EntranceInstance });
                    entranceClient.Wait<N3TeleportMessage>();
                    entranceClient.Wait<ZoneRedirectionMessage>();
                }
                using (var dungeonClient = EnterChestTicket(fixture, ticket))
                {
                    frozenFlags = ChestFlagSnapshot(missions, questId);
                    Require(frozenFlags.Length > 1, "chest-entry-materializes-nonempty-population");
                    var selected = missions.ReadCharacter(Owner).Flags.Where(flag => flag.QuestId == questId
                            && flag.FlagKey.StartsWith("mission-chest:1:", StringComparison.Ordinal) && flag.FlagKey.Contains(":chest:", StringComparison.Ordinal))
                        .Select(flag => JsonSerializer.Deserialize<MissionChestSnapshot>(flag.Value)!)
                        .Select(snapshot => (Snapshot: snapshot, Contents: inventory.LoadContainerItems(snapshot.ContainerInstance)
                            .OrderBy(row => row.ContainerPlacement).ToArray()))
                        .OrderByDescending(value => value.Contents.Length).First();
                    chest = selected.Snapshot;
                    PersistedItemData[] contents = selected.Contents;
                    Require(contents.Length is >= 2 and <= 3, "fixture-limitation-partial-loot-requires-a-naturally-rolled-multi-item-chest");
                    MoveNearChest(dungeonClient, chest);
                    CheckChestSpawn(dungeonClient, chest);
                    InventoryUpdateMessage opened = OpenChest(dungeonClient, chest, contents);
                    taken = contents[0];
                    TakeChestItem(dungeonClient, opened, taken, 80);
                    Until(() => inventory.LoadCarriedItems(Owner).Any(row => row.InstanceId == taken.InstanceId), "chest-first-loot-durable");
                    remainder = contents.Skip(1).ToArray();
                    CloseChest(dungeonClient, chest);
                    OpenChest(dungeonClient, chest, remainder);
                    VerifyChestRows(inventory, chest, taken, remainder);
                    ChestLogout(dungeonClient, connection);
                    Console.WriteLine("CONNECTED_CHEST_ENTRY_OPEN_TAKE_REOPEN=PASS REMAINING=" + remainder.Length);
                }
                using (var reconnected = EnterChest(fixture, password, out _))
                {
                    VerifyCarriedWire(reconnected, taken);
                    MoveNearChest(reconnected, chest);
                    CheckChestSpawn(reconnected, chest);
                    OpenChest(reconnected, chest, remainder);
                    VerifyChestRows(inventory, chest, taken, remainder);
                    Require(ChestFlagSnapshot(missions, questId).SequenceEqual(frozenFlags), "chest-reconnect-frozen-identities-and-positions");
                    ChestLogout(reconnected, connection);
                    Console.WriteLine("CONNECTED_CHEST_AUTH_RECONNECT=PASS");
                }
                zone.Stop();
            }
            using (var zone = new ConnectedEngineProcess(zoneBinary, false, fixture, gameDataRoot))
            {
                Require(zone.Id != firstProcess, "chest-distinct-process-restart");
                Console.WriteLine("CONNECTED_CHEST_ENGINE_PID_AFTER=" + zone.Id + " BINARY_SHA256=" + zone.BinarySha256);
                using (var restarted = EnterChest(fixture, password, out _))
                {
                    VerifyCarriedWire(restarted, taken);
                    MoveNearChest(restarted, chest);
                    CheckChestSpawn(restarted, chest);
                    InventoryUpdateMessage opened = OpenChest(restarted, chest, remainder);
                    Require(ChestFlagSnapshot(missions, questId).SequenceEqual(frozenFlags), "chest-restart-frozen-identities-and-positions");
                    for (int index = 0; index < remainder.Length; index++)
                        TakeChestItem(restarted, opened, remainder[index], 81 + index);
                    Until(() => inventory.LoadContainerItems(chest.ContainerInstance).Count == 0, "chest-empty-after-restart-loot");
                    CloseChest(restarted, chest);
                    OpenChest(restarted, chest, []);
                    Require(JsonSerializer.Serialize(inventory.LoadCarriedItems(Owner).Single(row => row.InstanceId == retainedItem)) == retainedBefore,
                        "chest-unrelated-player-item-preserved");
                    foreach (PersistedItemData row in remainder.Prepend(taken))
                        Require(inventory.LoadCarriedItems(Owner).Count(item => item.InstanceId == row.InstanceId) == 1, "chest-original-instance-owned-once");
                    ChestLogout(restarted, connection);
                    Console.WriteLine("CONNECTED_CHEST_DISTINCT_PROCESS_RESTART=PASS FROZEN_POPULATION=PASS EMPTY_REOPEN=PASS PLAYER_RETENTION=PASS");
                }
                zone.Stop();
            }
            login.Stop();
            Console.WriteLine("CONNECTED_MISSION_CHEST_ACCEPTANCE=PASS PRODUCTION_CONTACT=NO ORIGINAL_CLIENT_ACCEPTANCE=NOT_EXERCISED");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AO_REBIRTH_MYSQL_CONNECTION", previousConnection);
            Environment.SetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH", previousGameData);
        }
    }

    static ConnectedWireClient EnterChest(DisposableSchemaDatabase fixture, string password, out ZoneLoginMessage ticket)
    {
        ticket = Authorize(fixture, password);
        return EnterChestTicket(fixture, ticket);
    }

    static ConnectedWireClient EnterChestTicket(DisposableSchemaDatabase fixture, ZoneLoginMessage ticket)
    {
        var client = new ConnectedWireClient(fixture.ZonePort);
        try
        {
            client.Send(ticket);
            client.Wait<FullCharacterMessage>(message => message.Identity == Character);
            client.Wait<SpecialAttackWeaponMessage>(message => message.Identity == Character);
            client.Send(new CharInPlayMessage { Identity = Character }, Owner);
            client.Wait<CharInPlayMessage>(message => message.Identity == Character);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    static void ChestLogout(ConnectedWireClient client, MySqlConnection connection)
    {
        client.Send(new CharacterActionMessage { Identity = Character, Action = CharacterActionType.Logout }, Owner);
        client.Wait<StartLogoutMessage>();
        Until(() => FixtureSql.Scalar(connection, $"SELECT Online FROM characters WHERE Id={Owner}") == 0, "chest-logout");
    }

    static string[] ChestFlagSnapshot(MySqlMissionDao missions, string quest)
        => missions.ReadCharacter(Owner).Flags.Where(flag => flag.QuestId == quest && flag.FlagKey.StartsWith("mission-chest:1:", StringComparison.Ordinal))
            .OrderBy(flag => flag.FlagKey, StringComparer.Ordinal).Select(flag => flag.FlagKey + "=" + flag.Value).ToArray();

    static void CheckChestSpawn(ConnectedWireClient client, MissionChestSnapshot chest)
    {
        var identity = new Identity { Type = IdentityType.Container, Instance = chest.ContainerInstance };
        ChestItemFullUpdateMessage spawn = client.Received.OfType<ChestItemFullUpdateMessage>().FirstOrDefault(message => message.Identity == identity)
            ?? client.Wait<ChestItemFullUpdateMessage>(message => message.Identity == identity);
        Require(spawn.Coordinate.X == chest.X && spawn.Coordinate.Y == chest.Y && spawn.Coordinate.Z == chest.Z,
            "chest-spawn-frozen-transform");
        Require(spawn.StateMachine.Type == (IdentityType)chest.SpawnDefinition!.StateMachineType
            && spawn.StateMachine.Instance == chest.SpawnDefinition.StateMachineInstance, "chest-spawn-frozen-appearance");
    }

    static void MoveNearChest(ConnectedWireClient client, MissionChestSnapshot chest)
    {
        double yaw = chest.YawDegrees * Math.PI / 180;
        // Direct-control Update uses the production movement handler. This is not a walking/navigation claim.
        client.Send(new CharDCMoveMessage { Identity = Character, MoveType = (byte)MovementAction.Update,
            Heading = new Quaternion { X = 0, Y = 0, Z = 0, W = 1 },
            Coordinates = new Vector3(chest.X + (float)Math.Sin(yaw) * 1.5f, chest.Y, chest.Z + (float)Math.Cos(yaw) * 1.5f) }, Owner);
    }

    static void SendChestUse(ConnectedWireClient client, Identity target)
        => client.Send(new GenericCmdMessage { Identity = Character, Temp1 = 0, Count = 21,
            Action = GenericCmdAction.Use, Temp4 = 1, User = Character, Target = [target] }, Owner);

    static InventoryUpdateMessage OpenChest(ConnectedWireClient client, MissionChestSnapshot chest, PersistedItemData[] expected)
    {
        var identity = new Identity { Type = IdentityType.Container, Instance = chest.ContainerInstance };
        SendChestUse(client, identity);
        InventoryUpdateMessage opened = client.Wait<InventoryUpdateMessage>(message => message.BagIdentity == identity);
        Require(opened.NumberOfSlots == Chest.LootCapacity && opened.SlotnumberInMainInventory > 0
            && opened.Entries.Length == expected.Length, "chest-open-original-contents-contract");
        foreach (PersistedItemData row in expected)
            Require(opened.Entries.Any(entry => entry.Identity.Instance == row.InstanceId && entry.Slotnumber == row.ContainerPlacement
                && entry.LowId == row.LowId && entry.HighId == row.HighId && entry.Quality == row.Quality), "chest-open-exact-remaining-instance");
        client.Wait<GenericCmdMessage>(message => message.Action == GenericCmdAction.Use && message.Temp1 == 1 && message.Target.Contains(identity));
        return opened;
    }

    static void CloseChest(ConnectedWireClient client, MissionChestSnapshot chest)
    {
        var identity = new Identity { Type = IdentityType.Container, Instance = chest.ContainerInstance };
        SendChestUse(client, identity);
        client.Wait<ActionMessage>(message => message.Identity == identity && message.ActionIdentity == 0x66);
        client.Wait<GenericCmdMessage>(message => message.Action == GenericCmdAction.Use && message.Temp1 == 1 && message.Target.Contains(identity));
    }

    static void TakeChestItem(ConnectedWireClient client, InventoryUpdateMessage opened, PersistedItemData row, int targetSlot)
    {
        var source = new Identity { Type = IdentityType.Backpack, Instance = opened.SlotnumberInMainInventory << 16 | row.ContainerPlacement };
        client.Send(new ClientMoveItemToInventoryMessage { Identity = Character, SourceContainer = source, TargetPlacement = targetSlot }, Owner);
        client.Wait<ContainerAddItemMessage>(message => message.Identity == Character && message.SourceContainer == source && message.TargetPlacement == targetSlot);
    }

    static void VerifyChestRows(MySqlCharacterPersistenceDao inventory, MissionChestSnapshot chest, PersistedItemData taken, PersistedItemData[] remainder)
    {
        PersistedItemData carried = inventory.LoadCarriedItems(Owner).Single(row => row.InstanceId == taken.InstanceId);
        Require(carried.ContainerType == 104 && carried.ContainerInstance == Owner && carried.LowId == taken.LowId
            && carried.HighId == taken.HighId && carried.Quality == taken.Quality && carried.StackCount == taken.StackCount && carried.Source == taken.Source,
            "chest-looted-item-exact-durable-owner-and-definition");
        Require(inventory.LoadContainerItems(chest.ContainerInstance).OrderBy(row => row.ContainerPlacement).Select(row => JsonSerializer.Serialize(row))
            .SequenceEqual(remainder.Select(row => JsonSerializer.Serialize(row))), "chest-remaining-durable-rows-unchanged");
    }

    static void VerifyCarriedWire(ConnectedWireClient client, PersistedItemData row)
        => Require(client.Received.OfType<FullCharacterMessage>().Single(message => message.Identity == Character).InventorySlots
            .Count(slot => slot.Identity.Instance == row.InstanceId && slot.Placement == 80 && slot.ItemLowId == row.LowId
                && slot.ItemHighId == row.HighId && slot.Quality == row.Quality) == 1, "chest-reconnect-carried-item-wire");
}
