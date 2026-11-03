#:package SixLabors.ImageSharp@3.1.11
// Saves a raid's in-game map for the site's heatmaps: the map image (web/maps/<id>.png) and its world-coordinate
// bounds (src/WclQuery.Core/Knowledge/maps.json). Run once per raid zone:
//
//   dotnet run scripts/fetch-map.cs <uiMapId> [<uiMapId> ...]
//
// The id is the map id Warcraft Logs reports for a fight (fights { maps { id } }), which is the game's UiMap id.
// Data comes from the game's own files as published by https://wago.tools: UiMap (name), UiMapAssignment (bounds),
// UiMapXMapArt + UiMapArtTile (the image, as 256x256 BLP tiles).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/fetch-map.cs <uiMapId> [<uiMapId> ...]");
    return 1;
}

var root = FindRepoRoot();
var mapsJsonPath = Path.Combine(root, "src", "WclQuery.Core", "Knowledge", "maps.json");
var imageDir = Path.Combine(root, "web", "maps");
Directory.CreateDirectory(imageDir);

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("WclQuery-map-fetch/1.0");

var uiMap = await Table("UiMap");
var assignments = await Table("UiMapAssignment");
var mapArt = await Table("UiMapXMapArt");
var artTiles = await Table("UiMapArtTile");

var maps = File.Exists(mapsJsonPath)
    ? JsonNode.Parse(File.ReadAllText(mapsJsonPath))!.AsObject()
    : new JsonObject();

foreach (var arg in args)
{
    var id = int.Parse(arg, CultureInfo.InvariantCulture);
    var name = uiMap.FirstOrDefault(r => r["ID"] == arg)?["Name_lang"] ?? $"Map {id}";

    // The whole-map assignment (UI 0..1) gives the world box the image covers. World X runs north, Y runs west.
    var a = assignments
        .Where(r => r["UiMapID"] == arg && D(r["UiMin_0"]) == 0 && D(r["UiMin_1"]) == 0 && D(r["UiMax_0"]) == 1 && D(r["UiMax_1"]) == 1)
        .OrderBy(r => int.Parse(r["OrderIndex"], CultureInfo.InvariantCulture))
        .FirstOrDefault() ?? throw new Exception($"No UiMapAssignment for map {id}.");

    var artId = mapArt.FirstOrDefault(r => r["UiMapID"] == arg)?["UiMapArtID"] ?? throw new Exception($"No map art for map {id}.");
    var tiles = artTiles.Where(r => r["UiMapArtID"] == artId && r["LayerIndex"] == "0").ToList();
    if (tiles.Count == 0) throw new Exception($"No tiles for map art {artId}.");

    var rows = tiles.Max(t => int.Parse(t["RowIndex"])) + 1;
    var cols = tiles.Max(t => int.Parse(t["ColIndex"])) + 1;
    using var canvas = new Image<Rgba32>(cols * 256, rows * 256);
    foreach (var t in tiles)
    {
        var bytes = await http.GetByteArrayAsync($"https://wago.tools/api/casc/{t["FileDataID"]}?download");
        var (w, h, pixels) = Blp.Decode(bytes);
        using var tile = Image.LoadPixelData<Rgba32>(pixels, w, h);
        var x = int.Parse(t["ColIndex"]) * 256;
        var y = int.Parse(t["RowIndex"]) * 256;
        canvas.Mutate(c => c.DrawImage(tile, new Point(x, y), 1f));
    }

    // The in-game world map shows the top-left 1002x668 of the 1024x768 tile grid; UI coordinates 0..1 span that.
    var width = Math.Min(1002, canvas.Width);
    var height = Math.Min(668, canvas.Height);
    canvas.Mutate(c => c.Crop(width, height));
    var imagePath = Path.Combine(imageDir, $"{id}.png");
    await canvas.SaveAsPngAsync(imagePath);

    maps[arg] = new JsonObject
    {
        ["name"] = name,
        ["minX"] = D(a["Region_0"]), ["minY"] = D(a["Region_1"]),
        ["maxX"] = D(a["Region_3"]), ["maxY"] = D(a["Region_4"]),
        ["width"] = width, ["height"] = height,
    };
    Console.WriteLine($"{id} {name}: {width}x{height} -> {Path.GetRelativePath(root, imagePath)}");
}

var sorted = new JsonObject();
foreach (var kv in maps.OrderBy(kv => int.Parse(kv.Key))) sorted[kv.Key] = kv.Value?.DeepClone();
File.WriteAllText(mapsJsonPath, sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Updated {Path.GetRelativePath(root, mapsJsonPath)}");
return 0;

static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

async Task<List<Dictionary<string, string>>> Table(string name)
{
    var csv = await http.GetStringAsync($"https://wago.tools/db2/{name}/csv");
    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var header = SplitCsv(lines[0]);
    return [.. lines.Skip(1).Select(l =>
    {
        var cells = SplitCsv(l);
        var row = new Dictionary<string, string>();
        for (var i = 0; i < header.Count && i < cells.Count; i++) row[header[i]] = cells[i];
        return row;
    })];
}

static List<string> SplitCsv(string line)
{
    var cells = new List<string>();
    var sb = new System.Text.StringBuilder();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (quoted)
        {
            if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
            else if (ch == '"') quoted = false;
            else sb.Append(ch);
        }
        else if (ch == '"') quoted = true;
        else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
        else if (ch != '\r') sb.Append(ch);
    }
    cells.Add(sb.ToString());
    return cells;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
    throw new Exception("Run this from inside the repository.");
}

