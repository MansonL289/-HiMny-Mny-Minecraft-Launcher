using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinUI3Shell.Services.Mc;

/// <summary>Mojang rules 语义：os/arch/version 正则过滤、features 匹配（本启动器仅 Windows）。</summary>
internal static class McRules
{
    public static readonly string ArchBits = Environment.Is64BitOperatingSystem ? "64" : "32";

    private static readonly string CurrentArch =
        RuntimeInformation.OSArchitecture == Architecture.X86
            ? "x86"
            : RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    private static readonly string OsVersion = Environment.OSVersion.Version.ToString();

    public static bool Allow(JsonArray? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules == null || rules.Count == 0) return true;
        bool allowed = false;
        foreach (var node in rules)
        {
            var rule = node!.AsObject();
            if (!OsMatch(rule["os"]?.AsObject())) continue;
            if (!FeaturesMatch(rule["features"]?.AsObject(), features)) continue;
            allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    private static bool OsMatch(JsonObject? os)
    {
        if (os == null) return true;
        var name = os["name"]?.GetValue<string>();
        if (name != null && name != "windows") return false;
        var arch = os["arch"]?.GetValue<string>();
        if (arch != null && !Regex.IsMatch(CurrentArch, arch)) return false;
        var version = os["version"]?.GetValue<string>();
        if (version != null)
        {
            try
            {
                if (!Regex.IsMatch(OsVersion, version)) return false;
            }
            catch (RegexParseException) { return false; }
        }
        return true;
    }

    private static bool FeaturesMatch(JsonObject? ruleFeatures, IReadOnlyDictionary<string, bool>? features)
    {
        if (ruleFeatures == null) return true;
        foreach (var kv in ruleFeatures)
        {
            bool expected = kv.Value!.GetValue<bool>();
            bool actual = features != null && features.TryGetValue(kv.Key, out var b) && b;
            if (actual != expected) return false;
        }
        return true;
    }
}

/// <summary>libraries 规则过滤、classpath 计算、natives 解压。</summary>
public static class Libraries
{
    public sealed record NativeItem(McDownloadTask Task, JsonObject? Extract);

