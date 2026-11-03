using System.Text.Json;

namespace WclQuery.Core.Knowledge;

/// <summary>
/// A raid map saved by scripts/fetch-map.cs: the world box (yards) its image covers. World X runs north and world Y
/// runs west, so the image's top-left corner is (MaxX, MaxY). The image itself is web/maps/{Id}.png.
/// </summary>
public sealed record GameMap(int Id, string Name, double MinX, double MinY, double MaxX, double MaxY, int Width, int Height)
{
    /// <summary>Image-relative position (0..1 across, 0..1 down) of a world position.</summary>
    public (double U, double V) ToImage(double worldX, double worldY) =>
        ((MaxY - worldY) / (MaxY - MinY), (MaxX - worldX) / (MaxX - MinX));
}

/// <summary>The maps saved in Knowledge/maps.json (embedded), keyed by the game's UiMap id.</summary>
public static class MapCatalog
{
    private static readonly Lazy<Dictionary<int, GameMap>> Maps = new(Load);

    public static GameMap? Get(int id) => Maps.Value.GetValueOrDefault(id);

    private static Dictionary<int, GameMap> Load()
    {
        using var stream = typeof(MapCatalog).Assembly.GetManifestResourceStream("WclQuery.Maps");
        if (stream is null) return [];
        using var doc = JsonDocument.Parse(stream);
        var maps = new Dictionary<int, GameMap>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var m = p.Value;
            var id = int.Parse(p.Name);
            maps[id] = new GameMap(id, m.GetProperty("name").GetString() ?? $"Map {id}",
                m.GetProperty("minX").GetDouble(), m.GetProperty("minY").GetDouble(),
                m.GetProperty("maxX").GetDouble(), m.GetProperty("maxY").GetDouble(),
                m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32());
        }
        return maps;
    }
}
