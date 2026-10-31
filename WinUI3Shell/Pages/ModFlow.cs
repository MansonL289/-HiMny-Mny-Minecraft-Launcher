using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>实例下拉项（版本名 + 加载器 + MC 版本）。</summary>
public sealed class ModInstanceOption
{
    public string Dir { get; set; } = "";
    public string Display { get; set; } = "";
    public string McVersion { get; set; } = "";
    /// <summary>fabric / forge / neoforge / quilt，原版为空串。</summary>
    public string LoaderKind { get; set; } = "";
}

/// <summary>安装到某个版本的结果。</summary>
public sealed record ModInstallOutcome(string Dir, string Text, bool Ok);

/// <summary>MOD 公共流程：实例枚举、选版本弹窗、库 → 版本安装。</summary>
public static class ModFlow
{
    public static string LoaderName(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "optifine" => "OptiFine",
        "liteloader" => "LiteLoader",
        "" => "原版",
        _ => kind,
    };

    /// <summary>枚举已装好的版本（完整安装的那些）。</summary>
    public static List<ModInstanceOption> LoadInstances()
    {
        var list = new List<ModInstanceOption>();
        if (!Directory.Exists(McPaths.VersionsDir)) return list;
        foreach (string full in Directory.GetDirectories(McPaths.VersionsDir)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string dir = Path.GetFileName(full);
            if (string.IsNullOrEmpty(dir)) continue;
            var loaded = VersionMeta.Load(dir);
            if (loaded == null || !loaded.IsComplete(dir)) continue;
            string loader = loaded.LoaderType(dir);
            list.Add(new ModInstanceOption
            {
                Dir = dir,
                Display = loader.Length == 0 ? dir : $"{dir}（{LoaderName(loader)}）",
                McVersion = loaded.McVersion(dir),
                LoaderKind = loader,
            });
        }
        return list;
    }

    /// <summary>下载进库后的询问：是否立即安装到版本。</summary>
    public static async Task<bool> ConfirmInstallAsync(XamlRoot root, string title)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = "已加入 MOD 库，现在安装到版本？", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "安装到版本",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>弹窗多选目标版本；没有加载器的版本不可勾选。</summary>
    public static async Task<List<ModInstanceOption>?> PickTargetsAsync(XamlRoot root)
    {
        var all = LoadInstances();
        if (all.Count == 0)
        {
            await ShowMessageAsync(root, "安装到版本", "还没有已安装的版本。");
            return null;
        }
        if (!all.Any(o => o.LoaderKind.Length > 0))
        {
            await ShowMessageAsync(root, "安装到版本", "没有安装加载器的版本，先下载 Fabric / Forge / NeoForge 版本。");
            return null;
        }

        var panel = new StackPanel { Spacing = 2 };
        var boxes = new List<CheckBox>();
        foreach (var option in all)
        {
            var box = new CheckBox
            {
                Content = option.Display,
                IsEnabled = option.LoaderKind.Length > 0,
                Tag = option,
            };
            boxes.Add(box);
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = "安装到版本",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
            Content = new ScrollViewer { Content = panel, MaxHeight = 320 },
        };
        void Sync() => dialog.IsPrimaryButtonEnabled = boxes.Any(b => b.IsChecked == true);
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => Sync();
            box.Unchecked += (_, _) => Sync();
        }
        var usable = boxes.Where(b => b.IsEnabled).ToList();
        if (usable.Count == 1)
        {
            usable[0].IsChecked = true;
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return boxes
            .Where(b => b.IsChecked == true && b.Tag is ModInstanceOption { LoaderKind.Length: > 0 })
            .Select(b => (ModInstanceOption)b.Tag!)
            .ToList();
    }