    /// <summary>解析 version.json 的 libraries，返回 (下载任务, classpath 路径, natives 列表)。</summary>
    public static (List<McDownloadTask> Tasks, List<string> Classpath, List<NativeItem> Natives)
        Collect(JsonObject versionJson, string mirror)
    {
        var tasks = new List<McDownloadTask>();
        var classpath = new List<string>();
        var natives = new List<NativeItem>();
        if (versionJson["libraries"] is not JsonArray libs) return (tasks, classpath, natives);

        foreach (var node in libs)
        {
            var lib = node!.AsObject();
            if (!McRules.Allow(lib["rules"]?.AsArray())) continue;
            var downloads = lib["downloads"]?.AsObject();

            var artifact = downloads?["artifact"]?.AsObject();
            // 注意：有些库（如 Forge 内嵌 version.json 里的 forge:client）url 是空字符串，
            // 这类文件由安装器处理器在本地生成（PATCHED），不能下载，只需补进 classpath。
            string? artifactUrl = artifact?["url"] is { } urlNode ? urlNode.GetValue<string>() : null;
            string? artifactPath = artifact?["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(artifactUrl) && artifactPath != null)
            {
                tasks.Add(new McDownloadTask(
                    Mirror.Rewrite(artifactUrl, mirror),
                    Path.Combine(McPaths.LibrariesDir, artifactPath),
                    artifact!["sha1"]?.GetValue<string>(),
                    artifact["size"]?.GetValue<long>()));
                classpath.Add(Path.Combine(McPaths.LibrariesDir, artifactPath));
            }
            else if (artifactPath != null)
            {
                classpath.Add(Path.Combine(McPaths.LibrariesDir, artifactPath));
            }
            else if (downloads == null && MavenPath(lib["name"]?.GetValue<string>()) is { Length: > 0 } legacyRel)
            {
                // 老格式库：没有 downloads，按 maven 坐标 + 仓库地址拼接
                string repo = lib["url"]?.GetValue<string>() is { Length: > 0 } rawRepo
                    ? rawRepo
                    : "https://libraries.minecraft.net/";
                string dest = Path.Combine(McPaths.LibrariesDir, legacyRel.Replace('/', Path.DirectorySeparatorChar));
                tasks.Add(new McDownloadTask(Mirror.Rewrite(repo.TrimEnd('/') + "/" + legacyRel, mirror), dest));
                classpath.Add(dest);
            }

            // natives：按当前系统取分类器，处理 ${arch}
            var classifier = lib["natives"]?.AsObject()?["windows"]?.GetValue<string>()
                ?.Replace("${arch}", McRules.ArchBits);
            if (classifier != null)
            {
                var entry = downloads?["classifiers"]?[classifier]?.AsObject();
                string? classifierUrl = entry?["url"] is { } classifierNode
                    ? classifierNode.GetValue<string>()
                    : null;
                if (!string.IsNullOrWhiteSpace(classifierUrl))
                {
                    var task = new McDownloadTask(
                        Mirror.Rewrite(classifierUrl, mirror),
                        Path.Combine(McPaths.LibrariesDir, entry!["path"]!.GetValue<string>()),
                        entry["sha1"]?.GetValue<string>(),
                        entry["size"]?.GetValue<long>());
                    tasks.Add(task);
                    natives.Add(new NativeItem(task, lib["extract"]?.AsObject()));
                }
                else if (downloads == null
                    && MavenPath(lib["name"]?.GetValue<string>() + ":" + classifier) is { Length: > 0 } legacyNativeRel)
                {
                    // 老格式 natives（如 1.5 时代的 lwjgl-platform）
                    string repo = lib["url"]?.GetValue<string>() is { Length: > 0 } rawRepo
                        ? rawRepo
                        : "https://libraries.minecraft.net/";
                    var task = new McDownloadTask(
                        Mirror.Rewrite(repo.TrimEnd('/') + "/" + legacyNativeRel, mirror),
                        Path.Combine(McPaths.LibrariesDir, legacyNativeRel.Replace('/', Path.DirectorySeparatorChar)));
                    tasks.Add(task);
                    natives.Add(new NativeItem(task, lib["extract"]?.AsObject()));
                }
            }
        }
        return (tasks, classpath, natives);
    }

    /// <summary>maven 坐标转相对路径：group:artifact:version[:classifier][@ext]
    /// -&gt; group/artifact/version/artifact-version[-classifier].ext。格式不对返回空串。</summary>
    internal static string MavenPath(string? maven)
    {
        if (string.IsNullOrEmpty(maven)) return "";
        string ext = "jar";
        int at = maven.IndexOf('@');
        if (at >= 0)
        {
            ext = maven[(at + 1)..];
            maven = maven[..at];
        }
        var parts = maven.Split(':');
        if (parts.Length < 3) return "";
        string g = parts[0].Replace('.', '/'), a = parts[1], v = parts[2];
        string file = parts.Length > 3 ? $"{a}-{v}-{parts[3]}.{ext}" : $"{a}-{v}.{ext}";
        return $"{g}/{a}/{v}/{file}";
    }

    /// <summary>把 natives jar 解压到 versions/&lt;id&gt;/natives，遵守 extract.exclude。</summary>
    public static void ExtractNatives(IEnumerable<NativeItem> natives, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var (task, extract) in natives)
        {
            var excludes = extract?["exclude"]?.AsArray()
                .Select(x => x!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            using var zip = ZipFile.OpenRead(task.Dest);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项
                if (excludes.Any(ex => entry.FullName.StartsWith(ex, StringComparison.Ordinal))) continue;
                string dest = Path.Combine(destDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
        }
    }
}

/// <summary>assetIndex 与资源文件下载（assets/indexes + assets/objects）。</summary>
public static class Assets
{
    /// <summary>启动前自愈：virtual 索引（pre-1.6/legacy）的老版本若资源目录未物化（历史安装/安装中断），从 objects 补齐。</summary>
    public static async Task HealVirtualAsync(JsonObject versionJson, CancellationToken ct = default)
    {
        string? id = versionJson["assetIndex"]?["id"]?.GetValue<string>()
            ?? versionJson["assets"]?.GetValue<string>();
        if (id == null) return;
        string indexPath = Path.Combine(McPaths.AssetsDir, "indexes", id + ".json");
        if (!File.Exists(indexPath)) return;
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, ct))!.AsObject();
        if (index["virtual"]?.GetValue<bool>() != true) return;
        string virtualDir = Path.Combine(McPaths.AssetsDir, "virtual", id);
        var objects = index["objects"]!.AsObject();
        if (Directory.Exists(virtualDir) && Directory.EnumerateFiles(virtualDir).Count() >= objects.Count)
        {
            return; // 已物化完整
        }
        await Task.Run(() => MaterializeVirtual(objects, id), ct);
    }

    /// <summary>把 objects 按资源路径物化为 assets/virtual/&lt;id&gt;/&lt;路径&gt;（老版本游戏按路径读资源）。</summary>
    private static void MaterializeVirtual(JsonObject objects, string id)
    {
        foreach (var kv in objects)
        {
            string hash = kv.Value!["hash"]!.GetValue<string>();
            string src = Path.Combine(McPaths.AssetsDir, "objects", hash[..2], hash);
            string dst = Path.Combine(McPaths.AssetsDir, "virtual", id,
                kv.Key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dst) || !File.Exists(src)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }
    }