/// <summary>Minimal BLP2 decoder (first mip only): palettized, DXT1/3/5 and uncompressed BGRA.</summary>
static class Blp
{
    public static (int W, int H, byte[] Rgba) Decode(byte[] d)
    {
        if (d.Length < 148 || d[0] != 'B' || d[1] != 'L' || d[2] != 'P' || d[3] != '2') throw new Exception("Not a BLP2 file.");
        int compression = d[8], alphaDepth = d[9], alphaType = d[10];
        var w = BitConverter.ToInt32(d, 12);
        var h = BitConverter.ToInt32(d, 16);
        var offset = BitConverter.ToInt32(d, 20);
        var output = new byte[w * h * 4];
        switch (compression)
        {
            case 1: Palette(d, offset, w, h, alphaDepth, output); break;
            case 2 when alphaType == 0: Dxt(d, offset, w, h, output, 1); break;
            case 2 when alphaType == 1: Dxt(d, offset, w, h, output, 3); break;
            case 2: Dxt(d, offset, w, h, output, 5); break;
            case 3:
                for (var i = 0; i < w * h; i++)
                {
                    output[i * 4] = d[offset + i * 4 + 2];
                    output[i * 4 + 1] = d[offset + i * 4 + 1];
                    output[i * 4 + 2] = d[offset + i * 4];
                    output[i * 4 + 3] = d[offset + i * 4 + 3];
                }
                break;
            default: throw new Exception($"Unsupported BLP compression {compression}.");
        }
        return (w, h, output);
    }

    private static void Palette(byte[] d, int offset, int w, int h, int alphaDepth, byte[] o)
    {
        const int paletteStart = 148;
        var n = w * h;
        for (var i = 0; i < n; i++)
        {
            var p = paletteStart + d[offset + i] * 4;
            o[i * 4] = d[p + 2]; o[i * 4 + 1] = d[p + 1]; o[i * 4 + 2] = d[p];
            o[i * 4 + 3] = alphaDepth switch
            {
                8 => d[offset + n + i],
                1 => (byte)(((d[offset + n + i / 8] >> (i % 8)) & 1) * 255),
                4 => (byte)(((d[offset + n + i / 2] >> (i % 2 * 4)) & 0xF) * 17),
                _ => 255,
            };
        }
    }

    private static void Dxt(byte[] d, int offset, int w, int h, byte[] o, int format)
    {
        var blockSize = format == 1 ? 8 : 16;
        var pos = offset;
        Span<byte> colours = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[16];
        for (var by = 0; by < (h + 3) / 4; by++)
        for (var bx = 0; bx < (w + 3) / 4; bx++, pos += blockSize)
        {
            var colourPos = format == 1 ? pos : pos + 8;
            alphas.Fill(255);
            if (format == 3)
                for (var i = 0; i < 16; i++) alphas[i] = (byte)(((d[pos + i / 2] >> (i % 2 * 4)) & 0xF) * 17);
            else if (format == 5)
            {
                int a0 = d[pos], a1 = d[pos + 1];
                ulong bits = 0;
                for (var i = 0; i < 6; i++) bits |= (ulong)d[pos + 2 + i] << (8 * i);
                for (var i = 0; i < 16; i++)
                {
                    var code = (int)((bits >> (3 * i)) & 7);
                    alphas[i] = (byte)(code switch
                    {
                        0 => a0,
                        1 => a1,
                        _ when a0 > a1 => ((8 - code) * a0 + (code - 1) * a1) / 7,
                        6 => 0,
                        7 => 255,
                        _ => ((6 - code) * a0 + (code - 1) * a1) / 5,
                    });
                }
            }

            int c0 = d[colourPos] | d[colourPos + 1] << 8, c1 = d[colourPos + 2] | d[colourPos + 3] << 8;
            Span<int> r = stackalloc int[4], g = stackalloc int[4], b = stackalloc int[4], a = stackalloc int[4];
            (r[0], g[0], b[0]) = Rgb565(c0);
            (r[1], g[1], b[1]) = Rgb565(c1);
            a[0] = a[1] = a[2] = a[3] = 255;
            if (c0 > c1 || format != 1)
            {
                r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
                r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
            }
            else
            {
                r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
                r[3] = g[3] = b[3] = 0; a[3] = 0;
            }
            var indices = (uint)(d[colourPos + 4] | d[colourPos + 5] << 8 | d[colourPos + 6] << 16 | d[colourPos + 7] << 24);
            for (var i = 0; i < 16; i++)
            {
                var px = bx * 4 + i % 4;
                var py = by * 4 + i / 4;
                if (px >= w || py >= h) continue;
                var idx = (int)((indices >> (2 * i)) & 3);
                var o4 = (py * w + px) * 4;
                o[o4] = (byte)r[idx]; o[o4 + 1] = (byte)g[idx]; o[o4 + 2] = (byte)b[idx];
                o[o4 + 3] = format == 1 ? (byte)a[idx] : alphas[i];
            }
        }
    }

    private static (int R, int G, int B) Rgb565(int c) =>
        (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);
}
