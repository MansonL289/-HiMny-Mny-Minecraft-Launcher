using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>库中的一个版本项。</summary>
public sealed class VersionItem
{
    public string Name { get; set; } = "";
    public string Loader { get; set; } = "";

    /// <summary>根据加载器类型返回对应的 3D 图标 URI。</summary>
    public string IconUrl
    {
        get
        {
            string l = Loader ?? "";
            if (l.StartsWith("NeoForge", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_neoforge.png";
            if (l.StartsWith("Forge", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_forge.png";
            if (l.StartsWith("Fabric", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_fabric.png";
            if (l.StartsWith("Quilt", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_quilt.png";
            if (l.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_optifine.png";
            if (l.StartsWith("LiteLoader", StringComparison.OrdinalIgnoreCase)) return "ms-appx:///Assets/icon_liteloader.png";
            return "ms-appx:///Assets/icon_vanilla.png";
        }
    }
}

/// <summary>库列表项：一个 Mod（可能有多份不同加载器/版本的文件）。</summary>
public sealed class ModLibraryItem
{
    public ModLibraryEntry Entry { get; set; } = new();
    public string Name => Entry.Name;
    public string IconUrl => Entry.IconUrl;
    public string DetailText { get; set; } = "";
}

/// <summary>库里已下载的安装器（Forge/Fabric/NeoForge/Quilt 安装器 jar）。</summary>
public sealed class InstallerItem
{
    public string Name { get; set; } = "";
    /// <summary>加载器种类：forge / fabric / neoforge / quilt。</summary>
    public string Loader { get; set; } = "";
    public string Path { get; set; } = "";
    public string DetailText { get; set; } = "";

    public string IconUrl => Loader switch
    {
        "forge" => "ms-appx:///Assets/icon_forge.png",
        "fabric" => "ms-appx:///Assets/icon_fabric.png",
        "neoforge" => "ms-appx:///Assets/icon_neoforge.png",
        "quilt" => "ms-appx:///Assets/icon_quilt.png",
        "optifine" => "ms-appx:///Assets/icon_optifine.png",
        "liteloader" => "ms-appx:///Assets/icon_liteloader.png",
        _ => "ms-appx:///Assets/icon_vanilla.png",
    };
}

/// <summary>库里已下载的服务端 jar。</summary>
public sealed class ServerFileItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string DetailText { get; set; } = "";
}

/// <summary>库：已安装的版本、下载的 Mod、以及各加载器安装情况。</summary>
public sealed partial class ModLibraryPage : Page
{
    private readonly ObservableCollection<ModLibraryItem> _items = new();
    private readonly ObservableCollection<VersionItem> _versions = new();
    private readonly ObservableCollection<InstallerItem> _installers = new();
    private readonly ObservableCollection<ServerFileItem> _servers = new();
    private bool _busy;
    private int _refreshSeq;

    public ModLibraryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshVersions();
        RefreshMods();
        RefreshLoaders();
        RefreshServers();
    }

    // ===== 版本 =====

    private void RefreshVersions()
    {
        _versions.Clear();
        if (Directory.Exists(McPaths.VersionsDir))
        {
            var dirs = Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => Path.GetFileName(d)!)
                .OrderByDescending(n => n)
                .ToList();
            foreach (var dir in dirs)
            {
                var meta = VersionMeta.Load(dir);
                if (meta == null) continue;
                string loader = meta.LoaderType(dir);
                _versions.Add(new VersionItem
                {
                    Name = dir,
                    Loader = loader.Length > 0 ? loader : "原版",
                });
            }
        }
        VersionList.ItemsSource = _versions;
        VersionEmpty.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionList.Visibility = _versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        string path = VersionMeta.DirPath(name);
        if (Directory.Exists(path)) Process.Start("explorer.exe", path);
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除版本",
            Content = $"确定删除版本「{name}」吗？此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        try
        {
            string dir = VersionMeta.DirPath(name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            RefreshVersions();
            RefreshLoaders();
        }
        catch (Exception ex)
        {
            var tip = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除失败",
                Content = ex.Message,
                CloseButtonText = "好",
            };
            await tip.ShowAsync();
        }
    }

    // ===== MOD =====

    private async void RefreshMods()
    {
        int seq = ++_refreshSeq;
        var entries = ModLibrary.Load().OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var originals = entries.Select(e => e.Name).ToList();
        var known = originals.Select(ModNames.Zh).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            if (known[i].Length > 0)
            {
                entries[i].Name = $"{known[i]} ({originals[i]})";
            }
        }
        Rebind(entries, null);
        ModList.ItemsSource = _items;

        var zhTask = Task.Run(() => entries.ToDictionary(
            e => e.Id,
            e => e.Files.Any(f => ModLang.SupportsChinese(Path.Combine(ModLibrary.DirFor(e.Id), f.FileName))),
            StringComparer.OrdinalIgnoreCase));
        var namesTask = Translator.ToChineseAsync(
            Enumerable.Range(0, originals.Count).Select(i => known[i].Length > 0 ? "" : originals[i]).ToList(),
            System.Threading.CancellationToken.None);
        await Task.WhenAll(zhTask, namesTask);
        if (seq != _refreshSeq) return;
        var zh = zhTask.Result;
        for (int i = 0; i < entries.Count && i < namesTask.Result.Count; i++)
        {
            if (known[i].Length > 0) continue;
            string original = originals[i];
            if (namesTask.Result[i].Length > 0 && namesTask.Result[i] != original)
            {
                entries[i].Name = $"{namesTask.Result[i]} ({original})";
            }
        }
        Rebind(entries, zh);
    }

    private void Rebind(List<ModLibraryEntry> entries, Dictionary<string, bool>? zh)
    {
        _items.Clear();
        foreach (var entry in entries)
        {
            _items.Add(new ModLibraryItem
            {
                Entry = entry,
                DetailText = Detail(entry, zh != null && zh.GetValueOrDefault(entry.Id)),
            });
        }
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Detail(ModLibraryEntry entry, bool supportsChinese = false)
    {
        var flavors = entry.Files
            .Select(f => $"{ModFlow.LoaderName(f.Loader)} {f.McVersion}")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        string text = string.Join(" · ", flavors);
        if (supportsChinese) text += " · 支持中文";
        return string.IsNullOrEmpty(entry.Author) ? text : $"{entry.Author} · {text}";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibraryItem item } || _busy) return;
        if (DownloadManager.IsModBusy(item.Entry.Name)) return;

        var targets = await ModFlow.PickTargetsAsync(XamlRoot);
        if (targets == null || targets.Count == 0) return;

        _busy = true;
        var entry = item.Entry;
        _job = DownloadManager.StartMod(entry.Name, async (job, ct) =>
        {
            var reporter = new Progress<ModInstallProgress>(p =>
                DownloadManager.Report(job, p.Status, p.Fraction));
            var outcomes = await ModFlow.InstallAsync(entry, targets, reporter, ct);
            _outcomes = outcomes;
            int ok = outcomes.Count(o => o.Ok);
            DownloadManager.Report(job, $"已安装到 {ok} 个版本", 1);
        }, entry.Id, isInstall: true);
        DownloadManager.JobsChanged += OnJobChanged;
        OnJobChanged(null, EventArgs.Empty);
    }

    private DownloadJob? _job;
    private List<ModInstallOutcome>? _outcomes;

    private void OnJobChanged(object? sender, EventArgs e)
    {
        var job = _job;
        if (job == null) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!job.Done) return;
            DownloadManager.JobsChanged -= OnJobChanged;
            _busy = false;
            _job = null;
            var failed = _outcomes?.Where(o => !o.Ok).ToList() ?? new();
            _outcomes = null;
            if (failed.Count > 0 && XamlRoot != null)
            {
                await ModFlow.ShowMessageAsync(XamlRoot, job.VersionId,
                    string.Join("\n", failed.Select(f => $"{f.Dir}：{f.Text}")));
            }
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibraryItem item }) return;
        ModLibrary.Remove(item.Entry.Id);
        _items.Remove(item);
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===== 安装器：按加载器分类展示已安装该加载器的版本 =====

    private void RefreshLoaders()
    {
        string? tag = null;
        if (SideBar.SelectedItem is NavigationViewItem item)
        {
            tag = item.Tag?.ToString();
        }

        IEnumerable<VersionItem> filtered;
        if (string.IsNullOrEmpty(tag) || tag == "loaders")
        {
            // 父栏目「安装器」：展示所有安装了加载器的版本
            filtered = _versions.Where(v => v.Loader != "原版");
        }
        else
        {
            string loaderName = tag switch
            {
                "forge" => "Forge",
                "fabric" => "Fabric",
                "neoforge" => "NeoForge",
                "quilt" => "Quilt",
                "optifine" => "OptiFine",
                "liteloader" => "LiteLoader",
                _ => tag,
            };
            filtered = _versions.Where(v => v.Loader.StartsWith(loaderName, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        LoaderList.ItemsSource = list;
        bool empty = list.Count == 0;
        LoaderEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LoaderList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshInstallers(tag);
    }

    /// <summary>刷新已下载的安装器列表（父栏目显示全部，子栏目只显示对应加载器）。</summary>
    private void RefreshInstallers(string? tag)
    {
        _installers.Clear();
        string? filter = tag is "forge" or "fabric" or "neoforge" or "quilt"
            or "optifine" or "liteloader" ? tag : null;
        if (Directory.Exists(McPaths.InstallersDir))
        {
            foreach (string loaderDir in Directory.GetDirectories(McPaths.InstallersDir)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string loader = Path.GetFileName(loaderDir).ToLowerInvariant();
                if (filter != null && loader != filter) continue;
                foreach (string jar in Directory.GetFiles(loaderDir, "*.jar").OrderByDescending(f => f))
                {
                    _installers.Add(new InstallerItem
                    {
                        Name = Path.GetFileName(jar),
                        Loader = loader,
                        Path = jar,
                        DetailText = $"{ModFlow.LoaderName(loader)} 安装器 · 已放入库",
                    });
                }
            }
        }
        InstallerList.ItemsSource = _installers;
        bool any = _installers.Count > 0;
        InstallerHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        InstallerList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>安装已下载的安装器：选适配版本，用该加载器新建版本。</summary>
    private async void InstallInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstallerItem item }) return;
        string mc = ModFlow.InstallerMcFromFileName(item.Loader, item.Name);
        string? version = ModFlow.InstallerVersionFromFileName(item.Loader, item.Name);
        await ModFlow.InstallInstallerAsync(XamlRoot, item.Loader, mc, version, item.Path);
    }

    private void DeleteInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstallerItem item }) return;
        try
        {
            File.Delete(item.Path);
        }
        catch { /* 占用时留待下次覆盖 */ }
        RefreshInstallers(SideBar.SelectedItem is NavigationViewItem nav ? nav.Tag?.ToString() : null);
    }

