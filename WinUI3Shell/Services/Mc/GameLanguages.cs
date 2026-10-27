using System.Globalization;
using System.IO.Compression;

namespace WinUI3Shell.Services.Mc;

/// <summary>游戏内可选的语言：只列出已安装版本里真实存在的语言（读客户端 jar / 资源索引），
/// 避免出现游戏根本没有的语言导致启动后被回退成英文。</summary>
public static class GameLanguages
{
    public sealed class Option
    {
        public required string Code { get; init; }
        public required string Display { get; init; }
    }

    private static readonly object Gate = new();
    private static List<Option>? _cache;

    /// <summary>版本变化（新装/删除）后调用，下次读取重新扫描。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    public static IReadOnlyList<Option> Available()
    {
        lock (Gate)
        {
            return _cache ??= Scan();
        }
    }

    /// <summary>解析最终写入 options.txt 的语言代码：优先用户选择，其次系统语言；
    /// 游戏没有该语言时依次退回同名语言、en_us。返回 null 表示一个可用语言都读不到。</summary>
    public static string? Resolve(string? preferred)
    {
        var options = Available();
        if (options.Count == 0)
        {
            string? raw = Normalize(preferred is null or "" ? CultureInfo.CurrentUICulture.Name : preferred);
            return raw;
        }

        var codes = options.Select(o => o.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string source = string.IsNullOrWhiteSpace(preferred) ? CultureInfo.CurrentUICulture.Name : preferred;
        string normalized = Normalize(source) ?? "";

        // 1. 完全匹配
        if (codes.Contains(normalized))
        {
            return FindCode(codes, normalized) ?? normalized;
        }

        // 2. 语言 + 地区（zh-Hans-CN -> zh_cn）
        string[] parts = normalized.Split('_');
        string lang = parts[0];
        if (lang == "zh")
        {
            bool traditional = source.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || parts.Contains("tw") || parts.Contains("hk") || parts.Contains("mo");
            string[] zhCandidates = traditional ? new[] { "zh_tw", "zh_hk", "zh_cn" } : new[] { "zh_cn", "zh_tw" };
            foreach (string code in zhCandidates)
            {
                string? hit = FindCode(codes, code);
                if (hit != null) return hit;
            }
        }
        if (parts.Length > 1)
        {
            string? regional = FindCode(codes, $"{lang}_{parts[^1]}");
            if (regional != null) return regional;
        }

        // 3. 同语言的任意地区
        string? sameLang = codes.FirstOrDefault(c => c.StartsWith(lang + "_", StringComparison.OrdinalIgnoreCase));
        if (sameLang != null) return sameLang;

        // 4. 英文兜底
        return FindCode(codes, "en_us") ?? options[0].Code;
    }

    private static string? FindCode(HashSet<string> codes, string code) =>
        codes.TryGetValue(code, out string? hit) ? hit : null;

    /// <summary>系统区域名统一成游戏代码格式：zh-Hans-CN -> zh_hans_cn。</summary>
    private static string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return null;
        string text = culture.Trim().Replace('-', '_').ToLowerInvariant();
        return text.Length == 0 ? null : text;
    }

    private static List<Option> Scan()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.GetDirectories(McPaths.VersionsDir))
            {
                string name = Path.GetFileName(dir);
                var meta = VersionMeta.Load(name);
                if (meta is null || !meta.IsComplete(name)) continue;
                ReadJarLanguages(meta.JarPath(name), codes);
                string? indexId = ReadAssetIndexId(meta.JsonPath(name));
                if (indexId != null) indexIds.Add(indexId);
            }
        }
        catch { /* 目录读不到就返回空列表 */ }

        // 只扫已装版本用到的资源索引；一个都没读到才退回全量扫描
        if (!ScanAssetIndexes(indexIds, codes) && indexIds.Count == 0)
        {
            ScanAssetIndexes(null, codes);
        }
        codes.Remove("deprecated");

        var options = new List<Option>(codes.Count);
        foreach (string code in codes)
        {
            options.Add(new Option { Code = code, Display = $"{NativeName(code)} ({code})" });
        }
        options.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        return options;
    }

    private static string? ReadAssetIndexId(string versionJsonPath)
    {
        try
        {
            if (!File.Exists(versionJsonPath)) return null;
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(versionJsonPath));
            string? id = json?["assetIndex"]?["id"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>客户端 jar 里的 assets/minecraft/lang/*.json（老版本为 lang/*.lang）。</summary>
    private static void ReadJarLanguages(string jarPath, HashSet<string> codes)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                string full = entry.FullName;
                string? code = null;
                if (full.StartsWith("assets/minecraft/lang/", StringComparison.OrdinalIgnoreCase)
                    && full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    code = full["assets/minecraft/lang/".Length..^".json".Length];
                }
                else if (full.StartsWith("lang/", StringComparison.OrdinalIgnoreCase)
                    && full.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
                {
                    code = full["lang/".Length..^".lang".Length];
                }
                if (!string.IsNullOrEmpty(code) && !code.Contains('/'))
                {
                    codes.Add(code.ToLowerInvariant());
                }
            }
        }
        catch { /* 单个 jar 读不了就跳过 */ }
    }

    /// <summary>资源索引里也记录了语言文件；ids 为 null 时扫描全部索引。</summary>
    private static bool ScanAssetIndexes(HashSet<string>? ids, HashSet<string> codes)
    {
        string dir = Path.Combine(McPaths.AssetsDir, "indexes");
        if (!Directory.Exists(dir)) return false;
        IEnumerable<string> files = ids is null
            ? Directory.EnumerateFiles(dir, "*.json")
            : ids.Select(id => Path.Combine(dir, id + ".json")).Where(File.Exists);
        bool any = false;
        foreach (string file in files)
        {
            any = true;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))?["objects"]?.AsObject();
                if (root is null) continue;
                foreach (var (key, _) in root)
                {
                    const string prefix = "minecraft/lang/";
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    codes.Add(key[prefix.Length..^".json".Length].ToLowerInvariant());
                }
            }
            catch { /* 坏索引跳过 */ }
        }
        return any;
    }

    private static string NativeName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code.Replace('_', '-'));
            return string.IsNullOrWhiteSpace(culture.NativeName) ? code : culture.NativeName;
        }
        catch
        {
            return code;
        }
    }
}