    /// <summary>把库里的 Mod（含 required 依赖）安装到选中的版本；返回每个版本的结果。</summary>
    public static async Task<List<ModInstallOutcome>> InstallAsync(ModLibraryEntry entry,
        IReadOnlyList<ModInstanceOption> targets, IProgress<ModInstallProgress> progress, CancellationToken ct)
    {
        var outcomes = new List<ModInstallOutcome>();
        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[i];
            int current = i;
            var sub = new Progress<ModInstallProgress>(p => progress.Report(new ModInstallProgress(
                p.Status, (current + Math.Clamp(p.Fraction, 0, 1)) / targets.Count)));
            try
            {
                progress.Report(new ModInstallProgress($"安装到 {target.Dir}…", (double)i / targets.Count));
                var chosen = await ResolveTargetVersionAsync(entry, target, sub, ct);
                if (chosen == null)
                {
                    outcomes.Add(new ModInstallOutcome(target.Dir, "没有适配的文件", false));
                    continue;
                }
                var dependencies = await Modrinth.ResolveRequiredAsync(chosen, target.McVersion,
                    target.LoaderKind, null, ct);
                var all = new List<ModrinthVersion> { chosen };
                all.AddRange(dependencies);

                var local = BuildLocalSources(all, target);
                var meta = VersionMeta.Load(target.Dir);
                string gameDir = meta?.GameDir(target.Dir) ?? McPaths.GameDir;
                string modsDir = Path.Combine(gameDir, "mods");
                var result = await Task.Run(() => ModInstaller.InstallAsync(modsDir, target.LoaderKind,
                    target.McVersion, all, sub, ct, local), ct);
                outcomes.Add(new ModInstallOutcome(target.Dir, $"已安装 {result.Files.Count} 个文件", true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcomes.Add(new ModInstallOutcome(target.Dir, ex.Message, false));
            }
        }
        return outcomes;
    }

    /// <summary>优先用库里已存的对应版本；没有就取该版本+加载器的最新文件并下载进库。</summary>
    private static async Task<ModrinthVersion?> ResolveTargetVersionAsync(ModLibraryEntry entry,
        ModInstanceOption target, IProgress<ModInstallProgress> progress, CancellationToken ct)
    {
        var stored = ModLibrary.FindFile(entry, target.LoaderKind, target.McVersion);
        if (stored != null && !string.IsNullOrEmpty(stored.VersionId))
        {
            var version = await Modrinth.GetVersionAsync(stored.VersionId, ct);
            if (version != null
                && version.GameVersions.Contains(target.McVersion, StringComparer.OrdinalIgnoreCase)
                && version.Loaders.Contains(target.LoaderKind, StringComparer.OrdinalIgnoreCase)
                && version.PickJar() != null)
            {
                return version;
            }
        }

        var versions = await Modrinth.GetVersionsAsync(entry.Id, target.McVersion, target.LoaderKind, ct);
        var picked = versions.FirstOrDefault();
        var jar = picked?.PickJar();
        if (picked == null || jar == null) return null;

        progress.Report(new ModInstallProgress($"下载到库：{entry.Name}", 0));
        await Task.Run(() => ModInstaller.DownloadJarToAsync(jar, ModLibrary.DirFor(entry.Id), progress,
            $"下载到库：{entry.Name}", ct), ct);
        ModLibrary.UpsertFile(entry.Id, entry.Name, entry.Author, entry.IconUrl, new ModLibraryFile
        {
            Loader = target.LoaderKind,
            McVersion = target.McVersion,
            VersionId = picked.Id,
            FileName = jar.FileName,
            Size = jar.Size,
        });
        return picked;
    }

    /// <summary>模块 + 依赖里凡库里已有「同一个版本」的 jar，直接复用，不再下载。
    /// 只认 VersionId 一致的记录，避免把库里旧版本的 jar 当成新版本装进去。</summary>
    private static Dictionary<string, string> BuildLocalSources(IEnumerable<ModrinthVersion> versions,
        ModInstanceOption target)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = ModLibrary.Load();
        foreach (var version in versions)
        {
            var jar = version.PickJar();
            if (jar == null) continue;
            var entry = ModLibrary.Find(entries, version.ProjectId);
            if (entry == null) continue;
            var file = ModLibrary.FindFile(entry, target.LoaderKind, target.McVersion);
            if (file == null || !file.VersionId.Equals(version.Id, StringComparison.OrdinalIgnoreCase)) continue;
            string path = Path.Combine(ModLibrary.DirFor(entry.Id), file.FileName);
            if (File.Exists(path))
            {
                map[file.FileName] = path;
            }
        }
        return map;
    }

    /// <summary>下载完成提示里的「安装」：MOD 装到可用的版本，安装器给适配的版本装加载器。</summary>
    public static async Task InstallFromJobAsync(XamlRoot root, DownloadJob job)
    {
        if (job.IsMod)
        {
            await InstallModFromJobAsync(root, job);
        }
        else if (job.IsInstaller)
        {
            await InstallInstallerAsync(root, job.InstallerLoader, job.InstallerMc,
                job.InstallerVersion, job.InstallerPath);
        }
    }

