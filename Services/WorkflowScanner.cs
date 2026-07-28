using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class WorkflowScanner
{
    private static readonly WorkflowCompatibilityScanner CompatibilityScanner = new();
    private static readonly Regex ChildWorkflowReference = new(
        @"<[^>]*InvokeOpenRPA\b[^>]*\b(?:workflow|ProjectAndName)\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public WorkflowScanResult Scan(string projectFolder)
    {
        WorkflowScanResult result = new();

        if (string.IsNullOrWhiteSpace(projectFolder) || !Directory.Exists(projectFolder))
        {
            result.Warnings.Add("Project 文件夹不存在。");
            return result;
        }

        foreach (string jsonFile in Directory.EnumerateFiles(projectFolder, "*.json", SearchOption.AllDirectories))
        {
            TryReadWorkflow(jsonFile, result);
        }

        LocalWorkflowRegistry registry = LocalWorkflowRegistry.Create(projectFolder);
        foreach (string warning in registry.Warnings)
        {
            result.Warnings.Add(warning);
        }
        ValidateChildWorkflowReferences(result, registry);
        return result;
    }

    private static void ValidateChildWorkflowReferences(
        WorkflowScanResult result,
        LocalWorkflowRegistry registry)
    {
        foreach (WorkflowItem workflow in result.Workflows)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(workflow.SourceFile));
                string xaml = ReadString(document.RootElement, "Xaml") ??
                              ReadString(document.RootElement, "xaml") ??
                              string.Empty;
                foreach (Match match in ChildWorkflowReference.Matches(xaml))
                {
                    string reference = match.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("[", StringComparison.Ordinal)) continue;
                    if (!registry.TryResolve(reference, out _))
                    {
                        result.Warnings.Add(
                            $"{workflow.ProjectAndName} 引用的子 workflow 不存在：{reference}");
                    }
                }
            }
            catch (JsonException)
            {
                // The normal workflow reader already reports malformed JSON.
            }
        }
    }

    private static void TryReadWorkflow(string jsonFile, WorkflowScanResult result)
    {
        try
        {
            using FileStream stream = File.OpenRead(jsonFile);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            string? type = ReadString(root, "_type");
            if (!string.Equals(type, "workflow", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? projectAndName = ReadString(root, "projectandname");
            string? name = ReadString(root, "name");
            string? filename = ReadString(root, "Filename") ?? ReadString(root, "filename");
            string? id = ReadString(root, "_id");
            string? projectId = ReadString(root, "projectid");
            string? xaml = ReadString(root, "Xaml") ?? ReadString(root, "xaml");

            List<string> missing = [];
            AddMissing(missing, "projectandname", projectAndName);
            AddMissing(missing, "name", name);
            AddMissing(missing, "Filename", filename);
            AddMissing(missing, "_id", id);
            AddMissing(missing, "projectid", projectId);

            if (missing.Count > 0)
            {
                result.Warnings.Add($"{Path.GetFileName(jsonFile)} 字段缺失：{string.Join(", ", missing)}");
                return;
            }

            WorkflowCompatibility compatibility = CompatibilityScanner.Analyze(xaml ?? string.Empty);
            if (!compatibility.CanRun || compatibility.Status is "需要外部环境" or "未验证" or "高风险")
            {
                result.Warnings.Add($"{Path.GetFileName(jsonFile)} 兼容性：{compatibility.Status}{Environment.NewLine}{compatibility.Details}");
            }

            (string projectName, string workflowName) = ParseProjectAndName(projectAndName!);
            if (string.IsNullOrWhiteSpace(workflowName))
            {
                workflowName = name!;
            }

            result.Workflows.Add(new WorkflowItem
            {
                ProjectName = projectName,
                WorkflowName = workflowName,
                ProjectAndName = projectAndName!,
                Name = name!,
                Filename = filename!,
                Id = id!,
                ProjectId = projectId!,
                SourceFile = jsonFile,
                Compatibility = compatibility
            });
        }
        catch (JsonException ex)
        {
            result.Warnings.Add($"{Path.GetFileName(jsonFile)} JSON 解析失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"{Path.GetFileName(jsonFile)} 读取失败：{ex.Message}");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => null
        };
    }

    private static void AddMissing(List<string> missing, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(fieldName);
        }
    }

    private static (string ProjectName, string WorkflowName) ParseProjectAndName(string projectAndName)
    {
        string normalized = projectAndName.Replace('/', '\\');
        int separatorIndex = normalized.IndexOf('\\');

        if (separatorIndex < 0)
        {
            return (normalized.Trim(), string.Empty);
        }

        string projectName = normalized[..separatorIndex].Trim();
        string workflowName = normalized[(separatorIndex + 1)..].Trim();
        return (projectName, workflowName);
    }
}
