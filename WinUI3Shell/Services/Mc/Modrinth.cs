using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI3Shell.Services.Mc;

/// <summary>Modrinth 搜索结果（项目）。</summary>
public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("icon_url")] public string IconUrl { get; set; } = "";
}

public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = new();
}

/// <summary>Modrinth 的一个文件。</summary>
public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
}

/// <summary>依赖项：required / optional / incompatible / embedded。</summary>
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}

/// <summary>Modrinth 的一个具体版本（含文件列表）。</summary>
public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("date_published")] public DateTimeOffset? DatePublished { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();

    /// <summary>优先选 primary 的 jar；跳过 sources / dev 之类的附加包。</summary>
    public ModrinthFile? PickJar()
    {
        var jars = Files.Where(f => f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            && !f.FileName.Contains("sources", StringComparison.OrdinalIgnoreCase)
            && !f.FileName.Contains("-dev", StringComparison.OrdinalIgnoreCase)).ToList();
        return jars.FirstOrDefault(f => f.Primary) ?? jars.FirstOrDefault();
    }
}

/// <summary>Modrinth 官方 API 封装（只读搜索、版本、依赖）。</summary>
public static class Modrinth
{
    private const string Base = "https://api.modrinth.com/v2";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0 (github.com/MansonL289)");
        return c;
    }

    /// <summary>全局搜索 Mod；关键词为空时返回热门 Mod（按下载量）；loader 非空时按加载器筛选。</summary>
    public static async Task<List<ModrinthProject>> SearchAsync(string query, string loader = "",
        CancellationToken ct = default)
    {
        string facets = string.IsNullOrWhiteSpace(loader)
            ? "[[\"project_type:mod\"]]"
            : $"[[\"project_type:mod\"],[\"categories:{loader}\"]]";
        string index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        string url = $"{Base}/search?query={Uri.EscapeDataString(query)}&limit=25&index={index}"
            + $"&facets={Uri.EscapeDataString(facets)}";
        string json = await Http.GetStringAsync(url, ct);
        var result = JsonSerializer.Deserialize<ModrinthSearchResponse>(json);
        return result?.Hits ?? new();
    }

    /// <summary>项目的全部可下载版本（不过滤加载器/游戏版本），最新在前。</summary>
    public static async Task<List<ModrinthVersion>> GetAllVersionsAsync(string projectId,
        CancellationToken ct = default)
    {
        string url = $"{Base}/project/{projectId}/version";
        var list = JsonSerializer.Deserialize<List<ModrinthVersion>>(await Http.GetStringAsync(url, ct)) ?? new();
        return list.Where(v => v.PickJar() != null).ToList();
    }

    /// <summary>列出项目在该 MC 版本 + 加载器下可用的全部版本，最新在前。</summary>
    public static async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, string mcVersion,
        string loader, CancellationToken ct = default)
    {
        string url = $"{Base}/project/{projectId}/version"
            + $"?game_versions={Uri.EscapeDataString($"[\"{mcVersion}\"]")}"
            + $"&loaders={Uri.EscapeDataString($"[\"{loader}\"]")}";
        var list = JsonSerializer.Deserialize<List<ModrinthVersion>>(await Http.GetStringAsync(url, ct)) ?? new();
        // API 已过滤，这里再校验一次，避免把不匹配的文件放进实例
        return list.Where(v =>
            v.GameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase)
            && v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)
            && v.PickJar() != null).ToList();
    }

    public static async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        string json = await Http.GetStringAsync($"{Base}/version/{versionId}", ct);
        return JsonSerializer.Deserialize<ModrinthVersion>(json);
    }

    /// <summary>批量查项目名称（用于可选依赖提示）。</summary>
    public static async Task<Dictionary<string, string>> GetProjectTitlesAsync(IReadOnlyList<string> ids,
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return map;
        string url = $"{Base}/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(ids))}";
        var list = JsonSerializer.Deserialize<List<ModrinthProject>>(await Http.GetStringAsync(url, ct)) ?? new();
        foreach (var project in list)
        {
            map[project.ProjectId] = project.Title;
        }
        return map;
    }

    /// <summary>递归解析 required 依赖；同版本同加载器，带循环保护。</summary>
    public static async Task<List<ModrinthVersion>> ResolveRequiredAsync(ModrinthVersion root,
        string mcVersion, string loader, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<ModrinthVersion>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
        var queue = new Queue<ModrinthVersion>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            foreach (var dep in current.Dependencies)
            {
                if (!dep.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase)) continue;

                ModrinthVersion? resolved = null;
                progress?.Report("解析依赖...");
                if (!string.IsNullOrEmpty(dep.VersionId))
                {
                    resolved = await GetVersionAsync(dep.VersionId, ct);
                }
                else if (!string.IsNullOrEmpty(dep.ProjectId))
                {
                    resolved = (await GetVersionsAsync(dep.ProjectId, mcVersion, loader, ct)).FirstOrDefault();
                }
                if (resolved == null || resolved.PickJar() == null) continue;

                // 依赖也必须匹配当前实例
                if (!resolved.GameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase)
                    || !resolved.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!visited.Add(resolved.Id)) continue;
                result.Add(resolved);
                queue.Enqueue(resolved);
            }
        }
        return result;
    }
}
