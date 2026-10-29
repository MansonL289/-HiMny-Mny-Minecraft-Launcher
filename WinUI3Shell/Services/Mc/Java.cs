using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace WinUI3Shell.Services.Mc;

/// <summary>
/// Java 查找与自动下载：
/// 已下载运行时 > 用户指定 > JAVA_HOME > PATH > 常见安装目录；
/// 没有满足主版本要求的 Java 时从 Adoptium 下载对应 JRE。
/// 老版本（launchwrapper / jre-legacy / 无 javaVersion 字段）必须用 Java 8：
/// launchwrapper 会把系统 ClassLoader 强转为 URLClassLoader，Java 9+ 必崩。
/// </summary>
public static class JavaService
{
    /// <summary>该版本可接受的 Java 主版本范围。老版本锁定 Java 8，新版按 javaVersion 要求起步。</summary>
    public static (int Min, int Max) RequiredRange(System.Text.Json.Nodes.JsonObject versionJson)
    {
        int major = versionJson["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;
        string component = versionJson["javaVersion"]?["component"]?.GetValue<string>() ?? "";
        bool legacy = major <= 8 || component == "jre-legacy"
            || versionJson["mainClass"]?.GetValue<string>() == "net.minecraft.launchwrapper.Launch";
        return legacy ? (8, 8) : (major, int.MaxValue);
    }

    /// <summary>找到主版本号在 [minMajor, maxMajor] 内的 Java；找不到就自动下载并返回路径。
    /// 设置里指定的全局默认 Java 版本合适时优先使用。</summary>
    public static async Task<string> EnsureAsync(int minMajor, int maxMajor,
        IProgress<McProgress>? progress, CancellationToken ct = default)
    {
        string global = AppSettings.GlobalJavaPath;
        if (!string.IsNullOrEmpty(global) && File.Exists(global))
        {
            int? major = await GetMajorAsync(global);
            if (major >= minMajor && major <= maxMajor) return global;
        }
        var found = await FindSuitableAsync(minMajor, maxMajor);
        if (found != null) return found;
        return await DownloadAsync(minMajor, progress, ct);
    }

    /// <summary>扫描本机所有可用 Java（下载的运行时 / JAVA_HOME / PATH / 常见安装目录）。</summary>
    public static async Task<List<(string Path, int Major)>> ScanAllAsync()
    {
        var result = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Candidates())
        {
            if (!seen.Add(candidate)) continue;
            int? major = await GetMajorAsync(candidate);
            if (major.HasValue) result.Add((candidate, major.Value));
        }
        return result;
    }

    /// <summary>遍历候选 java.exe，运行 -version 解析主版本号，返回第一个满足要求的。</summary>
    public static async Task<string?> FindSuitableAsync(int minMajor, int maxMajor = int.MaxValue)
    {
        foreach (var candidate in Candidates().Distinct())
        {
            int? major = await GetMajorAsync(candidate);
            if (major >= minMajor && major <= maxMajor) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 之前自动下载的运行时优先
        if (Directory.Exists(McPaths.RuntimesDir))
            foreach (var exe in Directory.EnumerateFiles(McPaths.RuntimesDir, "java.exe", SearchOption.AllDirectories))
                if (Path.GetFileName(Path.GetDirectoryName(exe)) == "bin")
                    yield return exe;

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            string p = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(p)) yield return p;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string p;
                try
                {
                    p = Path.Combine(dir.Trim(), "java.exe");
                }
                catch { continue; }
                if (File.Exists(p)) yield return p;
            }

        foreach (var root in new[]
        {
            @"C:\Program Files\Java", @"C:\Program Files (x86)\Java",
            @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu", @"C:\Program Files\Amazon Corretto",
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
                if (Path.GetFileName(Path.GetDirectoryName(exe)) == "bin")
                    yield return exe;
        }
    }

    /// <summary>运行 java -version 并解析主版本号（1.8 记作 8）。</summary>
    public static async Task<int?> GetMajorAsync(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            // 两个管道同时读，避免单向读满导致子进程阻塞
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(errTask, outTask);
            await p.WaitForExitAsync();
            string output = errTask.Result + outTask.Result;
            var m = Regex.Match(output, "version \"([^\"]+)\"");
            if (!m.Success) return null;
            string v = m.Groups[1].Value;
            if (v.StartsWith("1.")) v = v[2..]; // 1.8.0_xxx -> 8.0_xxx
            string num = v.Split('.', '-', '_', '+')[0];
            return int.TryParse(num, out int major) ? major : null;
        }
        catch { return null; }
    }

    /// <summary>从 Adoptium 下载指定主版本的 Windows x64 JRE 并解压到 runtimes/。</summary>
    public static async Task<string> DownloadAsync(int major,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
        string url = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse";
        string destDir = Path.Combine(McPaths.RuntimesDir, $"jre-{major}");
        string zipPath = Path.Combine(McPaths.RuntimesDir, $"jre-{major}.zip");
        Directory.CreateDirectory(McPaths.RuntimesDir);

        string phase = $"下载 Java {major}";
        progress?.Report(new McProgress(phase, 0, 0, 0, 0));
        string tmpZip = zipPath + ".part"; // 半成品走 .part，中断后由启动清理删掉
        using (var resp = await McHttp.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            long done = 0, lastReportMs = 0;
            var sw = Stopwatch.StartNew();
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tmpZip);
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (sw.ElapsedMilliseconds - lastReportMs > 100)
                {
                    lastReportMs = sw.ElapsedMilliseconds;
                    progress?.Report(new McProgress(phase, done, total, 0, 0));
                }
            }
        }
        File.Move(tmpZip, zipPath, true);

        progress?.Report(new McProgress($"解压 Java {major}", 0, 0, 0, 0));
        await Task.Run(() =>
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            ZipFile.ExtractToDirectory(zipPath, destDir);
            File.Delete(zipPath);
        }, ct);

        return Directory
            .EnumerateFiles(destDir, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin")
            ?? throw new FileNotFoundException("Java 解压后未找到 bin\\java.exe");
    }
}
