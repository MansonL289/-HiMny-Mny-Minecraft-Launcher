using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>附加模块安装：Fabric Loader（JSON 合并进版本文件）。</summary>
public static class LoaderInstaller
{
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

        // 3. 下载 Fabric 的 libraries（maven 坐标），深拷贝节点供后面合并
        var tasks = new List<McDownloadTask>();
        var newLibs = new List<JsonObject>();
        foreach (var node in profile["libraries"]!.AsArray())
        {
            var lib = (JsonObject)JsonNode.Parse(node!.ToJsonString())!;
            string rel = Libraries.MavenPath(lib["name"]!.GetValue<string>());
            if (rel.Length == 0) continue;
            string url = (lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/").TrimEnd('/') + "/" + rel;
            tasks.Add(new McDownloadTask(url,
                Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar))));
            newLibs.Add(lib);
        }
        await Downloader.DownloadManyAsync(tasks, 8, "Fabric 依赖库", null, ct);

        // 4. 合并进版本 json
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