    public static async Task<string> EnsureAsync(JsonObject versionJson, string mirror, int workers,
        IProgress<McProgress>? progress, CancellationToken ct = default)
    {
        var idx = versionJson["assetIndex"]?.AsObject();
        if (idx == null) return versionJson["assets"]?.GetValue<string>() ?? "legacy";

        string id = idx["id"]!.GetValue<string>();
        string indexPath = Path.Combine(McPaths.AssetsDir, "indexes", id + ".json");
        progress?.Report(new McProgress("资产索引", 0, 0, 0, 0));
        await Downloader.DownloadFileAsync(new McDownloadTask(
            Mirror.Rewrite(idx["url"]!.GetValue<string>(), mirror), indexPath,
            idx["sha1"]?.GetValue<string>(), idx["size"]?.GetValue<long>()), null, ct);

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, ct))!.AsObject();
        var objects = index["objects"]!.AsObject();
        var tasks = new List<McDownloadTask>(objects.Count);
        foreach (var kv in objects)
        {
            string hash = kv.Value!["hash"]!.GetValue<string>();
            string url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            tasks.Add(new McDownloadTask(
                Mirror.Rewrite(url, mirror),
                Path.Combine(McPaths.AssetsDir, "objects", hash[..2], hash),
                hash, kv.Value!["size"]?.GetValue<long>()));
        }
        await Downloader.DownloadManyAsync(tasks, workers, "资源文件", progress, ct);

        // 老版本（virtual 索引，如 pre-1.6/legacy）：游戏按资源路径直接读文件，
        // 需要把 objects 物化为 assets/virtual/<id>/<资源路径>，否则老版本没有声音
        if (index["virtual"]?.GetValue<bool>() == true)
        {
            progress?.Report(new McProgress("整理旧版资源", 0, 0, 0, 0));
            await Task.Run(() => MaterializeVirtual(objects, id), ct);
        }
        return id;
    }
}

/// <summary>版本安装管线：version.json → client.jar + libraries → natives → log4j → assets。</summary>
public static class Installer
{
    /// <param name="installName">安装目录名（版本名）；空则用 versionId。json/jar/natives 落在该目录，文件名仍按 versionId。</param>
    public static async Task<JsonObject> PrepareAsync(string versionId, string mirror, int workers,
        IProgress<McProgress>? progress, CancellationToken ct = default, string? installName = null)
    {
        progress?.Report(new McProgress("获取版本清单", 0, 0, 0, 0));
        JsonObject manifest;
        try { manifest = await Manifest.FetchAsync(mirror, v2: true, ct); }
        catch { manifest = await Manifest.FetchAsync(mirror, v2: false, ct); }
        var (url, sha1) = Manifest.Find(manifest, versionId);

        string vdir = McPaths.VersionDir(installName ?? versionId);
        string jsonPath = Path.Combine(vdir, versionId + ".json");
        progress?.Report(new McProgress("下载 version.json", 0, 0, 0, 0));
        await Downloader.DownloadFileAsync(
            new McDownloadTask(Mirror.Rewrite(url, mirror), jsonPath, sha1), null, ct);
        var versionJson = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct))!.AsObject();

        var client = versionJson["downloads"]?["client"]?.AsObject();
        if (client?["url"] == null)
        {
            // 远古版没有 downloads.client，直接空引用崩溃会变成看不懂的报错
            throw new InvalidOperationException($"版本 {versionId} 没有可用的客户端下载（远古版暂不支持自动安装）。");
        }
        var jarTask = new McDownloadTask(
            Mirror.Rewrite(client["url"]!.GetValue<string>(), mirror),
            Path.Combine(vdir, versionId + ".jar"),
            client["sha1"]?.GetValue<string>(), client["size"]?.GetValue<long>());
        var (libTasks, _, natives) = Libraries.Collect(versionJson, mirror);
        await Downloader.DownloadManyAsync(
            new[] { jarTask }.Concat(libTasks).ToList(), workers, "核心与依赖库", progress, ct);

        progress?.Report(new McProgress("解压运行库", 0, 0, 0, 0));
        await Task.Run(() => Libraries.ExtractNatives(natives, Path.Combine(vdir, "natives")), ct);

        var logFile = versionJson["logging"]?["client"]?["file"]?.AsObject();
        if (logFile?["url"] != null)
        {
            await Downloader.DownloadFileAsync(new McDownloadTask(
                Mirror.Rewrite(logFile["url"]!.GetValue<string>(), mirror),
                Path.Combine(McPaths.AssetsDir, "log_configs", logFile["id"]!.GetValue<string>()),
                logFile["sha1"]?.GetValue<string>(), logFile["size"]?.GetValue<long>()), null, ct);
        }

        await Assets.EnsureAsync(versionJson, mirror, workers, progress, ct);
        progress?.Report(new McProgress("完成", 0, 0, 0, 0));
        return versionJson;
    }
}
