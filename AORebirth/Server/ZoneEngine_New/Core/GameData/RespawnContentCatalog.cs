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

    /// <summary>
    /// Why NewCharacter.json's StartLocation was not used, or null when it was (or there is none). Reading never throws:
    /// <see cref="Load"/> can also run after startup (IGameData's default RespawnContent re-reads the files), where an
    /// exception would end the tick. GameDataStore refuses to start on it instead.
    /// </summary>
    public string? StartLocationError { get; private set; }

    void ReadStartLocation(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("StartLocation", out JsonElement start)
                || !start.TryGetProperty("Playfield", out JsonElement playfield)
                || !start.TryGetProperty("X", out JsonElement x) || !start.TryGetProperty("Y", out JsonElement y)
                || !start.TryGetProperty("Z", out JsonElement z))
                return;

            if (!playfield.TryGetInt32(out int playfieldId) || playfieldId <= 0
                || !x.TryGetSingle(out float px) || !y.TryGetSingle(out float py) || !z.TryGetSingle(out float pz)
                || !float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
            {
                StartLocationError = path + ": StartLocation needs a positive Playfield and finite X, Y, Z.";
                return;
            }

            StartPlayfieldId = playfieldId;
            StartPosition = [px, py, pz];
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            StartLocationError = path + ": " + exception.Message;
        }
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
