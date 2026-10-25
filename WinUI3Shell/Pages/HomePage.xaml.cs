using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

public sealed partial class HomePage : Page
{
    private bool _filtering;
    private CancellationTokenSource? _launchCts;

    public HomePage()
    {
        InitializeComponent();
        ApplyAvatarShadow();
        // 下载任务状态变化时刷新版本列表（下载中的版本不可选）
        DownloadManager.JobsChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshVersionList);
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshVersionList();
    }

    /// <summary>供主窗口在从版本设置返回时刷新（改名/删除/置顶可能已变化）。</summary>
    public void RefreshVersions() => RefreshVersionList();

    // 从 versions/ 目录刷新已下载的版本列表（置顶优先）；正在下载的版本列出但禁用
    private void RefreshVersionList()
    {
        string? current = (VersionComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var entries = Directory.Exists(McPaths.VersionsDir)
            ? Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => (Dir: Path.GetFileName(d)!, Meta: VersionMeta.Load(Path.GetFileName(d)!)))
                .Where(x => x.Meta != null)
                .Select(x => (x.Dir, Meta: x.Meta!))
                .ToList()
            : new();
        // 置顶优先，其余按名字倒序
        var ordered = entries.OrderByDescending(x => x.Meta.Pinned)
            .ThenByDescending(x => x.Dir).ToList();
        // 还没落盘的下载中版本也补进列表
        var names = ordered.Select(x => x.Dir).ToHashSet();
        foreach (var job in DownloadManager.Jobs)
        {
            if (!job.Done && !names.Contains(job.InstallName))
            {
                ordered.Insert(0, (job.InstallName, new VersionMeta { Id = job.VersionId }));
            }
        }

        ComboBoxItem? toSelect = null;
        var comboItems = ordered.Select(x =>
        {
            bool downloading = DownloadManager.Jobs.Any(j => !j.Done && j.InstallName == x.Dir);
            var item = new ComboBoxItem
            {
                Content = downloading ? x.Dir + "（下载中）" : x.Dir,
                Tag = x.Dir,
                IsEnabled = !downloading,
            };
            if (x.Dir == current && item.IsEnabled)
            {
                toSelect = item;
            }
            return item;
        }).ToList();
        VersionComboBox.ItemsSource = comboItems;
        VersionComboBox.SelectedItem = toSelect;
        LaunchButton.IsEnabled = VersionComboBox.SelectedItem != null;
        VersionSettingsButton.IsEnabled = VersionComboBox.SelectedItem != null;
    }

    // 用 Composition DropShadow 给直角头像加阴影（挂在头像后面的承载层上）
    private void ApplyAvatarShadow()
    {
        var visual = ElementCompositionPreview.GetElementVisual(AvatarShadowHost);
        var compositor = visual.Compositor;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 10f;
        shadow.Offset = new Vector3(0f, 3f, 0f);
        shadow.Color = Windows.UI.Color.FromArgb(140, 0, 0, 0);

        var sprite = compositor.CreateSpriteVisual();
        sprite.Size = new Vector2(56f, 56f);
        sprite.Shadow = shadow;

        ElementCompositionPreview.SetElementChildVisual(AvatarShadowHost, sprite);
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 只有选择了已下载的版本才能启动/进版本设置
        LaunchButton.IsEnabled = VersionComboBox.SelectedItem != null;
        VersionSettingsButton.IsEnabled = VersionComboBox.SelectedItem != null;
    }

    private void VersionSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is string dir)
        {
            App.MainWindow?.ShowVersionSettings(dir);
        }
    }

    // 启动流程：检查游戏文件 -> 检查/自动下载 Java -> 启动
    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is not string dir) return;
        var meta = VersionMeta.Load(dir);
        if (meta == null) return;
        string versionId = meta.Id;

        LaunchButton.IsEnabled = false;
        VersionComboBox.IsEnabled = false;
        CancelLaunchButton.Visibility = Visibility.Visible;
        LaunchProgress.Visibility = Visibility.Visible; // 上次启动后可能被隐藏，恢复
        LaunchProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<McProgress>(OnLaunchProgress);
        _launchCts = new CancellationTokenSource();

        try
        {
            SetLaunchStatus("检查游戏文件...");
            string jsonPath = meta.JsonPath(dir);
            string jarPath = meta.JarPath(dir);

            JsonObject versionJson;
            if (File.Exists(jsonPath) && File.Exists(jarPath))
            {
                versionJson = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;
            }
            else
            {
                // 文件不完整，自动补全下载（用下载页选定的镜像源；已存在且校验通过的文件会跳过）
                versionJson = await Installer.PrepareAsync(versionId, AppSettings.DownloadMirror, 16, progress, _launchCts.Token, dir);
            }

            // 自愈：老版本 virtual 资源未物化（历史安装/安装中断）→ 补齐；natives 目录为空 → 重新解压
            SetLaunchStatus("检查资源与 natives...");
            await Assets.HealVirtualAsync(versionJson, _launchCts.Token);
            var (_, _, nativeItems) = Libraries.Collect(versionJson, AppSettings.DownloadMirror);
            string nativesDir = Path.Combine(McPaths.VersionDir(dir), "natives");
            if (nativeItems.Count > 0 &&
                (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir, "*.dll").Any()))
            {
                SetLaunchStatus("修复 natives...");
                await Task.Run(() => Libraries.ExtractNatives(nativeItems, nativesDir));
            }

            var (javaMin, javaMax) = JavaService.RequiredRange(versionJson);
            SetLaunchStatus(javaMax == int.MaxValue
                ? $"检查 Java（需要 >= {javaMin}）..."
                : $"检查 Java（老版本需要 Java {javaMin}）...");
            // 自定义 Java 打不开/主版本不符时自动回退自动选择
            string java = await meta.ResolveJavaAsync(javaMin, javaMax, progress, _launchCts.Token);

            SetLaunchStatus("启动游戏...");
            string player = string.IsNullOrWhiteSpace(NameText.Text) ? "Steve" : NameText.Text.Trim();
            string gameDir = meta.GameDir(dir); // 版本隔离时 = 版本目录
            LaunchService.WriteGameLanguage(gameDir, ResolveLocale());
            var cmd = LaunchService.BuildCommand(versionJson, java, player, meta.MaxMemString(), gameDir,
                AppSettings.WindowWidth, AppSettings.WindowHeight);
            // 自动加入服务器：追加 --server/--port 游戏参数
            if (!string.IsNullOrWhiteSpace(meta.AutoJoinServer))
            {
                var parts = meta.AutoJoinServer.Trim().Split(':', 2);
                cmd.Add("--server");
                cmd.Add(parts[0]);
                cmd.Add("--port");
                cmd.Add(parts.Length > 1 && int.TryParse(parts[1], out int port) ? port.ToString() : "25565");
            }
            int code = await LaunchService.RunAsync(cmd, gameDir, null, _launchCts.Token,
                onStarted: () =>
                {
                    // 游戏进程已启动：进度条没有存在意义了，隐藏，只留状态文字
                    LaunchProgress.Visibility = Visibility.Collapsed;
                    SetLaunchStatus($"游戏运行中（{dir}）");
                });

            SetLaunchStatus(code == 0
                ? "游戏已正常退出"
                : $"游戏异常退出 (代码 {code})，日志见 {Path.Combine(gameDir, "logs", "latest.log")}");
        }
        catch (OperationCanceledException)
        {
            SetLaunchStatus("已取消启动");
        }
        catch (Exception ex)
        {
            SetLaunchStatus($"启动失败: {ex.Message}");
        }
        finally
        {
            _launchCts.Dispose();
            _launchCts = null;
            CancelLaunchButton.Visibility = Visibility.Collapsed;
            VersionComboBox.IsEnabled = true;
            LaunchButton.IsEnabled = VersionComboBox.SelectedItem != null;
        }
    }

    // 取消启动：下载阶段直接中断；游戏已启动则杀掉进程
    private void CancelLaunch_Click(object sender, RoutedEventArgs e) => _launchCts?.Cancel();

    private void OnLaunchProgress(McProgress p)
    {
        if (p.TotalBytes > 0)
        {
            LaunchProgress.IsIndeterminate = false;
            LaunchProgress.Maximum = p.TotalBytes;
            LaunchProgress.Value = Math.Min(p.DoneBytes, p.TotalBytes);
            SetLaunchStatus(p.TotalFiles > 0
                ? $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB ({p.DoneFiles}/{p.TotalFiles})"
                : $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB");
        }
        else
        {
            LaunchProgress.IsIndeterminate = true;
            SetLaunchStatus(p.Phase);
        }
    }

    private void SetLaunchStatus(string text) => LaunchStatus.Text = text;

    // 解析游戏语言：设置项为空时跟随系统，"zh-CN" -> "zh_cn"
    private static string ResolveLocale()
    {
        string culture = AppSettings.GameLanguage;
        if (string.IsNullOrEmpty(culture))
        {
            culture = CultureInfo.CurrentUICulture.Name;
        }
        return culture.Replace('-', '_').ToLowerInvariant();
    }

    // 悬浮在名称上时切换为文本指针，移开恢复箭头
    private void NameText_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    }

    private void NameText_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void NameText_Tapped(object sender, TappedRoutedEventArgs e)
    {
        NameBox.Text = NameText.Text;
        NameText.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Visible;
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只允许英文字母、数字和下划线
        if (_filtering)
        {
            return;
        }

        string filtered = new(NameBox.Text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        if (filtered != NameBox.Text)
        {
            _filtering = true;
            int caret = NameBox.SelectionStart;
            NameBox.Text = filtered;
            NameBox.SelectionStart = Math.Min(caret, filtered.Length);
            _filtering = false;
        }
    }

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitName();
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitName();
    }

    private void CommitName()
    {
        if (NameBox.Visibility == Visibility.Collapsed)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Steve" : NameBox.Text;
        NameText.Text = name;
        NameBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
    }
}
