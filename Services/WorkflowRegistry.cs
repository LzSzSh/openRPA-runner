using System.IO;
using System.Text.Json;

namespace OpenRpaWorkflowLauncher.Services;

/// <summary>
/// Read-only index for workflows stored in a Maxwell project directory.
/// It validates child-workflow references before RuntimeHost execution.
/// </summary>
public sealed class WorkflowRegistry
{
    private readonly Dictionary<string, WorkflowRegistryEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkflowRegistryEntry> _byProjectAndName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkflowRegistryEntry> _byProjectAndFilename = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings;

    public IReadOnlyCollection<WorkflowRegistryEntry> Workflows => _byId.Values;
    public IReadOnlyList<string> Warnings => _warnings;

    private WorkflowRegistry(List<string> warnings)
    {
        _warnings = warnings;
    }

    public static WorkflowRegistry Create(string projectRoot)
    {
        List<string> warnings = [];
        WorkflowRegistry registry = new(warnings);
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            warnings.Add("项目目录不存在，无法建立 workflow registry。");
            return registry;
        }

        foreach (string jsonFile in Directory.EnumerateFiles(projectRoot, "*.json", SearchOption.AllDirectories))
        {
            registry.TryAdd(jsonFile);
        }
        return registry;
    }

    public bool TryResolve(string reference, out WorkflowRegistryEntry? workflow)
    {
        workflow = null;
        if (string.IsNullOrWhiteSpace(reference)) return false;

        string value = reference.Trim();
        if (_byId.TryGetValue(value, out workflow)) return true;

        string projectAndName = NormalizeProjectAndName(value);
        if (_byProjectAndName.TryGetValue(projectAndName, out workflow)) return true;
        return _byProjectAndFilename.TryGetValue(projectAndName, out workflow);
    }

    private void TryAdd(string jsonFile)
    {
        try
        {
            using FileStream stream = File.OpenRead(jsonFile);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (!string.Equals(ReadString(root, "_type"), "workflow", StringComparison.OrdinalIgnoreCase)) return;

            string? id = ReadString(root, "_id");
            string? name = ReadString(root, "name");
            string? projectAndName = ReadString(root, "projectandname");
            string? filename = ReadString(root, "Filename") ?? ReadString(root, "filename");
            string? projectId = ReadString(root, "projectid");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(projectAndName) || string.IsNullOrWhiteSpace(filename)) return;

            (string projectName, _) = SplitProjectAndName(projectAndName);
            if (string.IsNullOrWhiteSpace(projectName)) return;

            WorkflowRegistryEntry entry = new()
            {
                Id = id,
                Name = name,
                ProjectId = projectId ?? string.Empty,
                ProjectName = projectName,
                ProjectAndName = NormalizeProjectAndName(projectAndName),
                Filename = filename,
                SourceFile = jsonFile
            };
            AddUnique(_byId, entry.Id, entry, "_id");
            AddUnique(_byProjectAndName, entry.ProjectAndName, entry, "projectandname");
            AddUnique(_byProjectAndFilename, NormalizeProjectAndName(entry.ProjectName + "\\" + entry.Filename), entry, "Project\\Filename");
        }
        catch (JsonException)
        {
            // WorkflowScanner reports malformed files to the UI; registry ignores them.
        }
        catch (Exception ex)
        {
            _warnings.Add($"无法登记 workflow {Path.GetFileName(jsonFile)}：{ex.Message}");
        }
    }

    private void AddUnique(Dictionary<string, WorkflowRegistryEntry> index, string key, WorkflowRegistryEntry entry, string kind)
    {
        if (index.TryGetValue(key, out WorkflowRegistryEntry? existing) &&
            !string.Equals(existing.SourceFile, entry.SourceFile, StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"workflow {kind} 重复：{key}（保留 {existing.SourceFile}，忽略 {entry.SourceFile}）。");
            return;
        }
        index[key] = entry;
    }

    private static string NormalizeProjectAndName(string value) => value.Replace('/', '\\').Trim();

    private static (string ProjectName, string WorkflowName) SplitProjectAndName(string value)
    {
        string normalized = NormalizeProjectAndName(value);
        int separator = normalized.IndexOf('\\');
        return separator < 0
            ? (string.Empty, normalized)
            : (normalized[..separator].Trim(), normalized[(separator + 1)..].Trim());
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed class WorkflowRegistryEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectAndName { get; init; }
    public required string Filename { get; init; }
    public required string SourceFile { get; init; }
}
