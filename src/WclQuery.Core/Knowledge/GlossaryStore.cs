using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WclQuery.Core.Knowledge;

public sealed class GlossaryOptions
{
    public const string SectionName = "Glossary";

    /// <summary>
    /// Where the editable glossary lives. Leave empty for the default: on Azure App Service,
    /// %HOME%\data\wclquery (persistent and untouched by deployments); locally, App_Data next to the app.
    /// </summary>
    public string Directory { get; set; } = "";
}

public sealed record Glossary(string Text, string? UpdatedBy, DateTimeOffset? UpdatedAt);

/// <summary>
/// The guild glossary officers edit from the site. Stored as a Markdown file on disk, with each
/// previous version kept in a history folder. Seeded from the built-in Knowledge/glossary.md.
/// </summary>
public sealed class GlossaryStore
{
    public const int MaxLength = 30_000;

    private readonly string _directory;
    private readonly ILogger<GlossaryStore> _logger;
    private readonly Lock _lock = new();
    private Glossary? _current;

    public GlossaryStore(IOptions<GlossaryOptions> options, ILogger<GlossaryStore> logger)
    {
        _logger = logger;
        _directory = !string.IsNullOrWhiteSpace(options.Value.Directory)
            ? options.Value.Directory
            : Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null
              && Environment.GetEnvironmentVariable("HOME") is string home
                ? Path.Combine(home, "data", "wclquery")
                : Path.Combine(AppContext.BaseDirectory, "App_Data");
    }

    private string TextPath => Path.Combine(_directory, "glossary.md");
    private string MetaPath => Path.Combine(_directory, "glossary.meta.json");
    private string HistoryDirectory => Path.Combine(_directory, "glossary-history");

    public Glossary Get()
    {
        lock (_lock)
        {
            return _current ??= Load();
        }
    }

    public Glossary Save(string text, string updatedBy)
    {
        text = text.Replace("\r\n", "\n").Trim() + "\n";
        lock (_lock)
        {
            Directory.CreateDirectory(HistoryDirectory);
            if (File.Exists(TextPath))
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(TextPath, Path.Combine(HistoryDirectory, $"glossary-{stamp}.md"), overwrite: true);
            }

            var glossary = new Glossary(text, updatedBy, DateTimeOffset.UtcNow);
            File.WriteAllText(TextPath, text);
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta(updatedBy, glossary.UpdatedAt)));
            _logger.LogInformation("Glossary updated by {User} ({Length} chars)", updatedBy, text.Length);
            return _current = glossary;
        }
    }

    private Glossary Load()
    {
        try
        {
            if (File.Exists(TextPath))
            {
                var meta = File.Exists(MetaPath) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) : null;
                return new Glossary(File.ReadAllText(TextPath), meta?.UpdatedBy, meta?.UpdatedAt);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Couldn't read the glossary from {Directory}; using the built-in one", _directory);
        }
        return new Glossary(LoadBuiltIn(), null, null);
    }

    private static string LoadBuiltIn()
    {
        using var stream = typeof(GlossaryStore).Assembly.GetManifestResourceStream("WclQuery.Glossary");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Meta(string? UpdatedBy, DateTimeOffset? UpdatedAt);
}
