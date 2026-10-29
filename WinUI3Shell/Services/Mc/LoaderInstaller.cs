using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>附加模块安装：Fabric / Quilt Loader（JSON 合并进版本文件）。</summary>
public static class LoaderInstaller
{
    private const string FabricRepo = "https://maven.fabricmc.net/";
    private const string QuiltRepo = "https://maven.quiltmc.org/repository/release/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return c;
    }

    /// <summary>把 Fabric Loader 合并进已安装的原版版本 json：
    /// libraries 前置、mainClass 覆盖、jvm/game arguments 追加。</summary>
    /// <param name="installName">安装目录名（版本名）；空则用 mcVersion。</param>
    /// <param name="loaderVersion">手动指定的 loader 版本号；空则自动取最新。</param>
    public static async Task InstallFabricAsync(string mcVersion, CancellationToken ct,
        string? installName = null, string? loaderVersion = null)
    {
        string loaderVer;
        if (loaderVersion != null)
        {
            loaderVer = loaderVersion;
        }
        else
        {
            // 1. 该 MC 版本的最新 loader 版本
            var loaders = JsonNode.Parse(await Http.GetStringAsync(
                $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct))!.AsArray();
            if (loaders.Count == 0)
            {
                throw new InvalidOperationException($"Fabric 不支持 {mcVersion}");
            }
            loaderVer = loaders[0]!["loader"]!["version"]!.GetValue<string>();
        }

        // 2. profile json（mainClass + libraries + arguments）
        var profile = JsonNode.Parse(await Http.GetStringAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVer}/profile/json", ct))!.AsObject();

        // 3. 下载 Fabric 的 libraries（maven 坐标）
        var newLibs = await DownloadProfileLibrariesAsync(profile, FabricRepo, "Fabric 依赖库", ct);

        // 4. 合并进版本 json
        await MergeProfileAsync(mcVersion, installName, profile, newLibs, ct);
    }

    /// <summary>把 Quilt Loader 合并进已安装的原版版本 json，结构与 Fabric 一致。</summary>
    /// <param name="installName">安装目录名（版本名）；空则用 mcVersion。</param>
    /// <param name="loaderVersion">手动指定的 loader 版本号；空则自动取最新。</param>
    public static async Task InstallQuiltAsync(string mcVersion, CancellationToken ct,
        string? installName = null, string? loaderVersion = null)
    {
        string loaderVer;
        if (loaderVersion != null)
        {
            loaderVer = loaderVersion;
        }
        else
        {
            // 1. 该 MC 版本的最新 loader 版本
            var loaders = JsonNode.Parse(await Http.GetStringAsync(
                $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}", ct))!.AsArray();
            if (loaders.Count == 0)
            {
                throw new InvalidOperationException($"Quilt 不支持 {mcVersion}");
            }
            loaderVer = loaders[0]!["loader"]!["version"]!.GetValue<string>();
        }

        // 2. profile json（mainClass + libraries + arguments）
        var profile = JsonNode.Parse(await Http.GetStringAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loaderVer}/profile/json", ct))!.AsObject();

        // 3. 下载 Quilt 的 libraries：url 为空时回落到 Quilt 官方 maven
        var newLibs = await DownloadProfileLibrariesAsync(profile, QuiltRepo, "Quilt 依赖库", ct);

        // 4. 合并进版本 json
        await MergeProfileAsync(mcVersion, installName, profile, newLibs, ct);
    }

    /// <summary>把 profile 里的 libraries 深拷贝成版本 json 条目，并下载到 libraries 目录。</summary>
    private static async Task<List<JsonObject>> DownloadProfileLibrariesAsync(JsonObject profile,
        string defaultRepo, string phase, CancellationToken ct)
    {
        var tasks = new List<McDownloadTask>();
        var newLibs = new List<JsonObject>();
        var profileLibs = profile["libraries"]?.AsArray();
        if (profileLibs == null) return newLibs;

        foreach (var node in profileLibs)
        {
            var lib = (JsonObject)JsonNode.Parse(node!.ToJsonString())!;
            string rel = Libraries.MavenPath(lib["name"]!.GetValue<string>());
            if (rel.Length == 0) continue;
            string? rawUrl = lib["url"]?.GetValue<string>();
            string repo = string.IsNullOrEmpty(rawUrl) ? defaultRepo : rawUrl!;
            string url = repo.TrimEnd('/') + "/" + rel;
            tasks.Add(new McDownloadTask(url,
                Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar))));
            newLibs.Add(lib);
        }
        await Downloader.DownloadManyAsync(tasks, 8, phase, null, ct);
        return newLibs;
    }

    /// <summary>mainClass 覆盖、libraries 前置、jvm/game arguments 追加（Fabric 与 Quilt 共用）。</summary>
    private static async Task MergeProfileAsync(string mcVersion, string? installName,
        JsonObject profile, List<JsonObject> newLibs, CancellationToken ct)
    {
        string jsonPath = Path.Combine(McPaths.VersionDir(installName ?? mcVersion), mcVersion + ".json");
        var vjson = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct))!.AsObject();
        vjson["mainClass"] = profile["mainClass"]!.GetValue<string>();

        var libs = vjson["libraries"]!.AsArray();
        for (int i = newLibs.Count - 1; i >= 0; i--)
        {
            libs.Insert(0, newLibs[i]);
        }

        if (profile["arguments"]?.AsObject() is { } pArgs)
        {
            var vArgs = vjson["arguments"]!.AsObject();
            foreach (string key in new[] { "jvm", "game" })
            {
                if (pArgs[key] is JsonArray extra)
                {
                    if (vArgs[key] is JsonArray target)
                    {
                        foreach (var item in extra)
                        {
                            target.Add(JsonNode.Parse(item!.ToJsonString()));
                        }
                    }
                    else
                    {
                        vArgs[key] = JsonNode.Parse(extra.ToJsonString());
                    }
                }
            }
        }
        await File.WriteAllTextAsync(jsonPath, vjson.ToJsonString(), ct);
    }
}