    /// <summary>MOD 下载完成后安装：从库里取条目，选目标版本，后台安装。</summary>
    private static async Task InstallModFromJobAsync(XamlRoot root, DownloadJob job)
    {
        var entry = job.ModProjectId.Length > 0
            ? ModLibrary.Find(ModLibrary.Load(), job.ModProjectId)
            : ModLibrary.Load().FirstOrDefault(e => e.Name == job.VersionId);
        if (entry == null)
        {
            await ShowMessageAsync(root, "安装", "库里没有找到这个 Mod（可能已被删除）。");
            return;
        }
        var targets = await PickTargetsAsync(root);
        if (targets == null || targets.Count == 0) return;
        DownloadManager.StartMod(entry.Name, async (installJob, ct) =>
        {
            var reporter = new Progress<ModInstallProgress>(p =>
                DownloadManager.Report(installJob, p.Status, p.Fraction));
            var outcomes = await InstallAsync(entry, targets, reporter, ct);
            int ok = outcomes.Count(o => o.Ok);
            var failed = outcomes.Where(o => !o.Ok).ToList();
            DownloadManager.Report(installJob, failed.Count == 0
                ? $"已安装到 {ok} 个版本"
                : $"已安装到 {ok} 个版本，失败：{string.Join("；", failed.Select(f => $"{f.Dir}：{f.Text}"))}", 1);
        }, entry.Id, isInstall: true);
    }

    /// <summary>安装器安装：选适配版本，用该加载器新建一个版本（走后台下载任务）。</summary>
    /// <param name="installerPath">库里已下载的安装器 jar；OptiFine / LiteLoader 直接复用。</param>
    public static async Task InstallInstallerAsync(XamlRoot root, string loader, string mcVersion,
        string? loaderVersion, string? installerPath = null)
    {
        if (loader is "optifine" or "liteloader")
        {
            await InstallPatchAsync(root, loader, mcVersion, installerPath);
            return;
        }

        McAddon addon = loader switch
        {
            "fabric" => McAddon.Fabric,
            "forge" => McAddon.Forge,
            "neoforge" => McAddon.NeoForge,
            "quilt" => McAddon.Quilt,
            _ => McAddon.None,
        };
        if (addon == McAddon.None)
        {
            await ShowMessageAsync(root, "安装", "这个安装器暂时不支持在启动器内安装，请用 java -jar 手动运行。");
            return;
        }

        var all = LoadInstances();
        var candidates = all
            .Where(o => mcVersion.Length == 0
                || o.McVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            await ShowMessageAsync(root, "安装",
                mcVersion.Length == 0
                    ? "还没有已安装的版本，先下载一个游戏版本。"
                    : $"还没有 Minecraft {mcVersion} 的版本，先下载一个该版本。");
            return;
        }

        var targets = await PickVersionsAsync(root, "安装到版本", candidates);
        if (targets == null || targets.Count == 0) return;

        string loaderName = LoaderName(loader);
        foreach (var target in targets)
        {
            string name = UniqueVersionName($"{target.Dir}-{loaderName}");
            DownloadManager.Start(target.McVersion, name, AppSettings.DownloadMirror, addon, loaderVersion);
        }
        // 安装任务进下载列表，切到任务页可见进度
        App.MainWindow?.ShowDownloadTasks();
    }

    /// <summary>OptiFine / LiteLoader：把库里的安装器打到所选的原版版本上，生成 &lt;原版&gt;-OptiFine 之类的版本。</summary>
    private static async Task InstallPatchAsync(XamlRoot root, string loader, string mcVersion,
        string? installerPath)
    {
        string loaderName = LoaderName(loader);
        var candidates = LoadInstances()
            .Where(o => o.LoaderKind.Length == 0
                && (mcVersion.Length == 0
                    || o.McVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0)
        {
            await ShowMessageAsync(root, "安装", mcVersion.Length == 0
                ? "还没有已安装的原版版本，先下载一个原版。"
                : $"还没有 Minecraft {mcVersion} 的原版版本，先下载一个原版。");
            return;
        }

        var targets = await PickVersionsAsync(root, $"安装{loaderName}", candidates);
        if (targets == null || targets.Count == 0) return;

        OptiFine.OfVersion? of = null;
        LiteLoader.LlVersion? ll = null;
        if (loader == "optifine")
        {
            of = ParseOptiFine(installerPath);
            if (of == null)
            {
                await ShowMessageAsync(root, "安装", "安装器文件名无法识别 OptiFine 版本。");
                return;
            }
        }
        else
        {
            // LiteLoader 安装需要 tweakClass / 依赖列表，按库里的文件名回查元数据
            var list = await LiteLoader.ListAsync();
            string file = Path.GetFileName(installerPath ?? "");
            ll = list.FirstOrDefault(v => LiteLoaderFileName(v) == file);
            if (ll == null)
            {
                await ShowMessageAsync(root, "安装", "这个 LiteLoader 安装器版本已不在可用列表里，无法安装。");
                return;
            }
        }

        DownloadManager.StartMod($"{loaderName} · {mcVersion}", async (job, ct) =>
        {
            var progress = new Progress<McProgress>(p => DownloadManager.Report(job,
                p.TotalFiles > 0 ? $"{p.Phase} ({p.DoneFiles}/{p.TotalFiles})" : p.Phase,
                p.TotalFiles > 0 ? (double)p.DoneFiles / p.TotalFiles : 0));
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                string name = UniqueVersionName($"{target.Dir}-{loaderName}");
                if (loader == "optifine")
                {
                    await OptiFine.InstallAsync(target.Dir, target.McVersion, of!, name, progress, ct,
                        installerPath);
                }
                else
                {
                    await LiteLoader.InstallAsync(target.Dir, ll!, name, progress, ct, installerPath);
                }
            }
            DownloadManager.Report(job, $"已安装到 {targets.Count} 个版本", 1);
        }, isInstall: true, kind: DownloadKind.Installer);
    }

