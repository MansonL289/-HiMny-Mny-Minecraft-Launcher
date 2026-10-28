using System.IO.Compression;
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

/// <summary>解析后的整合包信息（Modrinth .mrpack）。</summary>
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

/// <summary>读取 .mrpack 的 modrinth.index.json。</summary>
public static class Modpack
{
    public static ModpackInfo Load(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("modrinth.index.json");
        if (entry == null)
        {
            bool curseForge = zip.GetEntry("manifest.json") != null;
            throw new InvalidDataException(curseForge
                ? "这是 CurseForge 整合包（manifest.json），需要 CurseForge API Key，目前只支持 Modrinth 的 .mrpack。"
                : "压缩包里没有 modrinth.index.json，不是 Modrinth 整合包。");
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
