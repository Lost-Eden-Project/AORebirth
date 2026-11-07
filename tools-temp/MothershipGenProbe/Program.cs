// Throwaway probe: builds Alien Mothership (style 386) dungeons through the server generator with the
// DungeonGeneration.json band settings, so the DungeonEntrances.json data (entrance, lifts, boss floor) is exercised.
using AORebirth.DungeonGenerator;

string root = args.Length > 0 ? args[0] : @"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData";
var generator = new DungeonGenerator(root);
StyleRoomSet style = generator.LoadStyle(386);
Console.WriteLine($"style 386: all={style.All.Count} entrances={style.Entrances.Count} mainHalls={style.MainHalls.Count} growth={style.Growth.Count} terminators={style.Terminators.Count}");

(int floors, int minSize, int maxSize)[] bands = [(3, 7, 9), (4, 9, 11), (5, 10, 12), (5, 12, 14)];
int ok = 0, failed = 0;
foreach (var (floors, minSize, maxSize) in bands)
{
    for (int seed = 1; seed <= 25; seed++)
    {
        int size = minSize + seed % (maxSize - minSize + 1);
        try
        {
            DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
            {
                StyleId = 386, Seed = seed * 7919, FloorCount = floors, FloorSize = size, FloorHeight = 64,
                BossFloor = true, DescendingFloors = false, UseFloorTransitions = true, LockedDoorChancePercent = 0,
                BuildingInstance = unchecked((int)3221229836u)
            });
            ok++;
            if (seed <= 2)
            {
                var perFloor = result.Rooms.GroupBy(r => r.Floor).OrderBy(g => g.Key)
                    .Select(g => $"f{g.Key}:{g.Count()}");
                Console.WriteLine($"floors={floors} size={size} seed={seed}: rooms={result.Rooms.Count} [{string.Join(' ', perFloor)}] " +
                    $"transitions={result.Transitions.Count} boss={result.BossRoomIndex} attempts={result.Attempts} valid={result.Validation}");
                foreach (DungeonFloorTransition t in result.Transitions)
                    Console.WriteLine($"   lift {result.Rooms[t.From.RoomIndex].Shape.Template.Name}@{t.From.Position} -> {result.Rooms[t.To.RoomIndex].Shape.Template.Name}@{t.To.Position} buttons {t.From.ButtonTemplateId}/{t.To.ButtonTemplateId}");
            }
        }
        catch (Exception exception)
        {
            failed++;
            Console.WriteLine($"floors={floors} size={size} seed={seed}: FAILED {exception.GetType().Name}: {exception.Message}");
        }
    }
}
Console.WriteLine($"ok={ok} failed={failed}");
