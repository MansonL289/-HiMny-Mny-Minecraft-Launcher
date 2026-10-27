using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUI3Shell.Pages;
using WinUI3Shell.Services.Mc;
using Windows.Graphics;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace WinUI3Shell;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "MnyLauncher";
        SystemBackdrop = new MicaBackdrop();
        // 启动器自身窗口：屏幕工作区的 5/12 大小并居中（与“游戏窗口大小”设置无关）
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int winW = workArea.Width * 5 / 12, winH = workArea.Height * 5 / 12;
        AppWindow.Resize(new SizeInt32(winW, winH));
        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - winW) / 2,
            workArea.Y + (workArea.Height - winH) / 2));

        // 任务栏/窗口图标
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // 内容延伸到标题栏，由 AppTitleBar 充当可拖动的标题栏区域
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 与设置、任务管理器一致的高标题栏（48px），标题栏按钮垂直居中
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        UpdateTitleBarColors();

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        }

        // 默认选中“启动”，触发导航
        NavView.SelectedItem = NavView.MenuItems[0];

        // 全局下载指示按钮
        DownloadManager.JobsChanged += OnJobsChanged;
        OnJobsChanged(null, EventArgs.Empty);
    }

    /// <summary>窗口句柄，供文件选择器等 Win32 互操作使用。</summary>
    public IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>打开指定版本的版本设置页（不进页面缓存，每次新建）；section 可直达子页。</summary>
    public void ShowVersionSettings(string versionDir, string? section = null)
    {
        ContentFrame.Content = new VersionSettingsPage(versionDir, section);
    }

    /// <summary>从版本设置返回启动页（恢复缓存的页面实例，保留状态）。</summary>
    public void ShowHome()
    {
        if (_pageCache.TryGetValue("Launch.Home", out var page))
        {
            ContentFrame.Content = page;
            // 改名/删除/置顶可能已变化，刷新版本列表
            if (page is HomePage home)
            {
                home.RefreshVersions();
            }
        }
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    // 下载任务状态变化（来自后台线程，需切回 UI 线程）
    private void OnJobsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var jobs = DownloadManager.Jobs;
            DlIndicator.Visibility = jobs.Any(j => !j.Done) ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(DlIndicator, string.Join('\n',
                jobs.Select(j => $"{j.VersionId}：{j.StatusText}")));
        });
    }

    // 点击标题栏下载按钮：跳到下载页并直达「下载任务」
    private void DlIndicator_Click(object sender, RoutedEventArgs e) => ShowDownloadTasks();

    /// <summary>跳到下载页并切到「下载任务」（下载开始后返回用）。</summary>
    public void ShowDownloadTasks()
    {
        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem navItem && navItem.Tag as string == "Download.Vanilla")
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }
        if (_pageCache.TryGetValue("Download.Vanilla", out var page) && page is DownloadsPage dl)
        {
            dl.ShowTasks();
        }
    }

    /// <summary>切换整个窗口的主题（ElementTheme.Default 表示跟随系统）。</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }

        UpdateTitleBarColors();
    }

    private void UpdateTitleBarColors()
    {
        bool isDark = Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
        AppWindowTitleBar titleBar = AppWindow.TitleBar;

        // 按钮背景透明，让标题栏区域与 Mica 融为一体
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x15, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
    }

    // 手动缓存页面实例：切页后回来状态（如下载进度）保持不变。
    // WinUI 3 的 NavigationCacheMode 缓存不可靠，故不用 Frame.Navigate。
    private readonly Dictionary<string, Page> _pageCache = new();

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!_pageCache.TryGetValue(tag, out Page? page))
        {
            page = tag switch
            {
                "Download.Vanilla" => new DownloadsPage(),
                "Mod.Library" => new ModLibraryPage(),
                "Settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pageCache[tag] = page;
        }

        if (!ReferenceEquals(ContentFrame.Content, page))
        {
            ContentFrame.Content = page;
        }
    }
}