    /// <summary>生成不与其他版本/进行中的任务重名的版本名。</summary>
    private static string UniqueVersionName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Directory.Exists(VersionMeta.DirPath(name))
            || DownloadManager.IsNameOrVersionBusy(name, ""); i++)
        {
            name = $"{baseName}-{i}";
        }
        return name;
    }

    /// <summary>LiteLoader 安装器在库里的固定文件名。</summary>
    public static string LiteLoaderFileName(LiteLoader.LlVersion ll) =>
        $"liteloader-{ll.McVersion}-{ll.Version}.jar";

    /// <summary>OptiFine 安装器文件名 → (type, patch)，形如 OptiFine_1.20.1_HD_U_I6.jar。</summary>
    private static OptiFine.OfVersion? ParseOptiFine(string? installerPath)
    {
        if (installerPath == null) return null;
        string name = Path.GetFileName(installerPath);
        string stem = Path.GetFileNameWithoutExtension(name);
        var parts = stem.Split('_');
        if (parts.Length < 4
            || !parts[0].StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase)) return null;
        string type = string.Join('_', parts.Skip(2).Take(parts.Length - 3));
        return new OptiFine.OfVersion(type, parts[^1], name, "");
    }

    /// <summary>从安装器文件名推导绑定的游戏版本（Forge / OptiFine / LiteLoader 文件名自带 MC 版本）。</summary>
    public static string InstallerMcFromFileName(string loader, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split(loader is "optifine" or "liteloader" ? '_' : '-');
        if (loader.Equals("forge", StringComparison.OrdinalIgnoreCase))
        {
            // forge-<mc>-<version>-installer.jar
            return parts.Length >= 4 && parts[0].Equals("forge", StringComparison.OrdinalIgnoreCase)
                ? parts[1] : "";
        }
        if (loader.Equals("optifine", StringComparison.OrdinalIgnoreCase))
        {
            // OptiFine_<mc>_<type>_<patch>.jar
            return parts.Length >= 4
                && parts[0].StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase) ? parts[1] : "";
        }
        if (loader.Equals("liteloader", StringComparison.OrdinalIgnoreCase))
        {
            // liteloader-<mc>-<version>.jar
            return parts.Length >= 3
                && parts[0].Equals("liteloader", StringComparison.OrdinalIgnoreCase) ? parts[1] : "";
        }
        return "";
    }

    /// <summary>Forge 安装器文件名 → Forge 版本号（forge-&lt;mc&gt;-&lt;version&gt;-installer.jar）。</summary>
    public static string? InstallerVersionFromFileName(string loader, string fileName)
    {
        if (!loader.Equals("forge", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
        return parts.Length >= 4 && parts[0].Equals("forge", StringComparison.OrdinalIgnoreCase)
            ? string.Join('-', parts.Skip(2).Take(parts.Length - 3))
            : null;
    }

    /// <summary>弹窗多选目标版本（通用版，不做加载器限制）。</summary>
    private static async Task<List<ModInstanceOption>?> PickVersionsAsync(XamlRoot root, string title,
        List<ModInstanceOption> candidates)
    {
        var panel = new StackPanel { Spacing = 2 };
        var boxes = new List<CheckBox>();
        foreach (var option in candidates)
        {
            var box = new CheckBox { Content = option.Display, Tag = option };
            boxes.Add(box);
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
            Content = new ScrollViewer { Content = panel, MaxHeight = 320 },
        };
        void Sync() => dialog.IsPrimaryButtonEnabled = boxes.Any(b => b.IsChecked == true);
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => Sync();
            box.Unchecked += (_, _) => Sync();
        }
        if (boxes.Count == 1)
        {
            boxes[0].IsChecked = true;
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return boxes.Where(b => b.IsChecked == true).Select(b => (ModInstanceOption)b.Tag!).ToList();
    }

    public static async Task ShowMessageAsync(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }
}
