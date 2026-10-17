namespace ZoneEngine_New.Core.GameData;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Core.GameData;

public sealed class RespawnContentCatalog
{
    public static RespawnContentCatalog Empty { get; } = new();

    public int SchemaVersion { get; set; } = 1;

    public int PlayfieldId { get; set; }

    public float[] Position { get; set; } = [];

    /// <summary>
    /// Where a character that has never saved comes back: the new-character start (NewCharacter.json StartLocation,
    /// Arete Landing). 0 when that file has none; then <see cref="PlayfieldId"/> / <see cref="Position"/> are used.
    /// </summary>
    public int StartPlayfieldId { get; private set; }

    public float[] StartPosition { get; private set; } = [];

    public static RespawnContentCatalog Load(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return Empty;

        string path = Path.Combine(root, GameDataPaths.RespawnFileName);
        RespawnContentCatalog content = File.Exists(path) ? Parse(File.ReadAllText(path)) : new RespawnContentCatalog();
        content.ReadStartLocation(Path.Combine(root, GameDataPaths.NewCharacterFileName));
        return content;
    }

    void ReadStartLocation(string path)
    {
        if (!File.Exists(path))
            return;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("StartLocation", out JsonElement start)
            || !start.TryGetProperty("Playfield", out JsonElement playfield)
            || !start.TryGetProperty("X", out JsonElement x) || !start.TryGetProperty("Y", out JsonElement y)
            || !start.TryGetProperty("Z", out JsonElement z))
            return;

        float[] position = [x.GetSingle(), y.GetSingle(), z.GetSingle()];
        if (playfield.GetInt32() <= 0 || position.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Invalid NewCharacter.json StartLocation.");

        StartPlayfieldId = playfield.GetInt32();
        StartPosition = position;
    }

    public static RespawnContentCatalog Parse(string json)
    {
        RespawnContentCatalog content = JsonSerializer.Deserialize<RespawnContentCatalog>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Respawn content is null.");
        content.Validate();
        return content;
    }

    public void Validate()
    {
        if (SchemaVersion != 1
            || PlayfieldId <= 0
            || Position is not { Length: 3 }
            || Position.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException("Invalid respawn content.");
        }
    }
}
