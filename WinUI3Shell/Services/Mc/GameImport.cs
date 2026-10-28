using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>外部启动器目录里发现的一个版本。</summary>
public sealed class ImportedVersion
{
    /// <summary>外部 versions/&lt;名&gt; 全路径。</summary>
    public string SourceDir { get; init; } = "";
    /// <summary>目录名。</summary>
    public string Name { get; init; } = "";
    /// <summary>底层 MC 版本（优先 inheritsFrom，其次 json 的 id），读不到时用目录名。</summary>
    public string McVersion { get; init; } = "";
    /// <summary>加载器说明，如 Fabric 0.15.11 / Forge 47.2.0 / 原版。</summary>
    public string LoaderText { get; init; } = "";
    /// <summary>json + jar 都在。</summary>
    public bool IsComplete { get; init; }

    /// <summary>列表说明文字，如「1.20.1 · Fabric 0.15.11」。</summary>
    public string DetailText => string.IsNullOrEmpty(LoaderText) ? McVersion : $"{McVersion} · {LoaderText}";
}

/// <summary>从其他启动器的 .minecraft 目录导入版本与存档。</summary>
public static class GameImport
{
    /// <summary>扫描外部启动器根目录（如 C:\Users\x\AppData\Roaming\.minecraft）。找不到 versions 时返回空列表。</summary>
    public static List<ImportedVersion> Scan(string root)
    {
        var result = new List<ImportedVersion>();
        try
        {
            string versionsDir = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsDir)) return result;

            foreach (string dir in Directory.EnumerateDirectories(versionsDir)
                         .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                try
                {
                    string name = Path.GetFileName(dir)!;
                    string? jsonPath = FindJson(dir, name);
                    if (jsonPath == null) continue;

                    JsonObject json;
                    try
                    {
                        json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
                    }
                    catch
                    {
                        continue; // json 损坏的目录不算可导入版本
                    }

                    string mcVersion = json["inheritsFrom"]?.GetValue<string>()
                        ?? json["id"]?.GetValue<string>() ?? name;
                    string jarStem = Path.GetFileNameWithoutExtension(jsonPath);
                    bool complete = File.Exists(Path.Combine(dir, jarStem + ".jar")) ||
                                    File.Exists(Path.Combine(dir, name + ".jar"));
                    result.Add(new ImportedVersion
                    {
                        SourceDir = dir,
                        Name = name,
                        McVersion = mcVersion,
                        LoaderText = DetectLoader(json, mcVersion),
                        IsComplete = complete,
                    });
                }
                catch
                {
                    // 单个版本读取出错：忽略该版本
                }
            }
        }
        catch
        {
            // 目录枚举失败：返回已发现的部分
        }
        return result;
    }

    /// <summary>把选中的版本复制到本启动器的 versions 目录，返回新目录名；重名自动加 -import/-import2。</summary>
    public static async Task<string> ImportVersionAsync(ImportedVersion version,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
        string source = Path.GetFullPath(version.SourceDir);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("源版本目录不存在");

        string name = UniqueVersionName(version.Name);
        string target = Path.GetFullPath(VersionMeta.DirPath(name));
        // 目标落在源目录内部会把新文件再复制进自己，直接拒绝
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标目录位于源目录内，已跳过");
        }

        string jsonPath = FindJson(source, version.Name)
            ?? throw new InvalidOperationException("源目录里没有版本 json");
        // 文件名保持原样，Id 指向正确的 json/jar 文件名
        string id = Path.GetFileNameWithoutExtension(jsonPath);

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(target);
                int total = files.Count;
                int done = 0;
                progress?.Report(new McProgress("导入版本", 0, 0, 0, total));
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string dest = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                    done++;
                    if (done % 20 == 0)
                    {
                        progress?.Report(new McProgress("导入版本", 0, 0, done, total));
                    }
                }
                progress?.Report(new McProgress("导入版本", 0, 0, done, total));
            }, ct);
        }
        catch
        {
            // 复制未完成：删掉半成品，避免出现在版本列表里
            try { Directory.Delete(target, true); } catch { /* 删不掉就留着 */ }
            throw;
        }

        new VersionMeta { Id = id }.Save(name);
        return name;
    }

    /// <summary>把外部根目录下的 saves/* 复制进本启动器存档目录（重名加 (2)），返回复制数量。</summary>
    public static async Task<int> ImportSavesAsync(string root, string targetSavesDir, CancellationToken ct)
    {
        string savesDir = Path.Combine(root, "saves");
        if (!Directory.Exists(savesDir)) return 0;

        Directory.CreateDirectory(targetSavesDir);
        int count = 0;
        foreach (string saveDir in Directory.EnumerateDirectories(savesDir).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string destName = UniqueSaveName(targetSavesDir, Path.GetFileName(saveDir)!);
                string dest = Path.Combine(targetSavesDir, destName);
                await Task.Run(() => CopyTree(saveDir, dest), ct);
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个存档复制失败不影响其他存档
            }
        }
        return count;
    }

    /// <summary>找版本目录里的 json：优先与目录同名的，其次任意 .json（忽略本启动器的 mnymc.json）。</summary>
    private static string? FindJson(string dir, string name)
    {
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).Equals(VersionMeta.MetaFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string preferred = name + ".json";
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>按 libraries[].name 识别加载器，返回如「Fabric 0.15.11」；识别不出返回「原版」。</summary>
    private static string DetectLoader(JsonObject json, string mcVersion)
    {
        if (json["libraries"] is not JsonArray libraries) return "原版";

        foreach (var node in libraries)
        {
            string coordinate = node?["name"]?.GetValue<string>() ?? "";
            if (coordinate.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
            {
                return "Fabric " + StripMcPrefix(CoordinateVersion(coordinate), mcVersion);
            }
            if (coordinate.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase))
            {
                return "NeoForge " + StripMcPrefix(CoordinateVersion(coordinate), mcVersion);
            }
            if (coordinate.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
            {
                return "Forge " + StripMcPrefix(CoordinateVersion(coordinate), mcVersion);
            }
        }
        return "原版";
    }

    /// <summary>maven 坐标最后一段版本号：group:artifact:version[:classifier][@ext]。</summary>
    private static string CoordinateVersion(string coordinate)
    {
        string version = coordinate;
        int at = version.IndexOf('@');
        if (at >= 0) version = version[..at];
        int colon = version.LastIndexOf(':');
        return colon >= 0 ? version[(colon + 1)..] : version;
    }

    /// <summary>Forge/NeoForge 的版本号常带 MC 前缀（1.20.1-47.2.0），展示时去掉。</summary>
    private static string StripMcPrefix(string version, string mcVersion)
    {
        string prefix = mcVersion + "-";
        return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? version[prefix.Length..]
            : version;
    }

    private static string UniqueVersionName(string baseName)
    {
        if (!Directory.Exists(VersionMeta.DirPath(baseName))) return baseName;
        string name = baseName + "-import";
        for (int i = 2; Directory.Exists(VersionMeta.DirPath(name)); i++)
        {
            name = $"{baseName}-import{i}";
        }
        return name;
    }

    private static string UniqueSaveName(string dir, string baseName)
    {
        if (!Directory.Exists(Path.Combine(dir, baseName))) return baseName;
        for (int i = 2; ; i++)
        {
            string name = $"{baseName} ({i})";
            if (!Directory.Exists(Path.Combine(dir, name))) return name;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
