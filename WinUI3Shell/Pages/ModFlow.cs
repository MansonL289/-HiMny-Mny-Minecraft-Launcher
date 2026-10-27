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
                progress.Report(new ModInstallProgress($"安装到 {target.Dir}...", (double)i / targets.Count));
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
