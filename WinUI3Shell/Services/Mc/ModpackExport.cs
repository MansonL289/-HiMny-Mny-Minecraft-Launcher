using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>把已安装版本导出成 Modrinth 整合包（.mrpack）：内容文件全部放进 overrides/。</summary>
public static class ModpackExport
{
    /// <summary>超过这个大小的单个文件不导出（避免导出超大资源包）。</summary>
    private const long MaxFileBytes = 100L * 1024 * 1024;

    /// <summary>需要打包进 overrides 的游戏目录子目录。</summary>
    private static readonly string[] ContentDirs = { "mods", "config", "resourcepacks", "shaderpacks" };
    /// <summary>需要打包进 overrides 的游戏目录根文件。</summary>
    private static readonly string[] ContentFiles = { "options.txt", "servers.dat" };

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>导出整合包，返回生成的 .mrpack 路径。</summary>
    public static async Task<string> ExportAsync(string versionDir, string destZip,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
        string versionName = Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(versionName)) versionName = "整合包";

        var meta = VersionMeta.Load(versionName) ?? new VersionMeta { Id = versionName };
        string mcVersion = meta.McVersion(versionName);
        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            throw new InvalidOperationException("无法确定该版本的 Minecraft 版本号，不能导出整合包。");
        }
        string gameDir = meta.GameDir(versionName);
        if (!Directory.Exists(gameDir))
        {
            throw new InvalidOperationException("版本游戏目录不存在，不能导出整合包。");
        }

        string destFull = Path.GetFullPath(destZip);
        string? destDir = Path.GetDirectoryName(destFull);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        // ZipFile 的 Create 模式要求目标文件不存在
        if (File.Exists(destFull)) File.Delete(destFull);

        var index = BuildIndex(versionName, mcVersion, meta);
        int skipped = 0;
        var files = CollectFiles(gameDir, destFull, ct, ref skipped);
        long totalBytes = files.Sum(f => f.Size);

        progress?.Report(new McProgress("导出整合包", 0, totalBytes, 0, files.Count));

        int done = 0;
        long doneBytes = 0;
        using (var zip = ZipFile.Open(destFull, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(index.ToJsonString(IndexJsonOptions));
            }

            foreach (var (fullPath, entryName, size) in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using var input = File.OpenRead(fullPath);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, ct);
                }
                catch (IOException)
                {
                    throw new InvalidOperationException(
                        $"读取「{Path.GetFileName(fullPath)}」失败，文件可能正被游戏占用。");
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"没有权限读取「{Path.GetFileName(fullPath)}」。");
                }

                done++;
                doneBytes += size;
                if (done % 10 == 0 || done == files.Count)
                {
                    progress?.Report(new McProgress("导出整合包", doneBytes, totalBytes, done, files.Count));
                }
            }
        }

        if (skipped > 0)
        {
            progress?.Report(new McProgress($"导出完成，已跳过 {skipped} 个超过 100MB 的文件",
                totalBytes, totalBytes, files.Count, files.Count));
        }
        else
        {
            progress?.Report(new McProgress("导出完成", totalBytes, totalBytes, files.Count, files.Count));
        }
        return destFull;
    }

    /// <summary>生成 modrinth.index.json：只有依赖信息，所有内容文件都放 overrides。</summary>
    private static JsonObject BuildIndex(string versionName, string mcVersion, VersionMeta meta)
    {
        var dependencies = new JsonObject { ["minecraft"] = mcVersion };

        // DetectLoader 返回形如「Fabric 0.15.0」「NeoForge 21.1.0」「Forge / NeoForge」
        string[] parts = meta.DetectLoader(versionName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string key = parts.Length > 0 ? LoaderDependencyKey(parts[0]) : "";
        string loaderVersion = parts.Length > 1 && parts[1] != "/" ? parts[1] : "";
        if (key.Length > 0 && loaderVersion.Length > 0) dependencies[key] = loaderVersion;

        return new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "1.0.0",
            ["name"] = versionName,
            ["summary"] = "",
            ["files"] = new JsonArray(),
            ["dependencies"] = dependencies,
        };
    }

    /// <summary>加载器种类 → mrpack dependencies 的键（取不到版本号时整项省略）。</summary>
    private static string LoaderDependencyKey(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "fabric-loader",
        "forge" => "forge",
        "neoforge" => "neoforge",
        _ => "",
    };

    /// <summary>收集要导出的文件；saves / libraries / assets / versions / crash-reports / logs 都不导出。</summary>
    private static List<(string FullPath, string EntryName, long Size)> CollectFiles(string gameDir,
        string destFull, CancellationToken ct, ref int skipped)
    {
        var list = new List<(string, string, long)>();
        string root = Path.GetFullPath(gameDir);

        foreach (string sub in ContentDirs)
        {
            string dirPath = Path.Combine(root, sub);
            if (!Directory.Exists(dirPath)) continue;
            foreach (string file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                TryAdd(file, root, destFull, ref skipped, list);
            }
        }

        foreach (string fileName in ContentFiles)
        {
            string file = Path.Combine(root, fileName);
            if (!File.Exists(file)) continue;
            TryAdd(file, root, destFull, ref skipped, list);
        }
        return list;
    }

    private static bool TryAdd(string file, string root, string destFull, ref int skipped,
        List<(string FullPath, string EntryName, long Size)> list)
    {
        string full = Path.GetFullPath(file);
        if (string.Equals(full, destFull, StringComparison.OrdinalIgnoreCase)) return false;
        long size = new FileInfo(full).Length;
        if (size > MaxFileBytes)
        {
            skipped++;
            return false;
        }
        string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        list.Add((full, "overrides/" + rel, size));
        return true;
    }
}
