using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class LocalWorkflowImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalWorkflowImportResult Import(string sourcePath, string projectFolder)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("待导入的 workflow JSON 不存在。", sourcePath);
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(sourcePath));
        if (node is not JsonObject workflow)
        {
            throw new InvalidOperationException("workflow JSON 根节点必须是对象。");
        }

        string? type = ReadString(workflow, "_type");
        if (!string.Equals(type, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("JSON 的 _type 必须为 workflow。");
        }

        string? xaml = ReadString(workflow, "Xaml") ?? ReadString(workflow, "xaml");
        if (string.IsNullOrWhiteSpace(xaml))
        {
            throw new InvalidOperationException("workflow JSON 缺少可执行的 Xaml 字段。");
        }

        string workflowName = ReadString(workflow, "name")?.Trim() ?? Path.GetFileNameWithoutExtension(sourcePath);
        string projectAndName = ReadString(workflow, "projectandname")?.Trim() ?? workflowName;
        string projectName = ParseProjectName(projectAndName, projectFolder);
        string workflowId = ReadString(workflow, "_id")?.Trim() ?? Guid.NewGuid().ToString("N");
        string projectId = ReadString(workflow, "projectid")?.Trim() ?? "local-" + StableProjectId(projectName);
        string workflowFilename = ReadString(workflow, "Filename")?.Trim()
            ?? ReadString(workflow, "filename")?.Trim()
            ?? SanitizeFilename(workflowName) + ".xaml";

        workflow["_id"] = workflowId;
        workflow["projectid"] = projectId;
        workflow["name"] = workflowName;
        workflow["Filename"] = workflowFilename;
        workflow["projectandname"] = projectName + "/" + workflowName;

        Directory.CreateDirectory(projectFolder);
        string targetPath = ResolveTargetPath(sourcePath, projectFolder, workflowId);
        string temporaryPath = Path.Combine(projectFolder, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, workflow.ToJsonString(JsonOptions));
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new LocalWorkflowImportResult
        {
            WorkflowId = workflowId,
            WorkflowName = workflowName,
            ProjectName = projectName,
            TargetPath = targetPath
        };
    }

    private static string ResolveTargetPath(string sourcePath, string projectFolder, string workflowId)
    {
        string preferred = Path.Combine(projectFolder, Path.GetFileName(sourcePath));
        if (!File.Exists(preferred) || PathsEqual(preferred, sourcePath)) return preferred;

        try
        {
            using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(preferred));
            if (existing.RootElement.TryGetProperty("_id", out JsonElement id) &&
                string.Equals(id.GetString(), workflowId, StringComparison.OrdinalIgnoreCase))
            {
                return preferred;
            }
        }
        catch (JsonException)
        {
            // Keep the existing unrelated file and choose another name.
        }

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(projectFolder, $"{stem} ({index}).json");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string ParseProjectName(string projectAndName, string projectFolder)
    {
        string normalized = projectAndName.Replace('\\', '/');
        int separator = normalized.IndexOf('/');
        string value = separator > 0 ? normalized[..separator].Trim() : string.Empty;
        return string.IsNullOrWhiteSpace(value)
            ? new DirectoryInfo(projectFolder).Name
            : value;
    }

    private static string StableProjectId(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in value.ToUpperInvariant())
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    private static string SanitizeFilename(string value)
    {
        string result = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "workflow" : result;
    }

    private static string? ReadString(JsonObject value, string propertyName)
    {
        return value[propertyName]?.GetValue<string>();
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LocalWorkflowImportResult
{
    public required string WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string ProjectName { get; init; }
    public required string TargetPath { get; init; }
}
