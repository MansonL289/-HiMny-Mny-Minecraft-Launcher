using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace WinUI3Shell.Services.Mc;

/// <summary>
/// Forge / NeoForge 自动安装：
/// 1. 解析最新构建版本号 → 2. 下载安装器 jar → 3. 抽取自带 maven/ 依赖 + 下载其余依赖
/// → 4. 用 Java 子进程运行安装处理器（DOWNLOAD_MOJMAPS 走镜像原生下载，避免 java 直连 Mojang）
/// → 5. 内嵌 version.json 合并进已装版本 json（libraries 追加 / mainClass 覆盖 / arguments 追加）。
/// 旧格式（MC ≤ 1.12.2，无 processors）：抽取 universal jar + 合并 versionInfo。
/// </summary>
public static class ForgeInstaller
{
    /// <param name="loaderVersion">手动指定的 Forge 版本号；空则自动取最新构建。</param>
    public static Task InstallForgeAsync(string mcVersion, JsonObject versionJson, string mirror,
        IProgress<McProgress>? progress, CancellationToken ct, string? installName = null,
        string? loaderVersion = null) => InstallAsync(
        "Forge", mcVersion, versionJson, mirror, progress, ct, installName,
        async m =>
        {
            if (loaderVersion != null)
            {
                return loaderVersion;
            }
            if (m == Mirror.Bmclapi)
            {
                try
                {
                    // BMCLAPI 聚合接口：该 MC 版本的全部 Forge 构建，取 build 号最大者
                    var arr = JsonNode.Parse(await McHttp.Client.GetStringAsync(
                        $"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}", ct))!.AsArray();
                    if (arr.Count > 0)
                    {
                        return arr.Select(n => n!.AsObject())
                            .MaxBy(o => o["build"]!.GetValue<long>())!["version"]!.GetValue<string>();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* 落到官方元数据 */ }
            }
            string? xml = await TryGetStringAsync(new[]
            {
                "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
            }, ct);
            if (xml == null) return null;
            return MaxVersion(VersionsFromMetadata(xml)
                .Where(v => v.StartsWith(mcVersion + "-", StringComparison.Ordinal))
                .Select(v => v[(mcVersion.Length + 1)..]));
        },
        ver =>
        {
            string full = $"{mcVersion}-{ver}";
            return $"net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
        });

    /// <param name="loaderVersion">手动指定的 NeoForge 版本号；空则自动取最新构建。</param>
    public static Task InstallNeoForgeAsync(string mcVersion, JsonObject versionJson, string mirror,
        IProgress<McProgress>? progress, CancellationToken ct, string? installName = null,
        string? loaderVersion = null) => InstallAsync(
        "NeoForge", mcVersion, versionJson, mirror, progress, ct, installName,
        async m =>
        {
            // 1.20.1：NeoForge 沿用 forge 构件名与 47.1.x 版本号；之后按 MC 次版本.补丁 命名
            bool legacy = mcVersion == "1.20.1";
            string artifact = legacy ? "forge" : "neoforge";
            if (loaderVersion != null)
            {
                return artifact + ":" + loaderVersion;
            }
            string prefix = legacy ? "47.1." : NeoForgePrefix(mcVersion);
            string rel = $"net/neoforged/{artifact}/maven-metadata.xml";
            var urls = m == Mirror.Bmclapi
                ? new[] { $"https://bmclapi2.bangbang93.com/maven/{rel}", $"https://maven.neoforged.net/releases/{rel}" }
                : new[] { $"https://maven.neoforged.net/releases/{rel}", $"https://bmclapi2.bangbang93.com/maven/{rel}" };
            string? xml = await TryGetStringAsync(urls, ct);
            if (xml == null) return null;
            string? ver = MaxVersion(VersionsFromMetadata(xml)
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal)));
            return ver == null ? null : artifact + ":" + ver;
        },
        ver =>
        {
            var parts = ver.Split(':');
            return $"net/neoforged/{parts[0]}/{parts[1]}/{parts[0]}-{parts[1]}-installer.jar";
        });

    /// <summary>1.21.1 → "21.1."，1.21 → "21.0."，1.20.4 → "20.4."。</summary>
    private static string NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length < 2 || parts[0] != "1") return mc + ".";
        return parts[1] + "." + (parts.Length > 2 ? parts[2] : "0") + ".";
    }

    // ========== 主流程 ==========

    private static async Task InstallAsync(string loaderName, string mcVersion, JsonObject versionJson,
        string mirror, IProgress<McProgress>? progress, CancellationToken ct, string? installName,
        Func<string, Task<string?>> resolveVersion, Func<string, string> installerRel)
    {
        string dir = installName ?? mcVersion;
        progress?.Report(new McProgress($"查询 {loaderName} 版本", 0, 0, 0, 0));
        string? ver = await resolveVersion(mirror);
        if (ver == null)
        {
            throw new InvalidOperationException($"{loaderName} 没有适配 {mcVersion} 的版本");
        }

        // 下载安装器 jar（多仓库候选，任一成功即可）
        progress?.Report(new McProgress($"下载 {loaderName} 安装器", 0, 0, 0, 0));
        string rel = installerRel(ver);
        string installerPath = Path.Combine(Path.GetTempPath(), "mnymc-" + rel.Replace('/', '_'));
        await DownloadFirstAsync(
            RepoCandidates(rel, mirror).Select(u => new McDownloadTask(u, installerPath)).ToList(), ct);

        using var zip = ZipFile.OpenRead(installerPath);
        var profile = JsonNode.Parse(await ReadStringAsync(
            zip.GetEntry("install_profile.json")
            ?? throw new InvalidOperationException("安装器损坏：缺少 install_profile.json"), ct))!.AsObject();

        string tempDir = Path.Combine(Path.GetTempPath(), "mnymc-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            if (profile["versionInfo"] is JsonObject versionInfo)
            {
                // ===== 旧格式（MC ≤ 1.12.2）：抽取 universal，合并 versionInfo =====
                progress?.Report(new McProgress($"安装 {loaderName}（旧格式）", 0, 0, 0, 0));
                var install = profile["install"]?.AsObject()
                    ?? throw new InvalidOperationException("安装器损坏：缺少 install 段");
                string filePath = install["filePath"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("安装器损坏：缺少 filePath");
                string destRel = Libraries.MavenPath(install["path"]!.GetValue<string>());
                var entry = zip.GetEntry(filePath.Replace('\\', '/'))
                    ?? throw new InvalidOperationException("安装器损坏：缺少 " + filePath);
                string dest = Path.Combine(McPaths.LibrariesDir, destRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);

                progress?.Report(new McProgress("下载依赖库", 0, 0, 0, 0));
                await DownloadLegacyLibrariesAsync(versionInfo["libraries"]?.AsArray(), mirror, ct);
                MergeVersionJson(dir, mcVersion, versionInfo);
            }
            else
            {
                // ===== 新格式（MC 1.13+）：内嵌 version.json + 安装处理器 =====
                string jsonEntryName = (profile["json"]?.GetValue<string>() ?? "/version.json").TrimStart('/');
                var embed = JsonNode.Parse(await ReadStringAsync(
                    zip.GetEntry(jsonEntryName)
                    ?? throw new InvalidOperationException("安装器损坏：缺少 " + jsonEntryName), ct))!.AsObject();

                // 安装器自带的 maven/ 依赖直接抽取，能省一笔下载
                ExtractBundledLibraries(zip, profile["libraries"]?.AsArray());

                var libTasks = CollectLibs(profile["libraries"]?.AsArray(), mirror)
                    .Concat(CollectLibs(embed["libraries"]?.AsArray(), mirror)).ToList();
                await Downloader.DownloadManyAsync(libTasks, 8, $"{loaderName} 依赖库", progress, ct);

                await RunProcessorsAsync(profile, zip, mcVersion, dir, versionJson, installerPath,
                    mirror, tempDir, progress, ct);
                MergeVersionJson(dir, mcVersion, embed);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ========== 安装处理器 ==========

    private static async Task RunProcessorsAsync(JsonObject profile, ZipArchive zip, string mcVersion,
        string dir, JsonObject versionJson, string installerPath, string mirror, string tempDir,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
        var procs = profile["processors"]?.AsArray();
        if (procs == null || procs.Count == 0) return;

        int requiredJava = versionJson["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 17;
        progress?.Report(new McProgress($"准备 Java {requiredJava}", 0, 0, 0, 0));
        // Forge 安装器是普通 Java 程序，满足最低版本的任意 Java 即可运行
        string java = await JavaService.EnsureAsync(requiredJava, int.MaxValue, progress, ct);

        // 变量表：内置变量 + data 的 client 值（[坐标]→库路径、'x'→字面量、/路径→安装器内文件）
        var vars = new Dictionary<string, string>
        {
            ["ROOT"] = McPaths.GameDir,
            ["INSTALLER"] = installerPath,
            ["MINECRAFT_JAR"] = Path.Combine(McPaths.VersionDir(dir), mcVersion + ".jar"),
            ["LIBRARY_DIR"] = McPaths.LibrariesDir,
            ["SIDE"] = "client",
            ["MINECRAFT_VERSION"] = mcVersion,
        };
        if (profile["data"] is JsonObject data)
        {
            foreach (var kv in data)
            {
                string raw = kv.Value?["client"]?.GetValue<string>() ?? "";
                vars[kv.Key] = ResolveDataValue(raw, zip, tempDir);
            }
        }

        // 只跑客户端侧的处理器（sides 缺省 = 双侧）
        var clientProcs = procs.Select(p => p!.AsObject())
            .Where(p => p["sides"] is not JsonArray sides
                || sides.Any(x => x!.GetValue<string>() == "client"))
            .ToList();
        int done = 0;
        foreach (var proc in clientProcs)
        {
            done++;
            ct.ThrowIfCancellationRequested();

            // 字面量 [坐标] 参数是处理器输入，确保已下载
            foreach (var raw in proc["args"]?.AsArray() ?? new JsonArray())
            {
                string s = raw!.GetValue<string>();
                if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
                {
                    await EnsureCoordAsync(s[1..^1], mirror, ct);
                }
            }

            var args = proc["args"]?.AsArray()
                .Select(a => ParseLiteral(a!.GetValue<string>(), vars)).ToList() ?? new List<string>();

            // DOWNLOAD_MOJMAPS 用镜像原生下载替代：java 工具会直连 Mojang，镜像网络下必失败
            int ti = args.IndexOf("--task");
            if (ti >= 0 && ti + 1 < args.Count && args[ti + 1] == "DOWNLOAD_MOJMAPS")
            {
                int oi = args.IndexOf("--output");
                if (oi >= 0 && oi + 1 < args.Count)
                {
                    progress?.Report(new McProgress($"安装处理器 ({done}/{clientProcs.Count}): 下载官方映射表", 0, 0, 0, 0));
                    await DownloadMojmapsAsync(versionJson, args[oi + 1], mirror, ct);
                    continue;
                }
            }

            string jarCoord = proc["jar"]!.GetValue<string>();
            progress?.Report(new McProgress($"安装处理器 ({done}/{clientProcs.Count}): {jarCoord}", 0, 0, 0, 0));

            var cp = new List<string>();
            foreach (var c in proc["classpath"]!.AsArray())
            {
                cp.Add(await EnsureCoordAsync(c!.GetValue<string>(), mirror, ct));
            }
            string procJar = await EnsureCoordAsync(jarCoord, mirror, ct);
            cp.Add(procJar);
            string mainClass = ReadMainClass(procJar)
                ?? throw new InvalidOperationException($"处理器 {jarCoord} 缺少 Main-Class");

            var psi = new ProcessStartInfo(java)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = tempDir,
            };
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(string.Join(';', cp));
            psi.ArgumentList.Add(mainClass);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            // 两个管道并发读，避免缓冲区满导致死锁
            var readOut = p.StandardOutput.ReadToEndAsync(ct);
            var readErr = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch
            {
                try { p.Kill(); } catch { }
                throw;
            }
            await Task.WhenAll(readOut, readErr);
            if (p.ExitCode != 0)
            {
                string tail = readErr.Result + readOut.Result;
                tail = tail.Length > 400 ? tail[^400..] : tail;
                throw new InvalidOperationException($"安装处理器 {jarCoord} 执行失败（退出码 {p.ExitCode}）\n{tail}");
            }
        }

        // 带 _SHA 后缀的数据键对应处理器输出文件，校验存在性
        // （不校验 sha1：生成的 jar 压缩方式可能不同导致哈希不一致，官方安装器同样处理）
        foreach (var key in vars.Keys)
        {
            if (!key.EndsWith("_SHA", StringComparison.Ordinal)) continue;
            if (vars.TryGetValue(key[..^4], out var file) && !File.Exists(file))
            {
                throw new InvalidOperationException($"安装处理器未生成预期文件: {file}");
            }
        }
    }

    /// <summary>替换 DOWNLOAD_MOJMAPS 处理器：直接从版本 json 的 client_mappings 下载（走镜像）。</summary>
    private static async Task DownloadMojmapsAsync(JsonObject versionJson, string output, string mirror, CancellationToken ct)
    {
        var mapping = versionJson["downloads"]?["client_mappings"]?.AsObject();
        if (mapping?["url"] == null)
        {
            throw new InvalidOperationException("该版本没有官方映射表，无法安装加载器");
        }
        await Downloader.DownloadFileAsync(new McDownloadTask(
            Mirror.Rewrite(mapping["url"]!.GetValue<string>(), mirror), output,
            mapping["sha1"]?.GetValue<string>(), mapping["size"]?.GetValue<long>()), null, ct);
    }

    // ========== 合并版本 json ==========

    private static void MergeVersionJson(string dir, string mcVersion, JsonObject embed)
    {
        string jsonPath = Path.Combine(McPaths.VersionDir(dir), mcVersion + ".json");
        var baseJson = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();

        if (embed["mainClass"] != null)
        {
            baseJson["mainClass"] = embed["mainClass"]!.GetValue<string>();
        }

        if (embed["libraries"] is JsonArray newLibs && baseJson["libraries"] is JsonArray libs)
        {
            var existing = libs.Select(l => l!["name"]?.GetValue<string>()).ToHashSet();
            foreach (var lib in newLibs)
            {
                string? name = lib!["name"]?.GetValue<string>();
                if (name == null || !existing.Add(name)) continue;
                libs.Add(JsonNode.Parse(lib.ToJsonString()));
            }
        }

        if (embed["arguments"] is JsonObject ea)
        {
            if (baseJson["arguments"] is JsonObject ba)
            {
                foreach (string key in new[] { "jvm", "game" })
                {
                    if (ea[key] is not JsonArray extra) continue;
                    if (ba[key] is JsonArray target)
                    {
                        foreach (var item in extra)
                        {
                            target.Add(JsonNode.Parse(item!.ToJsonString()));
                        }
                    }
                    else
                    {
                        ba[key] = JsonNode.Parse(extra.ToJsonString());
                    }
                }
            }
            else
            {
                baseJson["arguments"] = JsonNode.Parse(ea.ToJsonString());
            }
        }

        // 旧格式：加载器的 minecraftArguments 是完整字符串，直接替换
        if (embed["minecraftArguments"] is JsonValue ma)
        {
            baseJson["minecraftArguments"] = ma.GetValue<string>();
        }

        File.WriteAllText(jsonPath, baseJson.ToJsonString());
    }

    // ========== 工具 ==========

    /// <summary>data 值解析：[maven坐标]→库文件路径；'x'→字面量；/路径→安装器内文件解压到临时目录。</summary>
    private static string ResolveDataValue(string raw, ZipArchive zip, string tempDir)
    {
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
        {
            string rel = Libraries.MavenPath(raw[1..^1]);
            return Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }
        if (raw.StartsWith('/'))
        {
            var entry = zip.GetEntry(raw.TrimStart('/'))
                ?? throw new InvalidOperationException("安装器损坏：缺少 " + raw);
            string dest = Path.Combine(tempDir, raw.TrimStart('/').Replace('/', '_'));
            entry.ExtractToFile(dest, true);
            return dest;
        }
        return raw;
    }

    /// <summary>处理器参数替换：整串 {KEY} / 'x' / [坐标] 优先，否则内嵌 {KEY} 替换。</summary>
    private static string ParseLiteral(string text, IReadOnlyDictionary<string, string> vars)
    {
        if (text.Length >= 2 && text[0] == '{' && text[^1] == '}')
        {
            string key = text[1..^1];
            return vars.TryGetValue(key, out var v)
                ? v
                : throw new InvalidOperationException($"安装器配置缺少变量: {key}");
        }
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text[1..^1];
        }
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
        {
            string rel = Libraries.MavenPath(text[1..^1]);
            return Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }
        var sb = new StringBuilder(text);
        foreach (var (k, v) in vars)
        {
            sb.Replace("{" + k + "}", v);
        }
        return sb.ToString();
    }

    /// <summary>把库数组包装成临时 version json 复用 Libraries.Collect（rules 过滤 + artifact URL + maven 回退）。</summary>
    private static List<McDownloadTask> CollectLibs(JsonArray? libs, string mirror)
    {
        if (libs == null) return new();
        var fake = new JsonObject { ["libraries"] = JsonNode.Parse(libs.ToJsonString()) };
        var (tasks, _, _) = Libraries.Collect(fake, mirror);
        return tasks;
    }

    /// <summary>安装器 jar 自带的 maven/ 依赖直接抽取到 libraries 目录。</summary>
    private static void ExtractBundledLibraries(ZipArchive zip, JsonArray? libs)
    {
        if (libs == null) return;
        foreach (var node in libs)
        {
            string rel = Libraries.MavenPath(node!["name"]?.GetValue<string>());
            if (rel.Length == 0) continue;
            var entry = zip.GetEntry("maven/" + rel);
            if (entry == null) continue;
            string dest = Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }

    /// <summary>旧格式库下载：有 url 用 url，否则按坐标遍历候选仓库。</summary>
    private static async Task DownloadLegacyLibrariesAsync(JsonArray? libs, string mirror, CancellationToken ct)
    {
        if (libs == null) return;
        var tasks = new List<McDownloadTask>();
        foreach (var node in libs)
        {
            var lib = node!.AsObject();
            if (!McRules.Allow(lib["rules"]?.AsArray())) continue;
            string? name = lib["name"]?.GetValue<string>();
            string rel = Libraries.MavenPath(name);
            if (rel.Length == 0) continue;
            string? url = lib["url"]?.GetValue<string>();
            if (url != null)
            {
                string dest = Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                tasks.Add(new McDownloadTask(Mirror.Rewrite(url.TrimEnd('/') + "/" + rel, mirror), dest));
            }
            else if (name != null)
            {
                await EnsureCoordAsync(name, mirror, ct);
            }
        }
        await Downloader.DownloadManyAsync(tasks, 8, "依赖库", null, ct);
    }

    /// <summary>确保 maven 坐标对应的文件在 libraries 目录，缺失则按候选仓库顺序下载。</summary>
    private static async Task<string> EnsureCoordAsync(string coord, string mirror, CancellationToken ct)
    {
        string rel = Libraries.MavenPath(coord);
        if (rel.Length == 0)
        {
            throw new InvalidOperationException($"无法解析 maven 坐标: {coord}");
        }
        string dest = Path.Combine(McPaths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest)) return dest;
        await DownloadFirstAsync(
            RepoCandidates(rel, mirror).Select(u => new McDownloadTask(u, dest)).ToList(), ct);
        return dest;
    }

    /// <summary>maven 坐标的候选下载地址：镜像优先还是官方优先取决于设置，中央仓库与 libraries.minecraft.net 兜底。</summary>
    private static IEnumerable<string> RepoCandidates(string rel, string mirror)
    {
        string bmcl = "https://bmclapi2.bangbang93.com/maven/" + rel;
        string[] official =
        {
            "https://maven.minecraftforge.net/" + rel,
            "https://maven.neoforged.net/releases/" + rel,
            "https://repo.maven.apache.org/maven2/" + rel,
            "https://libraries.minecraft.net/" + rel,
        };
        return mirror == Mirror.Bmclapi
            ? new[] { bmcl }.Concat(official)
            : official.Append(bmcl);
    }

    private static async Task DownloadFirstAsync(IReadOnlyList<McDownloadTask> candidates, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var t in candidates)
        {
            try
            {
                await Downloader.DownloadFileAsync(t, null, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw new HttpRequestException($"所有仓库都下载失败: {candidates[0].Dest}", last);
    }

    /// <summary>读取 jar 清单里的 Main-Class（处理续行）。</summary>
    private static string? ReadMainClass(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = zip.GetEntry("META-INF/MANIFEST.MF");
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        var sb = new StringBuilder();
        bool inMainClass = false;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("Main-Class:", StringComparison.Ordinal))
            {
                inMainClass = true;
                sb.Clear().Append(line["Main-Class:".Length..].Trim());
            }
            else if (inMainClass && line.StartsWith(' '))
            {
                sb.Append(line.Trim());
            }
            else if (inMainClass)
            {
                break;
            }
        }
        string result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static async Task<string> ReadStringAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var reader = new StreamReader(s, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<string?> TryGetStringAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            try { return await McHttp.Client.GetStringAsync(url, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return null;
    }

    private static IEnumerable<string> VersionsFromMetadata(string xml)
        => XDocument.Parse(xml).Root!.Element("versioning")!.Element("versions")!
            .Elements("version").Select(x => x.Value);

    private static string? MaxVersion(IEnumerable<string> versions)
    {
        string? best = null;
        foreach (var v in versions)
        {
            if (best == null || CompareVersions(v, best) > 0) best = v;
        }
        return best;
    }

    /// <summary>按段数值比较版本号（非数字段按 0 计）。</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.', '-', '+');
        var pb = b.Split('.', '-', '+');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long va = i < pa.Length && long.TryParse(pa[i], out var x) ? x : 0;
            long vb = i < pb.Length && long.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
