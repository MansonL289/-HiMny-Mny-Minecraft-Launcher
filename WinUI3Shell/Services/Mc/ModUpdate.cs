using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI3Shell.Services.Mc;

/// <summary>一条可更新的 Mod 记录。</summary>
public sealed class ModUpdateInfo
{
    /// <summary>mods.json 里的显示名。</summary>
    public string ModName { get; init; } = "";
    /// <summary>本地 jar 文件名。</summary>
    public string FileName { get; init; } = "";
    /// <summary>本地 jar 的文件版本号（从 Modrinth 查不到时显示「未知」）。</summary>
    public string CurrentVersion { get; init; } = "";
    public string NewVersion { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    /// <summary>新 jar 文件名（从 url 取最后一段并解码）。</summary>
    public string NewFileName { get; init; } = "";
    public string SummaryText => $"v{CurrentVersion} → v{NewVersion}";
}

/// <summary>Mod 更新检查：用本地 jar 的 sha1 向 Modrinth 查询适配当前实例的新版本。</summary>
public static class ModUpdate
{
    private const string Base = "https://api.modrinth.com/v2";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0 (github.com/MansonL289)");
        return client;
    }

    /// <summary>用本地 jar 的 SHA1 去 Modrinth 查是否有适配该版本（MC 版本 + 加载器）的新版本。</summary>
    public static async Task<List<ModUpdateInfo>> CheckAsync(string modsDir, string mcVersion, string loader,
        CancellationToken ct = default)
    {
        var result = new List<ModUpdateInfo>();
        // 原版没有加载器，Modrinth 也无从匹配
        if (string.IsNullOrWhiteSpace(loader) || string.IsNullOrWhiteSpace(mcVersion)) return result;
        if (!Directory.Exists(modsDir)) return result;

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // sha1 → 本地 jar
        foreach (string file in Directory.GetFiles(modsDir, "*.jar"))
        {
            try
            {
                hashes[Sha1(file)] = file;
            }
            catch { /* 文件被占用 / 读取失败：本次跳过该 jar */ }
        }
        if (hashes.Count == 0) return result;

        var hashList = hashes.Keys.ToList();
        // 先查本地 jar 对应的当前版本，确认它确实来自 Modrinth
        var current = await PostVersionsAsync($"{Base}/version_files",
            new { hashes = hashList, algorithm = "sha1" }, ct);
        if (current.Count == 0) return result;

        // 再查同一批 hash 在当前 MC 版本 + 加载器下的最新版本
        var latest = await PostVersionsAsync($"{Base}/version_files/update",
            new
            {
                hashes = hashList,
                algorithm = "sha1",
                loaders = new[] { loader },
                game_versions = new[] { mcVersion },
            }, ct);
        if (latest.Count == 0) return result;

        var records = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in ModRegistry.Load(modsDir))
        {
            string bare = BareName(record.File);
            if (bare.Length > 0) records[bare] = record;
        }

        foreach (var (sha1, file) in hashes)
        {
            if (!current.TryGetValue(sha1, out var now)) continue; // 不是 Modrinth 来源的 Mod
            if (!latest.TryGetValue(sha1, out var next)) continue;
            if (now.Id.Equals(next.Id, StringComparison.OrdinalIgnoreCase)) continue; // 已是最新
            var jar = next.PickJar();
            if (jar == null || string.IsNullOrWhiteSpace(jar.Url)) continue;

            string fileName = Path.GetFileName(file);
            string modName = records.TryGetValue(fileName, out var record) && !string.IsNullOrWhiteSpace(record.Name)
                ? record.Name
                : fileName;
            result.Add(new ModUpdateInfo
            {
                ModName = modName,
                FileName = fileName,
                CurrentVersion = string.IsNullOrWhiteSpace(now.VersionNumber) ? "未知" : now.VersionNumber,
                NewVersion = string.IsNullOrWhiteSpace(next.VersionNumber) ? "未知" : next.VersionNumber,
                DownloadUrl = jar.Url,
                NewFileName = FileNameFromUrl(jar.Url, jar.FileName),
            });
        }
        return result;
    }

    /// <summary>下载新 jar 到 modsDir，删除旧 jar，更新 mods.json（保留 Enabled 状态）。</summary>
    public static async Task UpdateAsync(string modsDir, ModUpdateInfo update, IProgress<McProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.NewFileName))
        {
            throw new InvalidOperationException($"「{update.ModName}」缺少下载地址，无法更新");
        }
        Directory.CreateDirectory(modsDir);
        string oldPath = Path.Combine(modsDir, update.FileName);
        string newPath = Path.Combine(modsDir, update.NewFileName);
        bool sameName = oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase);
        // 同名时先下载到临时文件再替换，避免下载失败把原来的 jar 丢了
        string downloadPath = sameName
            ? Path.Combine(modsDir, ".mnymc-update-" + Guid.NewGuid().ToString("N") + ".jar")
            : newPath;
        try
        {
            await Downloader.DownloadManyAsync(
                new[] { new McDownloadTask(update.DownloadUrl, downloadPath) }, 1,
                $"更新 {update.ModName}", progress, ct);
            if (!downloadPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(downloadPath, newPath, true);
            }
            if (!sameName && File.Exists(oldPath)) File.Delete(oldPath);
        }
        finally
        {
            try
            {
                if (!downloadPath.Equals(newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(downloadPath))
                {
                    File.Delete(downloadPath);
                }
            }
            catch { /* 临时文件清理失败忽略 */ }
        }

        var oldRecord = ModRegistry.Load(modsDir)
            .FirstOrDefault(r => BareName(r.File).Equals(update.FileName, StringComparison.OrdinalIgnoreCase));
        ModRegistry.Remove(modsDir, update.FileName);
        ModRegistry.Upsert(modsDir, new ModRecord
        {
            Id = oldRecord?.Id ?? "local@" + update.NewFileName,
            Name = string.IsNullOrWhiteSpace(oldRecord?.Name) ? update.ModName : oldRecord!.Name,
            Loader = oldRecord?.Loader ?? "",
            McVersion = oldRecord?.McVersion ?? "",
            File = update.NewFileName,
            Enabled = oldRecord?.Enabled ?? true,
        });
    }

    /// <summary>POST 版本查询接口，返回 hash → version 字典；失败时抛中文异常。</summary>
    private static async Task<Dictionary<string, ModrinthVersion>> PostVersionsAsync(string url, object body,
        CancellationToken ct)
    {
        string json;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new InvalidOperationException("Modrinth 请求过于频繁（429），请稍后再试");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Modrinth 返回错误（{(int)response.StatusCode}），请稍后再试");
            }
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"检查 Mod 更新失败：{ex.Message}", ex);
        }

        var map = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion>>(json);
        return map == null
            ? new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModrinthVersion>(map, StringComparer.OrdinalIgnoreCase);
    }

    private static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static string FileNameFromUrl(string url, string fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return fallback;
        string name = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string BareName(string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;
}
