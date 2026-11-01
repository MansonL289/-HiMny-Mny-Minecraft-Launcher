using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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
    /// <summary>已用这个安装器装出的版本名；空 = 还没安装到任何版本。</summary>
    public string InstalledVersion { get; set; } = "";

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

    /// <summary>版本分组：原版一栏，各加载器各一栏（按此顺序排列）。</summary>
    private static readonly (string Key, string Title)[] VersionGroupDefs =
    {
        ("vanilla", "原版游戏"),
        ("forge", "Forge"),
        ("neoforge", "NeoForge"),
        ("fabric", "Fabric"),
        ("quilt", "Quilt"),
        ("optifine", "OptiFine"),
        ("liteloader", "LiteLoader"),
        ("other", "其他"),
        ("incomplete", "未完成安装"),
    };

    /// <summary>把「加载器显示文本」归类到分组键：原版 / forge / neoforge / fabric / quilt / optifine / liteloader / other。</summary>
    private static string LoaderKey(string loader)
    {
        if (loader == "未完成") return "incomplete";
        if (loader.Length == 0 || loader == "原版") return "vanilla";
        if (loader.StartsWith("NeoForge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (loader.StartsWith("Forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        if (loader.StartsWith("Fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (loader.StartsWith("Quilt", StringComparison.OrdinalIgnoreCase)) return "quilt";
        if (loader.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase)) return "optifine";
        if (loader.StartsWith("LiteLoader", StringComparison.OrdinalIgnoreCase)) return "liteloader";
        return "other";
    }

    /// <summary>分组图标（安装器/版本共用）。</summary>
    private static string? LoaderIcon(string key) => key switch
    {
        "vanilla" => "ms-appx:///Assets/icon_vanilla.png",
        "forge" => "ms-appx:///Assets/icon_forge.png",
        "fabric" => "ms-appx:///Assets/icon_fabric.png",
        "neoforge" => "ms-appx:///Assets/icon_neoforge.png",
        "quilt" => "ms-appx:///Assets/icon_quilt.png",
        "optifine" => "ms-appx:///Assets/icon_optifine.png",
        "liteloader" => "ms-appx:///Assets/icon_liteloader.png",
        _ => null,
    };

    /// <summary>生成一栏（可展开/收起的下拉栏），内容用列表项模板渲染。</summary>
    private FrameworkElement BuildGroup(string title, string? iconKey, int count, object itemsSource,
        string templateKey)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string? icon = iconKey == null ? null : LoaderIcon(iconKey);
        if (icon != null)
        {
            header.Children.Add(new Image
            {
                Width = 20,
                Height = 20,
                Source = new BitmapImage(new Uri(icon)),
            });
        }
        header.Children.Add(new TextBlock
        {
            Text = $"{title} ({count})",
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Expander
        {
            Header = header,
            Content = new ItemsControl
            {
                ItemTemplate = (DataTemplate)Resources[templateKey],
                ItemsSource = itemsSource,
            },
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private void RefreshVersions()
    {
        _versions.Clear();
        if (Directory.Exists(McPaths.VersionsDir))
        {
            var dirs = Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => Path.GetFileName(d)!)
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var dir in dirs)
            {
                var meta = VersionMeta.Load(dir);
                if (meta == null) continue;
                // 用 DetectLoader 才能识别 OptiFine / LiteLoader（LoaderType 只返回 4 种主流加载器）
                string loader = meta.DetectLoader(dir);
                bool complete = meta.IsComplete(dir);
                _versions.Add(new VersionItem
                {
                    Name = dir,
                    Loader = complete ? (loader.Length > 0 ? loader : "原版") : "未完成",
                });
            }
        }

        VersionGroups.Children.Clear();
        foreach (var (key, title) in VersionGroupDefs)
        {
            var items = _versions.Where(v => LoaderKey(v.Loader) == key).ToList();
            if (items.Count == 0) continue;
            VersionGroups.Children.Add(BuildGroup(title, key, items.Count, items, "VersionItemTemplate"));
        }

        VersionEmpty.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        string? tag = SideBar.SelectedItem is NavigationViewItem item ? item.Tag?.ToString() : null;
        RefreshInstallers(tag);

        // 已安装 = 已经用这个安装器装出过版本；未安装 = 还没装过
        var installed = _installers.Where(i => i.InstalledVersion.Length > 0).ToList();
        var pending = _installers.Where(i => i.InstalledVersion.Length == 0).ToList();

        LoaderGroups.Children.Clear();
        if (installed.Count > 0)
        {
            LoaderGroups.Children.Add(BuildGroup("已安装", null, installed.Count,
                installed, "InstallerItemTemplate"));
        }
        if (pending.Count > 0)
        {
            LoaderGroups.Children.Add(BuildGroup("未安装", null, pending.Count,
                pending, "InstallerItemTemplate"));
        }

        LoaderEmpty.Visibility = _installers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>刷新已下载的安装器列表（父栏目显示全部，子栏目只显示对应加载器），并标出是否已装出过版本。</summary>
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
                foreach (string jar in Directory.GetFiles(loaderDir, "*.jar")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(jar);
                    string installedAs = FindInstalledVersion(loader, fileName);
                    _installers.Add(new InstallerItem
                    {
                        Name = fileName,
                        Loader = loader,
                        Path = jar,
                        InstalledVersion = installedAs,
                        DetailText = installedAs.Length > 0
                            ? $"{ModFlow.LoaderName(loader)} 安装器 · 已安装到「{installedAs}」"
                            : $"{ModFlow.LoaderName(loader)} 安装器 · 未安装到任何版本",
                    });
                }
            }
        }
    }

    /// <summary>找出这个安装器已经装出的版本名（按加载器种类 + 文件名里的 MC 版本匹配）。</summary>
    private string FindInstalledVersion(string loader, string fileName)
    {
        string mc = ModFlow.InstallerMcFromFileName(loader, fileName);
        foreach (var v in _versions)
        {
            if (LoaderKey(v.Loader) != loader) continue;
            if (mc.Length > 0)
            {
                var meta = VersionMeta.Load(v.Name);
                if (meta == null || !meta.McVersion(v.Name).Equals(mc, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            return v.Name;
        }
        return "";
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
        RefreshLoaders();
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
