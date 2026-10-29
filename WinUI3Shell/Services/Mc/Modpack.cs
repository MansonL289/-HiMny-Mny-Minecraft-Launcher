using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>整合包内的一个待下载文件。</summary>
public sealed class ModpackFile
{
    /// <summary>相对游戏目录的路径，如 mods/xxx.jar。</summary>
    public string RelPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

/// <summary>解析后的整合包信息（Modrinth .mrpack 或 CurseForge .zip）。</summary>
public sealed class ModpackInfo
{
    public string ZipPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string McVersion { get; set; } = "";
    /// <summary>fabric / forge / neoforge / quilt（空 = 原版）。</summary>
    public string LoaderKind { get; set; } = "";
    public string? LoaderVersion { get; set; }
    public List<ModpackFile> Files { get; set; } = new();
    /// <summary>是否为 CurseForge 整合包（manifest.json）。</summary>
    public bool IsCurseForge { get; init; }
    /// <summary>CurseForge manifest 里的 (projectID, fileID)，安装时再查真实下载地址。</summary>
    public List<(int ModId, int FileId)> CfFiles { get; init; } = new();

    public string LoaderText => LoaderKind switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "原版",
    };

    public string SummaryText => $"{LoaderText} {LoaderVersion} · Minecraft {McVersion} · {Files.Count} 个文件";
}

/// <summary>读取整合包：优先 Modrinth 的 modrinth.index.json，其次 CurseForge 的 manifest.json。</summary>
public static class Modpack
{
    public static ModpackInfo Load(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("modrinth.index.json");
        if (entry == null)
        {
            var cfEntry = zip.GetEntry("manifest.json");
            if (cfEntry != null) return LoadCurseForge(zipPath, cfEntry);
            throw new InvalidDataException("压缩包里没有 modrinth.index.json，不是 Modrinth 整合包。");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();

        var deps = root["dependencies"]?.AsObject();
        string mc = deps?["minecraft"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(mc)) throw new InvalidDataException("整合包没有声明 Minecraft 版本。");

        string loaderKind = "";
        string? loaderVersion = null;
        foreach (var (key, kind) in new[]
        {
            ("fabric-loader", "fabric"),
            ("neoforge", "neoforge"),
            ("forge", "forge"),
            ("quilt-loader", "quilt"),
        })
        {
            if (deps?[key] is JsonNode node)
            {
                loaderKind = kind;
                loaderVersion = node.GetValue<string>();
                break;
            }
        }

        var info = new ModpackInfo
        {
            ZipPath = zipPath,
            Name = root["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(zipPath),
            Version = root["versionId"]?.GetValue<string>() ?? "",
            McVersion = mc,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
        };

        foreach (var node in root["files"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject file) continue;
            // env.client == "unsupported"：仅服务端文件，跳过
            if (file["env"]?["client"]?.GetValue<string>() == "unsupported") continue;

            string rel = file["path"]?.GetValue<string>() ?? "";
            string url = file["downloads"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(url)) continue;

            // 防目录穿越
            string full = Path.GetFullPath(Path.Combine(Path.GetTempPath(), rel.Replace('/', '\\')));
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) continue;

            info.Files.Add(new ModpackFile
            {
                RelPath = rel,
                Url = url,
                Sha1 = file["hashes"]?["sha1"]?.GetValue<string>(),
                Size = file["fileSize"]?.GetValue<long>() ?? 0,
            });
        }

        return info;
    }

    /// <summary>读取 CurseForge 整合包的 manifest.json：只有 projectID/fileID，真实下载地址安装时再查。</summary>
    private static ModpackInfo LoadCurseForge(string zipPath, ZipArchiveEntry entry)
    {
        JsonObject manifest;
        using (var stream = entry.Open())
        using (var reader = new StreamReader(stream))
        {
            try
            {
                manifest = JsonNode.Parse(reader.ReadToEnd())?.AsObject()
                    ?? throw new InvalidDataException("CurseForge manifest.json 内容为空。");
            }
            catch (JsonException)
            {
                throw new InvalidDataException("CurseForge 整合包的 manifest.json 格式不正确。");
            }
        }

        var minecraft = manifest["minecraft"]?.AsObject();
        string mc = minecraft?["version"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(mc)) throw new InvalidDataException("整合包没有声明 Minecraft 版本。");

        // modLoaders[0].id 形如 forge-47.2.0 / fabric-0.15.0 / neoforge-21.1.0
        string loaderKind = "";
        string? loaderVersion = null;
        string loaderId = minecraft?["modLoaders"]?.AsArray()?.FirstOrDefault()?["id"]?.GetValue<string>() ?? "";
        int dash = loaderId.IndexOf('-');
        if (dash > 0)
        {
            loaderKind = loaderId[..dash].ToLowerInvariant() switch
            {
                "forge" => "forge",
                "fabric" => "fabric",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                _ => "",
            };
            if (loaderKind.Length > 0) loaderVersion = loaderId[(dash + 1)..];
        }

        var cfFiles = new List<(int ModId, int FileId)>();
        foreach (var node in manifest["files"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject file) continue;
            int projectId = file["projectID"]?.GetValue<int>() ?? 0;
            int fileId = file["fileID"]?.GetValue<int>() ?? 0;
            if (projectId <= 0 || fileId <= 0) continue;
            cfFiles.Add((projectId, fileId));
        }

        var info = new ModpackInfo
        {
            ZipPath = zipPath,
            Name = manifest["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(zipPath),
            Version = manifest["version"]?.GetValue<string>() ?? "",
            McVersion = mc,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            IsCurseForge = true,
            CfFiles = cfFiles,
        };
        // 占位文件：真实文件名/地址在安装时查询；只用于预览数量
        foreach (var (modId, fileId) in cfFiles)
        {
            info.Files.Add(new ModpackFile { RelPath = $"mods/pending-{modId}-{fileId}.jar" });
        }
        return info;
    }

    /// <summary>整合包默认安装名：名称 + （有版本号时）版本号；重名自动加序号。</summary>
    public static string SuggestInstallName(ModpackInfo info)
    {
        string baseName = string.IsNullOrWhiteSpace(info.Version) ? info.Name : $"{info.Name}-{info.Version}";
        baseName = Sanitize(baseName);
        string name = baseName;
        for (int i = 2; Directory.Exists(VersionMeta.DirPath(name)); i++)
        {
            name = $"{baseName}-{i}";
        }
        return name;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "整合包" : name;
    }
}