    // ===== 服务端 =====

    /// <summary>刷新库里的服务端 jar 列表。</summary>
    private void RefreshServers()
    {
        _servers.Clear();
        if (Directory.Exists(McPaths.ServerDir))
        {
            foreach (string jar in Directory.GetFiles(McPaths.ServerDir, "*.jar").OrderByDescending(f => f))
            {
                long size = new FileInfo(jar).Length;
                _servers.Add(new ServerFileItem
                {
                    Name = Path.GetFileName(jar),
                    Path = jar,
                    DetailText = $"服务端 jar · {size / 1024.0 / 1024.0:F1} MB",
                });
            }
        }
        ServerList.ItemsSource = _servers;
        bool empty = _servers.Count == 0;
        ServerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ServerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenServerFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = McPaths.ServerDir;
        if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
    }

    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerFileItem item }) return;
        try
        {
            File.Delete(item.Path);
        }
        catch { /* 占用时留待下次覆盖 */ }
        RefreshServers();
    }

    private void SideBar_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item) return;
        string tag = item.Tag?.ToString() ?? "";

        switch (tag)
        {
            case "versions":
                TitleText.Text = "版本";
                VersionsPanel.Visibility = Visibility.Visible;
                ModsPanel.Visibility = Visibility.Collapsed;
                LoaderPanel.Visibility = Visibility.Collapsed;
                ServerPanel.Visibility = Visibility.Collapsed;
                break;
            case "mods":
                TitleText.Text = "MOD";
                VersionsPanel.Visibility = Visibility.Collapsed;
                ModsPanel.Visibility = Visibility.Visible;
                LoaderPanel.Visibility = Visibility.Collapsed;
                ServerPanel.Visibility = Visibility.Collapsed;
                break;
            case "servers":
                TitleText.Text = "服务端";
                VersionsPanel.Visibility = Visibility.Collapsed;
                ModsPanel.Visibility = Visibility.Collapsed;
                LoaderPanel.Visibility = Visibility.Collapsed;
                ServerPanel.Visibility = Visibility.Visible;
                RefreshServers();
                break;
            default:
                // loaders 父栏目或具体加载器子栏目
                TitleText.Text = tag == "loaders" ? "安装器" : (item.Content?.ToString() ?? "安装器");
                VersionsPanel.Visibility = Visibility.Collapsed;
                ModsPanel.Visibility = Visibility.Collapsed;
                LoaderPanel.Visibility = Visibility.Visible;
                ServerPanel.Visibility = Visibility.Collapsed;
                RefreshLoaders();
                break;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    /// <summary>供外部（下载完成横幅「查看」）切到对应栏目并刷新。</summary>
    public void ShowFor(DownloadJob? job)
    {
        string tag = job?.Kind switch
        {
            DownloadKind.Mod => "mods",
            DownloadKind.File => "servers",
            DownloadKind.Installer => job.InstallerLoader is "forge" or "fabric" or "neoforge"
                or "quilt" or "optifine" or "liteloader" ? job.InstallerLoader : "loaders",
            _ => "versions",
        };
        var item = FindItem(SideBar.MenuItems, tag);
        if (item != null)
        {
            SideBar.SelectedItem = item;
        }
        RefreshAll();
    }

    private static NavigationViewItem? FindItem(IList<object> items, string tag)
    {
        foreach (var obj in items)
        {
            if (obj is not NavigationViewItem item) continue;
            if (item.Tag as string == tag) return item;
            var child = FindItem(item.MenuItems, tag);
            if (child != null) return child;
        }
        return null;
    }
}
