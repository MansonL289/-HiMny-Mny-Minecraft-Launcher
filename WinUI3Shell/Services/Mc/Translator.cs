using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI3Shell.Services.Mc;

/// <summary>轻量在线翻译：把 Mod 简介翻成简体中文。
/// 主用 MyMemory 免费接口（无需密钥、国内可达），单条失败退回 Google 接口（走代理时可用），
/// 都不行就保持原文，绝不影响页面加载。</summary>
public static class Translator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // 翻译缓存：内存 + 磁盘 JSON（{"原文":"译文"}），启动后首次使用时读入，更新时写回
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static bool _loaded;
    private static string CachePath => Path.Combine(McPaths.GameDir, "cache", "translate.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            if (saved == null) return;
            foreach (var (key, value) in saved)
            {
                Cache[key] = Clean(value); // 老缓存里的标记/符号顺手清掉
            }
        }
        catch { /* 缓存坏了就重新积累 */ }
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var snapshot = Cache.ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot, CacheJsonOptions));
        }
        catch { /* 写不进去就下次再写 */ }
    }

    /// <summary>清除翻译缓存（内存 + 磁盘文件）。</summary>
    public static void ClearCache()
    {
        _loaded = true;
        Cache.Clear();
        try
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
        }
        catch { /* 占用时下次再清 */ }
    }

    /// <summary>磁盘上的翻译缓存文件大小（字节），供设置页显示。</summary>
    public static long CacheSizeBytes()
    {
        try
        {
            return File.Exists(CachePath) ? new FileInfo(CachePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>批量翻译；已含中文或翻译失败的条目保持原文。</summary>
    public static async Task<List<string>> ToChineseAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        EnsureLoaded();
        var result = new List<string>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            result.Add(texts[i] ?? "");
        }

        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i] ?? "";
            if (!NeedsTranslate(text)) continue;
            if (Cache.TryGetValue(text, out string? hit))
            {
                result[i] = hit;
            }
            else if (seen.Add(text))
            {
                unique.Add(text);
            }
        }
        if (unique.Count == 0) return result;

        // 6 路并发逐条请求；接口按条数计，一页结果一两个来回就完成
        var map = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(6);
        var tasks = unique.Select(async text =>
        {
            await gate.WaitAsync(ct);
            try
            {
                map[text] = await TranslateAsync(text, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 翻不了就保持原文
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { /* 个别失败忽略 */ }

        foreach (var (src, dst) in map)
        {
            Cache[src] = dst;
        }
        if (!map.IsEmpty)
        {
            SaveCache();
        }
        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i] ?? "";
            if (map.TryGetValue(text, out string? dst)) result[i] = dst;
        }
        return result;
    }

    /// <summary>单条翻译。</summary>
    public static async Task<string> ToChineseAsync(string text, CancellationToken ct)
        => (await ToChineseAsync(new[] { text }, ct))[0];

    // 主要是英文才翻（中文字符占比高就原样返回，避免把中文再翻一遍）
    private static bool NeedsTranslate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int cjk = 0, letters = 0;
        foreach (char ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
            else if (char.IsLetter(ch)) letters++;
        }
        return letters > 0 && cjk * 3 < letters;
    }

    /// <summary>MyMemory 单次请求的 q 上限约 500 字节，超长文本按句子切段后逐段翻译再拼回，
    /// 否则长简介会因超限直接失败（这是之前长简介一直显示英文的原因）。</summary>
    private static async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        var chunks = Chunk(text, 400);
        if (chunks.Count <= 1)
        {
            return await MyMemoryOrGoogleAsync(text, ct);
        }
        var parts = new List<string>(chunks.Count);
        foreach (string chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            parts.Add(await MyMemoryOrGoogleAsync(chunk, ct));
        }
        return string.Join(" ", parts);
    }

    private static async Task<string> MyMemoryOrGoogleAsync(string text, CancellationToken ct)
    {
        try
        {
            return await MyMemoryAsync(text, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await GoogleAsync(text, ct);
        }
    }

    /// <summary>按句子切分，单段不超过 max 字符；超长句子按空格硬切。</summary>
    private static List<string> Chunk(string text, int max)
    {
        var chunks = new List<string>();
        if (text.Length <= max) return chunks;
        var current = new StringBuilder();
        foreach (string sentence in Regex.Split(text, @"(?<=[.!?;:])\s+"))
        {
            string rest = sentence;
            while (rest.Length > max)
            {
                int cut = rest.LastIndexOf(' ', max);
                if (cut <= 0) cut = max;
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                chunks.Add(rest[..cut].Trim());
                rest = rest[cut..].TrimStart();
            }
            if (rest.Length == 0) continue;
            if (current.Length > 0 && current.Length + rest.Length + 1 > max)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(rest);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    // 接口偶尔把译文包上 XLIFF/HTML 标记（如 <g id="1070">★</g><g id="1073">钠</g>），
    // 剥掉标记、去掉首尾装饰符号（★、•、- 等）并还原实体，否则界面上会出现「★钠」
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Contains('<') && text.Contains('>'))
        {
            text = Regex.Replace(text, "<[^>]+>", "");
        }
        text = Regex.Replace(text, @"^[\s★☆●○◆◇■□▲△▼▽·•*#\-_]+", "");
        text = Regex.Replace(text, @"[\s★☆●○◆◇■□▲△▼▽·•*#\-_]+$", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    // MyMemory 免费接口：de 带上联系邮箱可把每日免费额度从 5000 字提到 50000 字
    private static async Task<string> MyMemoryAsync(string text, CancellationToken ct)
    {
        string url = "https://api.mymemory.translated.net/get?langpair=en|zh-CN&de=mnymc%40outlook.com&q="
            + Uri.EscapeDataString(text);
        string body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        string dst = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        if (dst.StartsWith("PLEASE SELECT", StringComparison.OrdinalIgnoreCase)
            || dst.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
            || dst.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("mymemory rejected");
        }
        dst = Clean(dst);
        return dst.Length == 0 ? throw new InvalidOperationException("mymemory empty") : dst;
    }

    // 兜底：Google 非官方接口（国内直连不通，走代理时才可用）
    private static async Task<string> GoogleAsync(string text, CancellationToken ct)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q="
            + Uri.EscapeDataString(text);
        string body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
        {
            sb.Append(seg[0].GetString());
        }
        string dst = Clean(sb.ToString());
        return dst.Length == 0 ? throw new InvalidOperationException("google empty") : dst;
    }
}
