using System.IO.Compression;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>把 .mrpack 安装成一个独立版本：游戏本体 + 加载器 + 整合包文件 + overrides。</summary>
public static class ModpackInstaller
{
    public static async Task InstallAsync(ModpackInfo pack, string installName, string mirror,
        IProgress<McProgress>? progress, CancellationToken ct)
    {
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

        progress?.Report(new McProgress("整合包安装完成", 1, 1, 1, 1));
    }

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
