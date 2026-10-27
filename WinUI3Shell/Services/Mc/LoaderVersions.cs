using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace WinUI3Shell.Services.Mc;

/// <summary>一个可选的加载器构建：下拉框显示文本 + 传给安装器的版本号。</summary>
public sealed record LoaderBuild(string Display, string Version);

/// <summary>列出某 MC 版本可用的 Forge / Fabric / NeoForge 加载器版本（最新在前），供安装页手动选择。</summary>
public static class LoaderVersions
{
    public static async Task<List<LoaderBuild>> ForgeAsync(string mcVersion, string mirror, CancellationToken ct = default)
    {
        // BMCLAPI 聚合接口：该 MC 版本的全部 Forge 构建（含 build 号，数据最完整）
        try
        {
            var arr = JsonNode.Parse(await McHttp.Client.GetStringAsync(
                $"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}", ct))!.AsArray();
            return arr.Select(n => n!.AsObject())
                .OrderByDescending(o => o["build"]!.GetValue<long>())
                .Select(o => new LoaderBuild(
                    $"{o["version"]!.GetValue<string>()}（build {o["build"]!.GetValue<long>()}）",
                    o["version"]!.GetValue<string>()))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* 落到 maven 元数据 */ }

        // 回退：maven 元数据，按 MC 版本前缀过滤
        const string rel = "net/minecraftforge/forge/maven-metadata.xml";
        string? xml = await TryGetAsync(new[]
        {
            "https://files.minecraftforge.net/" + rel,
            "https://maven.minecraftforge.net/" + rel,
            "https://bmclapi2.bangbang93.com/maven/" + rel,
        }, ct);
        if (xml == null) return new();
        return VersionsFromMetadata(xml)
            .Where(v => v.StartsWith(mcVersion + "-", StringComparison.Ordinal))
            .Select(v => v[(mcVersion.Length + 1)..])
            .OrderByDescending(v => v, SegmentComparer.Instance)
            .Select(v => new LoaderBuild(v, v))
            .ToList();
    }

    public static async Task<List<LoaderBuild>> FabricAsync(string mcVersion, string mirror, CancellationToken ct = default)
    {
        // Fabric meta：该 MC 版本的 loader 列表，本身按新旧排序（最新在前）；BMCLAPI 有 fabric-meta 镜像
        string rel = $"v2/versions/loader/{mcVersion}";
        var urls = Mirror.PrefersBmclapi(mirror)
            ? new[] { $"https://bmclapi2.bangbang93.com/fabric-meta/{rel}", $"https://meta.fabricmc.net/{rel}" }
            : new[] { $"https://meta.fabricmc.net/{rel}", $"https://bmclapi2.bangbang93.com/fabric-meta/{rel}" };
        string? json = await TryGetAsync(urls, ct);
        if (json == null) return new();
        return JsonNode.Parse(json)!.AsArray()
            .Select(n => n!["loader"]!["version"]!.GetValue<string>())
            .Select(v => new LoaderBuild(v, v))
            .ToList();
    }

    public static async Task<List<LoaderBuild>> NeoForgeAsync(string mcVersion, string mirror, CancellationToken ct = default)
    {
        // 1.20.1：NeoForge 沿用 forge 构件名与 47.1.x 版本号；之后按 MC 次版本.补丁 命名
        bool legacy = mcVersion == "1.20.1";
        string artifact = legacy ? "forge" : "neoforge";
        string prefix = legacy ? "47.1." : NeoForgePrefix(mcVersion);
        string rel = $"net/neoforged/{artifact}/maven-metadata.xml";
        var urls = Mirror.PrefersBmclapi(mirror)
            ? new[] { $"https://bmclapi2.bangbang93.com/maven/{rel}", $"https://maven.neoforged.net/releases/{rel}" }
            : new[] { $"https://maven.neoforged.net/releases/{rel}", $"https://bmclapi2.bangbang93.com/maven/{rel}" };
        string? xml = await TryGetAsync(urls, ct);
        if (xml == null) return new();
        return VersionsFromMetadata(xml)
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(v => v, SegmentComparer.Instance)
            .Select(v => new LoaderBuild(v, v))
            .ToList();
    }

    /// <summary>1.21.1 → "21.1."，1.21 → "21.0."，1.20.4 → "20.4."。</summary>
    private static string NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length < 2 || parts[0] != "1") return mc + ".";
        return parts[1] + "." + (parts.Length > 2 ? parts[2] : "0") + ".";
    }

    private static async Task<string?> TryGetAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (string url in urls)
        {
            try { return await McHttp.Client.GetStringAsync(url, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return null;
    }

    private static IEnumerable<string> VersionsFromMetadata(string xml)
        => XDocument.Parse(xml).Root!.Element("versioning")!.Element("versions")!
            .Elements("version").Select(x => x.Value);

    /// <summary>按段数值比较版本号（非数字段按 0 计）。</summary>
    public sealed class SegmentComparer : IComparer<string>
    {
        public static readonly SegmentComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var pa = (x ?? "").Split('.', '-', '+');
            var pb = (y ?? "").Split('.', '-', '+');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                long va = i < pa.Length && long.TryParse(pa[i], out long a) ? a : 0;
                long vb = i < pb.Length && long.TryParse(pb[i], out long b) ? b : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }
    }
}
