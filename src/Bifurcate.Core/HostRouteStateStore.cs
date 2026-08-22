using System.Globalization;
using System.Text.Json;

namespace Bifurcate.Core;

/// <summary>
/// Per-user ledger of host /32s last written to the VPN profile. The tray owns this file because
/// the profile lives in the signed-in user's phonebook.
/// </summary>
public sealed class HostRouteStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public HostRouteStateStore(string? path = null) =>
        _path = path ?? BifurcateInfo.HostRouteStatePath;

    public IReadOnlyDictionary<string, HostRouteEntry[]> Load(DateTimeOffset? utcNow = null)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, HostRouteEntry[]>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
            return Read(document.RootElement, utcNow ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Dictionary<string, HostRouteEntry[]>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, HostRouteEntry[]> state)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        Dictionary<string, HostRouteEntry[]> payload = new(state, StringComparer.OrdinalIgnoreCase);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, Options));
        File.Move(temp, _path, overwrite: true);
    }

    public IReadOnlySet<string> ManagedPrefixes()
    {
        HashSet<string> prefixes = new(StringComparer.OrdinalIgnoreCase);
        foreach (HostRouteEntry[] entries in Load().Values)
        {
            foreach (HostRouteEntry entry in entries) { prefixes.Add(entry.Prefix); }
        }

        return prefixes;
    }

    private static Dictionary<string, HostRouteEntry[]> Read(JsonElement root, DateTimeOffset utcNow)
    {
        Dictionary<string, HostRouteEntry[]> ledger = new(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) { return ledger; }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) { continue; }

            List<HostRouteEntry> entries = [];
            foreach (JsonElement item in property.Value.EnumerateArray())
            {
                HostRouteEntry? entry = ReadEntry(item, utcNow);
                if (entry is not null) { entries.Add(entry); }
            }

            if (entries.Count > 0) { ledger[property.Name] = [.. entries]; }
        }

        return ledger;
    }

    private static HostRouteEntry? ReadEntry(JsonElement item, DateTimeOffset utcNow)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            string prefix = item.GetString() ?? "";
            return prefix.Length == 0 ? null : new HostRouteEntry { Prefix = prefix, LastSeenUtc = utcNow };
        }

        if (item.ValueKind != JsonValueKind.Object) { return null; }

        string text = TryProperty(item, "prefix", out JsonElement prefixElement)
            ? prefixElement.GetString() ?? ""
            : "";
        if (text.Length == 0) { return null; }

        DateTimeOffset seen = utcNow;
        if (TryProperty(item, "lastSeenUtc", out JsonElement seenElement)
            && seenElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                seenElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            seen = parsed;
        }

        return new HostRouteEntry { Prefix = text, LastSeenUtc = seen };
    }

    private static bool TryProperty(JsonElement item, string name, out JsonElement value)
    {
        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
