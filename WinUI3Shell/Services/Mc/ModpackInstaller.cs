using System.IO.Compression;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>把 .mrpack 安装成一个独立版本：游戏本体 + 加载器 + 整合包文件 + overrides。</summary>
public static class ModpackInstaller
{
    public static async Task InstallAsync(ModpackInfo pack, string installName, string mirror,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
        // CurseForge 整合包必须先用 API 换真实下载地址，没 Key 就别开始下载本体了
        if (pack.IsCurseForge && !CurseForge.HasKey)
        {
            throw new InvalidOperationException("CurseForge 整合包需要在设置 → 下载 里填写 API Key");
        }

        progress?.Report(new McProgress("安装游戏本体", 0, 0, 0, 0));
        JsonObject versionJson = await Installer.PrepareAsync(pack.McVersion, mirror, 16, progress, ct, installName);

        // 整合包一律隔离，Mod/配置/存档都放在版本目录里，互不干扰
        var meta = VersionMeta.Load(installName) ?? new VersionMeta { Id = pack.McVersion };
        meta.Id = pack.McVersion;
        meta.IsolationMode = "on";
        meta.Save(installName);

        switch (pack.LoaderKind)
        {
            case "fabric":
                progress?.Report(new McProgress("安装 Fabric Loader", 0, 0, 0, 0));
                await LoaderInstaller.InstallFabricAsync(pack.McVersion, ct, installName, pack.LoaderVersion);
                break;
            case "forge":
                progress?.Report(new McProgress("安装 Forge", 0, 0, 0, 0));
                await ForgeInstaller.InstallForgeAsync(pack.McVersion, versionJson, mirror, progress, ct,
                    installName, pack.LoaderVersion);
                break;
            case "neoforge":
                progress?.Report(new McProgress("安装 NeoForge", 0, 0, 0, 0));
                await ForgeInstaller.InstallNeoForgeAsync(pack.McVersion, versionJson, mirror, progress, ct,
                    installName, pack.LoaderVersion);
                break;
            case "quilt":
                throw new NotSupportedException("暂不支持 Quilt 整合包，请使用 Fabric 版本。");
        }

        string dir = VersionMeta.DirPath(installName);
        Directory.CreateDirectory(Path.Combine(dir, "mods"));

        // 1) overrides + client-overrides 解压（client 覆盖优先）
        progress?.Report(new McProgress("解压整合包配置", 0, 0, 0, 0));
        await Task.Run(() => ExtractOverrides(pack.ZipPath, dir), ct);

        // 2) 下载 index 里的文件（Mod、资源包、配置等）
        if (pack.IsCurseForge)
        {
            progress?.Report(new McProgress("解析 CurseForge 文件", 0, 0, 0, 0));
            var cfFiles = await ResolveCurseForgeAsync(pack, ct);
            var cfTasks = cfFiles
                .Select(f => new McDownloadTask(f.Url, Path.Combine(dir, "mods", f.FileName), null, f.Size))
                .ToList();
            await Downloader.DownloadManyAsync(cfTasks, 8, "整合包文件", progress, ct);

            // CurseForge 的 jar 同样登记到 mods.json
            string cfModsDir = Path.Combine(dir, "mods");
            foreach (var file in cfFiles.Where(f => f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                ModRegistry.Upsert(cfModsDir, new ModRecord
                {
                    Id = "pack:" + Path.GetFileNameWithoutExtension(file.FileName),
                    Name = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
                    Loader = pack.LoaderKind,
                    McVersion = pack.McVersion,
                    File = file.FileName,
                    Enabled = true,
                });
            }
        }
        else
        {
            var tasks = pack.Files
                .Select(f => new McDownloadTask(f.Url, Path.Combine(dir, f.RelPath.Replace('/', '\\')), f.Sha1, f.Size))
                .ToList();
            await Downloader.DownloadManyAsync(tasks, 8, "整合包文件", progress, ct);

            // 3) mods 目录里的 jar 登记到 mods.json，版本设置里可直接启用/禁用
            string modsDir = Path.Combine(dir, "mods");
            foreach (var file in pack.Files.Where(f => f.RelPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                                                       && f.RelPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                string fileName = Path.GetFileName(file.RelPath);
                ModRegistry.Upsert(modsDir, new ModRecord
                {
                    Id = "pack:" + Path.GetFileNameWithoutExtension(fileName),
                    Name = fileName,
                    Loader = pack.LoaderKind,
                    McVersion = pack.McVersion,
                    File = fileName,
                    Enabled = true,
                });
            }
        }

        progress?.Report(new McProgress("整合包安装完成", 1, 1, 1, 1));
    }

    /// <summary>CurseForge manifest 只有 (projectID, fileID)，逐个查文件详情拿真实文件名与下载地址。</summary>
    private static async Task<List<CfResolved>> ResolveCurseForgeAsync(ModpackInfo pack, CancellationToken ct)
    {
        var pairs = pack.CfFiles;
        var resolved = new CfResolved?[pairs.Count];
        using var sem = new SemaphoreSlim(6);
        var tasks = pairs.Select((pair, index) => Task.Run(async () =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var file = await CurseForge.FileAsync(pair.ModId, pair.FileId, ct);
                if (file == null) return;
                string name = Path.GetFileName(file.FileName);
                if (string.IsNullOrEmpty(name)) return;
                resolved[index] = new CfResolved(name, file.DownloadUrl,
                    file.FileLength > 0 ? file.FileLength : null, file.DisplayName);
            }
            finally
            {
                sem.Release();
            }
        }, ct)).ToList();
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        var blocked = new List<string>();
        var missing = new List<string>();
        var result = new List<CfResolved>();
        for (int i = 0; i < pairs.Count; i++)
        {
            var item = resolved[i];
            if (item == null)
            {
                missing.Add($"#{pairs[i].ModId}/{pairs[i].FileId}");
                continue;
            }
            if (string.IsNullOrEmpty(item.Url))
            {
                blocked.Add(string.IsNullOrWhiteSpace(item.DisplayName) ? item.FileName : item.DisplayName);
                continue;
            }
            result.Add(item);
        }

        if (blocked.Count > 0)
        {
            string names = string.Join("、", blocked.Take(10));
            string more = blocked.Count > 10 ? $" 等 {blocked.Count} 个 Mod" : "";
            throw new InvalidOperationException(
                $"以下 Mod 的作者禁止第三方分发，无法自动下载：{names}{more}，请到 CurseForge 网页手动下载后放进 mods 文件夹。");
        }
        if (missing.Count > 0)
        {
            string names = string.Join("、", missing.Take(10));
            throw new InvalidOperationException($"有 {missing.Count} 个文件查不到 CurseForge 信息，无法安装：{names}");
        }
        return result;
    }

    /// <summary>解析后的 CurseForge 文件（真实文件名 + 下载地址）。</summary>
    private sealed record CfResolved(string FileName, string Url, long? Size, string DisplayName);

    /// <summary>解压 overrides/ 与 client-overrides/ 到版本目录。</summary>
    private static void ExtractOverrides(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        // 先 overrides 后 client-overrides，保证客户端文件覆盖通用文件
        foreach (string prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith('/')) continue;

                string rel = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.GetFullPath(Path.Combine(targetDir, rel));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // 防目录穿越

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
        }
    }
}
