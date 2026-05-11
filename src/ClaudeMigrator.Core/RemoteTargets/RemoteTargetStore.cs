using System.Text;
using System.Text.Json;
using ClaudeMigrator.Core.Utilities;

namespace ClaudeMigrator.Core.RemoteTargets;

public sealed class RemoteTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public RemoteTargetStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public IReadOnlyList<RemoteMachineSpec> Load()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(Path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            IEnumerable<JsonElement> rawItems = [];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("machines", out var machines) && machines.ValueKind == JsonValueKind.Array)
                {
                    rawItems = machines.EnumerateArray();
                }
                else if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
                {
                    rawItems = targets.EnumerateArray();
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                rawItems = root.EnumerateArray();
            }

            return Deduplicate(rawItems.Select(item => JsonSerializer.Deserialize<RemoteMachineSpec>(item.GetRawText(), JsonOptions))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList());
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<RemoteMachineSpec> Save(IEnumerable<RemoteMachineSpec> specs)
    {
        var normalized = Deduplicate(specs);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
        var payload = new
        {
            version = 1,
            updated_at = PathUtils.TimestampTag(),
            machines = normalized,
        };
        File.WriteAllText(Path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        return normalized;
    }

    public IReadOnlyList<RemoteMachineSpec> Upsert(RemoteMachineSpec spec)
    {
        var specs = Load().ToList();
        var normalized = spec.Normalized();
        var index = specs.FindIndex(existing => string.Equals(existing.MachineId, normalized.MachineId, StringComparison.Ordinal));
        if (index >= 0)
        {
            specs[index] = normalized;
        }
        else
        {
            specs.Add(normalized);
        }

        return Save(specs);
    }

    public IReadOnlyList<RemoteMachineSpec> Remove(string machineId)
    {
        return Save(Load().Where(spec => !string.Equals(spec.MachineId, machineId, StringComparison.Ordinal)).ToList());
    }

    public RemoteMachineSpec? Get(string machineId)
    {
        return Load().FirstOrDefault(spec => string.Equals(spec.MachineId, machineId, StringComparison.Ordinal));
    }

    private static IReadOnlyList<RemoteMachineSpec> Deduplicate(IEnumerable<RemoteMachineSpec> specs)
    {
        var ordered = new List<RemoteMachineSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            var normalized = spec.Normalized();
            var candidateId = normalized.MachineId;
            if (string.IsNullOrWhiteSpace(candidateId))
            {
                continue;
            }

            var baseId = candidateId;
            var suffix = 2;
            while (seen.Contains(candidateId))
            {
                candidateId = $"{baseId}-{suffix}";
                suffix++;
            }

            if (!string.Equals(candidateId, normalized.MachineId, StringComparison.Ordinal))
            {
                normalized = normalized with { MachineId = candidateId };
            }

            seen.Add(candidateId);
            ordered.Add(normalized);
        }

        return ordered;
    }
}
