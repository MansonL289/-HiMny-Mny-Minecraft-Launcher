using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinUI3Shell.Services.Mc;

/// <summary>CurseForge 搜索结果（Mod 项目）。</summary>
public sealed class CfMod
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("downloadCount")] public long Downloads { get; set; }
    [JsonPropertyName("authors")] public List<CfAuthor> Authors { get; set; } = new();
    [JsonPropertyName("logo")] public CfModLogo? Logo { get; set; }

    /// <summary>作者名（多个用顿号连接）。</summary>
    [JsonIgnore]
    public string Author => string.Join("、",
        (Authors ?? new List<CfAuthor>()).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)));

    /// <summary>图标地址；没有则为空串。</summary>
    [JsonIgnore] public string IconUrl => Logo?.ThumbnailUrl ?? "";
}

public sealed class CfAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class CfModLogo
{
    [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>CurseForge 的一个文件（mod 文件）。</summary>
public sealed class CfFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("fileLength")] public long FileLength { get; set; }
    [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; } = new();
    /// <summary>原始下载地址；作者禁止第三方分发时为 null。</summary>
    [JsonPropertyName("downloadUrl")] public string? DownloadUrlRaw { get; set; }

    /// <summary>下载地址；作者禁止第三方分发时为空串（调用方需提示手动下载）。</summary>
    [JsonIgnore]
    public string DownloadUrl
    {
        get => DownloadUrlRaw ?? "";
        set => DownloadUrlRaw = value;
    }

    /// <summary>适配的游戏版本列表（逗号分隔显示用）。</summary>
    [JsonIgnore] public string GameVersionsText => GameVersions == null ? "" : string.Join(", ", GameVersions);
}

/// <summary>依赖项（只取必需前置）。</summary>
public sealed class CfDependency
{
    public int ModId { get; set; }
    public int FileId { get; set; }
}

/// <summary>CurseForge API v1 客户端（URL 固定 api.curseforge.com，Key 来自设置）。</summary>
public static class CurseForge
{
    private const string Base = "https://api.curseforge.com/v1";
    private const int GameIdMinecraft = 432;
    private const int ClassIdMods = 6;
    /// <summary>relationType = 3 表示必需前置。</summary>
    private const int RequiredRelationType = 3;

    /// <summary>是否已在设置里填写 API Key。</summary>
    public static bool HasKey => AppSettings.CurseForgeKey.Length > 0;

    /// <summary>搜索 Mod；loader 传 fabric/forge/quilt/neoforge，mcVersion 传游戏版本。</summary>
    public static async Task<List<CfMod>> SearchAsync(string query, string? loader, string? mcVersion,
        int limit = 20, CancellationToken ct = default)
    {
        int pageSize = Math.Clamp(limit, 1, 50);
        string url = $"/v1/mods/search?gameId={GameIdMinecraft}&classId={ClassIdMods}"
            + $"&sortField=2&sortOrder=desc&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += $"&searchFilter={Uri.EscapeDataString(query.Trim())}";
        }
        int loaderType = LoaderTypeId(loader);
        if (loaderType > 0) url += $"&modLoaderType={loaderType}";
        if (!string.IsNullOrWhiteSpace(mcVersion))
        {
            url += $"&gameVersion={Uri.EscapeDataString(mcVersion.Trim())}";
        }

        var response = Deserialize<CfListResponse<CfMod>>(await GetAsync(url, ct));
        return response?.Data ?? new List<CfMod>();
    }

    /// <summary>某个 Mod 的文件列表（最多 50 个，可按游戏版本/加载器过滤）。</summary>
    public static async Task<List<CfFile>> FilesAsync(int modId, string? mcVersion, string? loader,
        CancellationToken ct = default)
    {
        string url = $"/v1/mods/{modId}/files?pageSize=50";
        int loaderType = LoaderTypeId(loader);
        if (loaderType > 0) url += $"&modLoaderType={loaderType}";
        if (!string.IsNullOrWhiteSpace(mcVersion))
        {
            url += $"&gameVersion={Uri.EscapeDataString(mcVersion.Trim())}";
        }

        var response = Deserialize<CfListResponse<CfFile>>(await GetAsync(url, ct));
        return response?.Data ?? new List<CfFile>();
    }

    /// <summary>直接取单个文件详情（整合包安装用，避免翻页）。</summary>
    public static async Task<CfFile?> FileAsync(int modId, int fileId, CancellationToken ct = default)
    {
        var response = Deserialize<CfDataResponse<CfFile>>(
            await GetAsync($"/v1/mods/{modId}/files/{fileId}", ct));
        return response?.Data;
    }

    /// <summary>某个文件的必需前置（relationType 3），返回其 modId / fileId。</summary>
    public static async Task<List<CfDependency>> DependenciesAsync(int modId, int fileId,
        CancellationToken ct = default)
    {
        string text = await GetAsync($"/v1/mods/{modId}/files/{fileId}", ct);
        var result = new List<CfDependency>();
        JsonNode? dependencies;
        try
        {
            dependencies = JsonNode.Parse(text)?["data"]?["dependencies"];
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("CurseForge 返回的数据无法解析。");
        }
        foreach (var node in dependencies?.AsArray() ?? new JsonArray())
        {
            int relation = node?["relationType"]?.GetValue<int>() ?? 0;
            if (relation != RequiredRelationType) continue;
            int dependencyModId = node?["modId"]?.GetValue<int>() ?? 0;
            if (dependencyModId <= 0) continue;
            result.Add(new CfDependency
            {
                ModId = dependencyModId,
                FileId = node?["fileId"]?.GetValue<int>() ?? 0,
            });
        }
        return result;
    }

    /// <summary>CurseForge loader 名称 → API 的 modLoaderType 数值。</summary>
    private static int LoaderTypeId(string? loader) => loader?.Trim().ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" or "neo-forge" => 6,
        _ => 0,
    };

    /// <summary>统一请求：拼 base、加 x-api-key 与 User-Agent，失败转中文提示。</summary>
    private static async Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        if (!HasKey) throw new InvalidOperationException("未配置 CurseForge API Key");

        string url = Base + pathAndQuery;
        // 不给静态 McHttp.Client 加全局头，避免把 Key 带到其它站点
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", AppSettings.CurseForgeKey.Trim());
        request.Headers.UserAgent.ParseAdd("MnyMC/1.0");

        try
        {
            using var response = await McHttp.Client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException("CurseForge API Key 无效");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"CurseForge API 请求失败（HTTP {(int)response.StatusCode}）");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("连接 CurseForge 超时，请检查网络或代理设置。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接 CurseForge，请检查网络或代理设置。");
        }
    }

    private static T? Deserialize<T>(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("CurseForge 返回的数据无法解析。");
        }
    }

    private sealed class CfListResponse<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
    }

    private sealed class CfDataResponse<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }
}
