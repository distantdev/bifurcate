using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifurcate.Core;

public sealed record ConfigLoadResult
{
    public BifurcateConfig? Config { get; init; }

    /// <summary>True when the file simply is not there yet, which means first run.</summary>
    public bool Missing { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Ok => Config is not null && Errors.Count == 0;
}

/// <summary>Reads, writes, and watches the machine-wide config file.</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ConfigLoadResult Load(string? path = null)
    {
        path ??= BifurcateInfo.ConfigPath;

        if (!File.Exists(path))
        {
            return new ConfigLoadResult { Missing = true, Errors = [$"No config file at {path}."] };
        }

        BifurcateConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<BifurcateConfig>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            return new ConfigLoadResult { Errors = [$"{path} is not valid JSON: {ex.Message}"] };
        }
        catch (IOException ex)
        {
            return new ConfigLoadResult { Errors = [$"Could not read {path}: {ex.Message}"] };
        }

        if (config is null)
        {
            return new ConfigLoadResult { Errors = [$"{path} is empty."] };
        }

        return new ConfigLoadResult { Config = config, Errors = config.Validate() };
    }

    /// <summary>
    /// Writes the config, creating the directory if needed. Requires administrator rights, which is
    /// deliberate: the service acts on these values, so a standard user must not be able to point
    /// hardening at a different adapter.
    /// </summary>
    public static void Save(BifurcateConfig config, string? path = null)
    {
        path ??= BifurcateInfo.ConfigPath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        // Write to a sibling then move, so a crash mid-write cannot leave a truncated config.
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(config));
        File.Move(temp, path, overwrite: true);
    }

    public static string Serialize(BifurcateConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>
    /// Value comparison for change detection. Record equality is not usable here: the config holds
    /// arrays, and those compare by reference, so two identical configs would look different.
    /// </summary>
    public static bool SameAs(BifurcateConfig? left, BifurcateConfig? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => Serialize(left) == Serialize(right),
        };

    public static BifurcateConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<BifurcateConfig>(json, Options);

    /// <summary>
    /// Watches the config file and invokes the callback after changes settle. Editors and the
    /// atomic save above both produce bursts of events, hence the debounce.
    /// </summary>
    public static IDisposable Watch(Action onChanged, string? path = null)
    {
        path ??= BifurcateInfo.ConfigPath;
        string directory = Path.GetDirectoryName(path) ?? BifurcateInfo.DataDirectory;
        Directory.CreateDirectory(directory);

        return new ConfigWatcher(directory, Path.GetFileName(path), onChanged);
    }

    private sealed class ConfigWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly Action _onChanged;

        public ConfigWatcher(string directory, string fileName, Action onChanged)
        {
            _onChanged = onChanged;
            _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
        }

        private void Schedule() => _debounce.Change(500, Timeout.Infinite);

        private void Fire()
        {
            try { _onChanged(); }
            catch { /* a bad reload must not take the watcher down */ }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _debounce.Dispose();
        }
    }
}
