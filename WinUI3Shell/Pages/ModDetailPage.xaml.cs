using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>详情页里的加载器筛选项。</summary>
public sealed class ModLoaderOption
{
    public string Kind { get; set; } = "";
    public string Display { get; set; } = "";
}

/// <summary>一个可下载的包（Modrinth 版本 + 文件）。</summary>
public sealed class ModPackageItem
{
    public ModrinthVersion Version { get; set; } = new();
    public string Title { get; set; } = "";
    public string DetailText { get; set; } = "";
}

/// <summary>Mod 详情子页：先选加载器 + 游戏版本，再列出全部可下载包并下载进库。</summary>
public sealed partial class ModDetailPage : Page
{
    private ModrinthProject _project = new();
    private List<ModrinthVersion> _versions = new();
    private readonly ObservableCollection<ModLoaderOption> _loaders = new();
    private readonly ObservableCollection<string> _gameVersions = new();
    private readonly ObservableCollection<ModPackageItem> _packages = new();
    private CancellationTokenSource? _cts;
    private string _loaderKind = "";
    private bool _initializing;
    private bool _busy;

    public ModDetailPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => DownloadManager.JobsChanged -= OnJobChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ModrinthProject project)
        {
            _project = project;
        }
        TitleText.Text = KnownTitle(_project.Title);
        if (!string.IsNullOrEmpty(_project.IconUrl)
            && Uri.TryCreate(_project.IconUrl, UriKind.Absolute, out var iconUri))
        {
            IconImage.Source = new BitmapImage(iconUri);
            IconBox.Visibility = Visibility.Visible;
        }
        SubText.Text = BuildSubText();
        DescriptionText.Text = _project.Description;
        _ = TranslateTextsAsync();
        LoaderBox.ItemsSource = _loaders;
        GameVersionBox.ItemsSource = _gameVersions;
        PackageList.ItemsSource = _packages;
        _ = LoadVersionsAsync();
    }

    /// <summary>已知中文名优先，否则原文。</summary>
    private static string KnownTitle(string title)
    {
        string known = ModNames.Zh(title);
        return known.Length > 0 ? $"{known} ({title})" : title;
    }

    /// <summary>后台把名字和英文简介翻成中文（对照表命中就不翻，失败保持原文）。</summary>
    private async Task TranslateTextsAsync()
    {
        try
        {
            string originalTitle = _project.Title;
            bool known = ModNames.Zh(originalTitle).Length > 0;
            var title = Translator.ToChineseAsync(known ? "" : originalTitle, CancellationToken.None);
            var description = Translator.ToChineseAsync(_project.Description, CancellationToken.None);
            await Task.WhenAll(title, description);
            if (!known && title.Result.Length > 0 && title.Result != originalTitle)
            {
                TitleText.Text = $"{title.Result} ({originalTitle})";
            }
            if (description.Result != _project.Description)
            {
                DescriptionText.Text = description.Result;
            }
        }
        catch { /* 翻译失败保持原文 */ }
    }

    private string BuildSubText()
    {
        string downloads = _project.Downloads switch
        {
            >= 100_000_000 => $"{_project.Downloads / 100_000_000.0:0.#} 亿",
            >= 10_000 => $"{_project.Downloads / 10_000.0:0.#} 万",
            _ => _project.Downloads.ToString(),
        };
        string author = string.IsNullOrEmpty(_project.Author) ? "" : _project.Author + " · ";
        return $"{author}{downloads} 次下载";
    }

    private async Task LoadVersionsAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        PackageList.IsEnabled = false;
        LoaderBox.IsEnabled = false;
        GameVersionBox.IsEnabled = false;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "";
        try
        {
            _versions = await Modrinth.GetAllVersionsAsync(_project.ProjectId, ct);
            if (ct.IsCancellationRequested) return;

            _initializing = true;
            _loaders.Clear();
            foreach (string kind in _versions.SelectMany(v => v.Loaders).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _loaders.Add(new ModLoaderOption { Kind = kind, Display = ModFlow.LoaderName(kind) });
            }
            _initializing = false;

            if (_loaders.Count == 0)
            {
                EmptyText.Text = "没有可下载的包";
                EmptyHint.Visibility = Visibility.Visible;
                StatusText.Text = "";
                return;
            }

            // 默认跟随上次启动的版本（加载器 + MC 版本），没有就取最新
            var instances = ModFlow.LoadInstances();
            var last = instances.FirstOrDefault(o => o.Dir == AppSettings.LastVersion)
                ?? instances.FirstOrDefault(o => o.LoaderKind.Length > 0);
            var defaultLoader = (_loaders.FirstOrDefault(o =>
                    last != null && o.Kind.Equals(last.LoaderKind, StringComparison.OrdinalIgnoreCase))
                ?? _loaders[0]);

            _initializing = true;
            LoaderBox.SelectedItem = defaultLoader;
            _initializing = false;
            _loaderKind = defaultLoader.Kind;
            RebuildGameVersions(last?.McVersion);
            StatusText.Text = $"共 {_versions.Count} 个版本";
        }
        catch (OperationCanceledException)
        {
            // 页面切换时的取消
        }
        catch (Exception ex)
        {
            if (_cts != null && _cts.Token == ct)
            {
                StatusText.Text = "加载失败：" + ex.Message;
            }
        }
        finally
        {
            // 只让最后一次加载收尾，避免旧请求把新请求的转圈关掉
            if (_cts != null && _cts.Token == ct)
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                PackageList.IsEnabled = true;
                LoaderBox.IsEnabled = true;
                GameVersionBox.IsEnabled = true;
            }
        }
    }

    /// <summary>按当前加载器重建游戏版本列表，尽量保留原来选中的版本。</summary>
    private void RebuildGameVersions(string? preferred)
    {
        _initializing = true;
        _gameVersions.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in _versions.Where(v => v.Loaders.Contains(_loaderKind, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string gameVersion in version.GameVersions)
            {
                if (seen.Add(gameVersion))
                {
                    _gameVersions.Add(gameVersion);
                }
            }
        }
        int index = preferred == null
            ? -1
            : _gameVersions.ToList().FindIndex(g => g.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        GameVersionBox.SelectedIndex = index >= 0 ? index : (_gameVersions.Count > 0 ? 0 : -1);
        _initializing = false;
        RefreshPackages();
    }

    private void LoaderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LoaderBox.SelectedItem is not ModLoaderOption loader) return;
        _loaderKind = loader.Kind;
        RebuildGameVersions(null);
    }

    private void GameVersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RefreshPackages();
    }

    private void RefreshPackages()
    {
        _packages.Clear();
        if (GameVersionBox.SelectedItem is string mc && mc.Length > 0)
        {
            foreach (var version in _versions.Where(v =>
                v.Loaders.Contains(_loaderKind, StringComparer.OrdinalIgnoreCase)
                && v.GameVersions.Contains(mc, StringComparer.OrdinalIgnoreCase)))
            {
                var jar = version.PickJar();
                if (jar == null) continue;
                _packages.Add(new ModPackageItem
                {
                    Version = version,
                    Title = version.VersionNumber,
                    DetailText = $"{version.Name} · {jar.FileName} · {SizeText(jar.Size)}",
                });
            }
        }
        EmptyText.Text = "没有可下载的包";
        EmptyHint.Visibility = _packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string SizeText(long bytes) => bytes >= 1048576
        ? $"{bytes / 1048576.0:0.#} MB"
        : $"{Math.Max(bytes, 0) / 1024.0:0.#} KB";

    private DownloadJob? _pendingJob;

    /// <summary>下载所选包进库（注册为全局下载任务），完成后由顶部横幅提供「安装」入口。</summary>
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModPackageItem item } || _busy) return;
        if (GameVersionBox.SelectedItem is not string mc || mc.Length == 0) return;
        if (DownloadManager.IsModBusy(_project.Title))
        {
            StatusText.Text = "这个 Mod 正在下载中";
            return;
        }
        var jar = item.Version.PickJar();
        if (jar == null) return;

        _busy = true;
        InstallBar.Value = 0;
        InstallBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "";

        string loaderKind = _loaderKind;
        _pendingJob = DownloadManager.StartMod(_project.Title, async (job, ct) =>
        {
            var progress = new Progress<ModInstallProgress>(p =>
                DownloadManager.Report(job, p.Status, p.Fraction));
            DownloadManager.Report(job, "下载到库…", 0);
            await ModInstaller.DownloadJarToAsync(jar, ModLibrary.DirFor(_project.ProjectId),
                progress, "下载到库…", ct);
            ModLibrary.UpsertFile(_project.ProjectId, _project.Title, _project.Author,
                _project.IconUrl, new ModLibraryFile
                {
                    Loader = loaderKind,
                    McVersion = mc,
                    VersionId = item.Version.Id,
                    FileName = jar.FileName,
                    Size = jar.Size,
                });
            bool zh = ModLang.SupportsChinese(
                Path.Combine(ModLibrary.DirFor(_project.ProjectId), jar.FileName));
            DownloadManager.Report(job, zh ? "已加入 MOD 库（支持中文）" : "已加入 MOD 库", 1);
        }, _project.ProjectId);
        DownloadManager.JobsChanged += OnJobChanged;
        // 任务可能在订阅前就结束（极小概率），主动同步一次
        OnJobChanged(null, EventArgs.Empty);
    }

    // 任务进度（来自后台线程，切回 UI 线程）
    private void OnJobChanged(object? sender, EventArgs e)
    {
        var job = _pendingJob;
        if (job == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = job.StatusText;
            InstallBar.Value = Math.Clamp(job.Fraction, 0, 1);
            if (!job.Done) return;
            DownloadManager.JobsChanged -= OnJobChanged;
            InstallBar.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            _busy = false;
            _pendingJob = null;
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingJob?.Cts.Cancel();
        StatusText.Text = "正在取消…";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(ModSearchPage));
        }
    }
}
