using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI3Shell.Services.Mc;

/// <summary>mods.json 里的一条安装记录。</summary>
public sealed class ModRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Loader { get; set; } = "";
    public string McVersion { get; set; } = "";
    /// <summary>磁盘上的实际文件名（禁用时带 .disabled 后缀）。</summary>
    public string File { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>mods/mods.json 的读写。</summary>
public static class ModRegistry
{
    public const string MetaFileName = "mods.json";

    /// <summary>与文档一致：字段名用小驼峰（id / name / loader / mcVersion / file / enabled）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(string modsDir) => Path.Combine(modsDir, MetaFileName);

    public static List<ModRecord> Load(string modsDir)
    {
        try
        {
            string path = PathFor(modsDir);
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<ModRecord>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Save(string modsDir, List<ModRecord> records)
    {
        Directory.CreateDirectory(modsDir);
        File.WriteAllText(PathFor(modsDir), JsonSerializer.Serialize(records, JsonOptions));
    }

    public static void Upsert(string modsDir, ModRecord record)
    {
        var list = Load(modsDir);
        list.RemoveAll(r => r.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        list.Add(record);
        Save(modsDir, list);
    }

    /// <summary>删除某文件对应的记录（兼容按禁用前文件名匹配）。</summary>
    public static void Remove(string modsDir, string fileName)
    {
        var list = Load(modsDir);
        string bare = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;
        if (list.RemoveAll(r => r.File.Equals(fileName, StringComparison.OrdinalIgnoreCase)
            || r.File.Equals(bare, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save(modsDir, list);
        }
    }

    public static void SetEnabled(string modsDir, string fileName, string newFile, bool enabled)
    {
        var list = Load(modsDir);
        var record = list.FirstOrDefault(r => r.File.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (record == null) return;
        record.File = newFile;
        record.Enabled = enabled;
        Save(modsDir, list);
    }

    /// <summary>移除同项目的旧版本记录，返回旧文件名（用于随后删除旧 jar）。</summary>
    public static List<string> RemoveProject(string modsDir, string projectId)
    {
        var list = Load(modsDir);
        var removed = list.Where(r => ProjectPart(r.Id).Equals(projectId, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.File).ToList();
        if (removed.Count > 0)
        {
            list.RemoveAll(r => ProjectPart(r.Id).Equals(projectId, StringComparison.OrdinalIgnoreCase));
            Save(modsDir, list);
        }
        return removed;
    }

    private static string ProjectPart(string id)
    {
        int index = id.IndexOf('@');
        return index > 0 ? id[..index] : id;
    }
}

/// <summary>Mod 安装进度：状态文本 + 0~1 比例。</summary>
public sealed record ModInstallProgress(string Status, double Fraction);

/// <summary>安装结果。</summary>
public sealed record ModInstallResult(IReadOnlyList<string> Files);

/// <summary>Mod 下载与安装：临时目录下载 → 校验 → 原样拷入 mods/ → 写 mods.json。</summary>
public static class ModInstaller
{
    private static async Task<string> DownloadToTempAsync(ModrinthFile file,
        IProgress<ModInstallProgress>? progress, string status, double baseFraction, double span, CancellationToken ct)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("文件来源不是 Modrinth 官方 CDN，已拒绝下载");
        }
        if (!file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不是 jar 文件：{file.FileName}");
        }
        if (file.Size <= 0)
        {
            throw new InvalidOperationException($"文件大小异常：{file.FileName}");
        }

        string temp = Path.Combine(Path.GetTempPath(),
            "mnymc-mod-" + Guid.NewGuid().ToString("N") + ".jar");
        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadWithResumeAsync(file.Url, temp, file.Size,
                        fraction => progress?.Report(new ModInstallProgress(status, baseFraction + span * fraction)), ct);
                    if (!Verify(temp, file.Size, Sha1(file)))
                    {
                        File.Delete(temp);
                        throw new IOException("文件校验失败（大小或 sha1 不匹配）");
                    }
                    return temp;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (attempt < 3)
                {
                    // 保留已下载部分，下次带 Range 从断点续传
                }
            }
            throw new IOException($"下载失败：{file.FileName}");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task DownloadWithResumeAsync(string url, string dest, long expectedSize,
        Action<double> onFraction, CancellationToken ct)
    {
        long existing = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        if (existing >= expectedSize)
        {
            TryDelete(dest);
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }
        using var response = await McHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            response.EnsureSuccessStatusCode();
            existing = 0;
        }
        long total = expectedSize > 0 ? expectedSize : (response.Content.Headers.ContentLength ?? 0) + existing;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(dest, resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            int n;
            long done = existing;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0)
                {
                    onFraction(Math.Clamp((double)done / total, 0, 1));
                }
            }
        }
    }

    private static bool Verify(string path, long expectedSize, string? expectedSha1)
    {
        if (!File.Exists(path)) return false;
        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize) return false;
        return expectedSha1 == null || Sha1(path).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Sha1(ModrinthFile file) =>
        file.Hashes != null && file.Hashes.TryGetValue("sha1", out var sha1) ? sha1 : null;

    private static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>把一份 jar 下载进 MOD 库目录（校验后原名落盘），返回本地路径。</summary>
    public static async Task<string> DownloadJarToAsync(ModrinthFile file, string destDir,
        IProgress<ModInstallProgress>? progress, string status, CancellationToken ct)
    {
        string temp = await DownloadToTempAsync(file, progress, status, 0, 1, ct);
        try
        {
            Directory.CreateDirectory(destDir);
            string dest = Path.Combine(destDir, file.FileName);
            File.Move(temp, dest, true);
            return dest;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>按顺序下载并安装（第一个是主 Mod，其余是 required 依赖）。
    /// localSources 按文件名提供库里已有的 jar，命中时直接复制，省去重复下载。</summary>
    public static async Task<ModInstallResult> InstallAsync(string modsDir, string loaderKind, string mcVersion,
        IReadOnlyList<ModrinthVersion> versions, IProgress<ModInstallProgress>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? localSources = null)
    {
        Directory.CreateDirectory(modsDir);
        var installed = new List<string>();
        for (int i = 0; i < versions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var version = versions[i];
            var file = version.PickJar()
                ?? throw new InvalidOperationException($"「{version.Name}」没有可用的 jar 文件");
            string status = $"下载 {version.Name} ({i + 1}/{versions.Count})";
            progress?.Report(new ModInstallProgress(status, (double)i / versions.Count));

            bool fromLibrary = false;
            string localPath = "";
            if (localSources != null && localSources.TryGetValue(file.FileName, out var found)
                && File.Exists(found))
            {
                localPath = found;
                fromLibrary = true;
            }
            string temp = fromLibrary
                ? "" : await DownloadToTempAsync(file, progress, status, (double)i / versions.Count, 1.0 / versions.Count, ct);
            try
            {
                string source = fromLibrary ? localPath : temp;
                string dest = Path.Combine(modsDir, file.FileName);
                // 同项目旧版本直接替换，避免同一个 Mod 装出两份
                foreach (string old in ModRegistry.RemoveProject(modsDir, version.ProjectId))
                {
                    if (!old.Equals(file.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(Path.Combine(modsDir, old));
                    }
                }
                File.Copy(source, dest, true); // 不改名、不解压
                installed.Add(file.FileName);
                ModRegistry.Upsert(modsDir, new ModRecord
                {
                    Id = $"{version.ProjectId}@{version.Id}",
                    Name = version.Name,
                    Loader = loaderKind,
                    McVersion = mcVersion,
                    File = file.FileName,
                    Enabled = true,
                });
            }
            finally
            {
                if (!fromLibrary) TryDelete(temp);
            }
        }
        progress?.Report(new ModInstallProgress("安装完成", 1));
        return new ModInstallResult(installed);
    }
}
