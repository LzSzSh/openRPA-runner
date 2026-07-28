using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRpaWorkflowLauncher.Services;

/// <summary>
/// Imports an OpenRPA project export without using OpenRPA's LiteDB storage.
/// The source layout is preserved so project-relative images, scripts, and
/// future child-workflow references remain available to Maxwell.
/// </summary>
public sealed class LocalProjectImportService
{
    private const string ManifestFilename = "maxwell-project.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalProjectImportResult Import(string projectFile, string localProjectsRoot)
    {
        if (!File.Exists(projectFile))
        {
            throw new FileNotFoundException("待导入的 OpenRPA Project 文件不存在。", projectFile);
        }
        if (!string.Equals(Path.GetExtension(projectFile), ".rpaproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("项目导入只接受 .rpaproj 文件。");
        }

        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(projectFile));
        if (parsed is not JsonObject project)
        {
            throw new InvalidOperationException(".rpaproj 根节点必须是 JSON 对象。");
        }

        string? type = ReadString(project, "_type");
        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "project", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(".rpaproj 的 _type 必须为 project。");
        }

        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        string projectName = ReadString(project, "name")?.Trim() ?? Path.GetFileNameWithoutExtension(projectFile);
        string projectId = ReadString(project, "_id")?.Trim() ?? "local-" + StableId(projectName);

        Directory.CreateDirectory(localProjectsRoot);
        string destinationDirectory = Path.Combine(localProjectsRoot, SanitizeDirectoryName(projectName));
        EnsureDestinationIsOutsideSource(sourceDirectory, destinationDirectory);
        PruneStaleProjectDocuments(sourceDirectory, destinationDirectory);
        CopyProjectFiles(sourceDirectory, destinationDirectory);

        List<LocalProjectWorkflow> workflows = ReadWorkflows(destinationDirectory);
        Dictionary<string, string> dependencies = ReadDependencies(project);
        LocalProjectManifest manifest = new()
        {
            SchemaVersion = 1,
            ProjectName = projectName,
            ProjectId = projectId,
            SourceProjectFilename = Path.GetFileName(projectFile),
            ImportedAtUtc = DateTime.UtcNow,
            Dependencies = dependencies,
            Workflows = workflows
        };
        WriteManifest(destinationDirectory, manifest);

        return new LocalProjectImportResult
        {
            ProjectName = projectName,
            ProjectId = projectId,
            TargetDirectory = destinationDirectory,
            WorkflowCount = workflows.Count,
            DependencyCount = dependencies.Count
        };
    }

    private static void CopyProjectFiles(string sourceDirectory, string destinationDirectory)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            if (string.Equals(relativePath, ManifestFilename, StringComparison.OrdinalIgnoreCase)) continue;

            string targetFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private static void PruneStaleProjectDocuments(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory)) return;

        HashSet<string> sourceDocuments = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(IsOpenRpaProjectDocument)
            .Select(file => Path.GetRelativePath(sourceDirectory, file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string destinationFile in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(destinationFile).Equals(ManifestFilename, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsOpenRpaProjectDocument(destinationFile)) continue;

            string relativePath = Path.GetRelativePath(destinationDirectory, destinationFile);
            if (!sourceDocuments.Contains(relativePath))
            {
                File.Delete(destinationFile);
            }
        }
    }

    private static bool IsOpenRpaProjectDocument(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".rpaproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(path));
            string? type = parsed is JsonObject value ? ReadString(value, "_type") : null;
            return string.Equals(type, "workflow", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "project", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<LocalProjectWorkflow> ReadWorkflows(string projectDirectory)
    {
        List<LocalProjectWorkflow> workflows = [];
        foreach (string jsonFile in Directory.EnumerateFiles(projectDirectory, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(jsonFile).Equals(ManifestFilename, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                JsonNode? parsed = JsonNode.Parse(File.ReadAllText(jsonFile));
                if (parsed is not JsonObject workflow ||
                    !string.Equals(ReadString(workflow, "_type"), "workflow", StringComparison.OrdinalIgnoreCase)) continue;

                string? name = ReadString(workflow, "name");
                string? id = ReadString(workflow, "_id");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

                workflows.Add(new LocalProjectWorkflow
                {
                    Id = id,
                    Name = name,
                    ProjectAndName = ReadString(workflow, "projectandname") ?? name,
                    Filename = ReadString(workflow, "Filename") ?? ReadString(workflow, "filename") ?? Path.GetFileNameWithoutExtension(jsonFile) + ".xaml",
                    RelativeJsonPath = Path.GetRelativePath(projectDirectory, jsonFile)
                });
            }
            catch (JsonException)
            {
                // WorkflowScanner will report malformed JSON in the visible warning list.
            }
        }
        return workflows.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadDependencies(JsonObject project)
    {
        Dictionary<string, string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        if (project["dependencies"] is not JsonObject values) return dependencies;
        foreach ((string package, JsonNode? version) in values)
        {
            string? value = version?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(value)) dependencies[package] = value;
        }
        return dependencies;
    }

    private static void WriteManifest(string directory, LocalProjectManifest manifest)
    {
        string target = Path.Combine(directory, ManifestFilename);
        string temporary = Path.Combine(directory, $".{ManifestFilename}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ReadString(JsonObject value, string propertyName) => value[propertyName]?.GetValue<string>();

    private static void EnsureDestinationIsOutsideSource(string sourceDirectory, string destinationDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本地项目目录不能位于待导入 OpenRPA Project 的目录内。请选择另一个 Maxwell 本地项目文件夹。");
        }
    }

    private static string SanitizeDirectoryName(string value)
    {
        string result = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(result) ? "OpenRPA-Project" : result;
    }

    private static string StableId(string value)
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
}

public sealed class LocalProjectImportResult
{
    public required string ProjectName { get; init; }
    public required string ProjectId { get; init; }
    public required string TargetDirectory { get; init; }
    public required int WorkflowCount { get; init; }
    public required int DependencyCount { get; init; }
}

public sealed class LocalProjectManifest
{
    public required int SchemaVersion { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectId { get; init; }
    public required string SourceProjectFilename { get; init; }
    public required DateTime ImportedAtUtc { get; init; }
    public required Dictionary<string, string> Dependencies { get; init; }
    public required List<LocalProjectWorkflow> Workflows { get; init; }
}

public sealed class LocalProjectWorkflow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ProjectAndName { get; init; }
    public required string Filename { get; init; }
    public required string RelativeJsonPath { get; init; }
}
