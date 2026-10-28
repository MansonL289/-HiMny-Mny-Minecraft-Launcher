using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
        ApplyDefaultAvatar();
        ApplyAvatarShadow();
        // 下载任务状态变化时刷新版本列表（下载中的版本不可选）
        DownloadManager.JobsChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshVersionList);
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshVersionList();
        RefreshAccount();
    }

    /// <summary>供主窗口在从版本设置返回时刷新（改名/删除/置顶可能已变化）。</summary>
    public void RefreshVersions() => RefreshVersionList();

    // 默认头像：Head.png（在线账号加载失败时也回落到它）
    private void ApplyDefaultAvatar()
    {
        AvatarBorder.Background = new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Head.png")),
            Stretch = Stretch.UniformToFill,
        };
    }

    /// <summary>按当前账号刷新名称与皮肤头；没有账号时显示 Steve。</summary>
    private void RefreshAccount()
    {
        var account = AccountStore.Active;
        if (account == null || string.IsNullOrWhiteSpace(account.Name))
        {
            NameText.Text = "Steve";
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            return;
        }

        NameText.Text = account.Name;
        AvatarImage.Source = new BitmapImage(
            new Uri($"https://mc-heads.net/avatar/{Uri.EscapeDataString(account.Name)}/56"));
        AvatarImage.Visibility = Visibility.Visible;
    }

    // 皮肤头加载失败（离线/无网络）：隐藏图片，露出默认 Head.png
    private void AvatarImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;
    }

    // 从 versions/ 目录刷新已下载的版本列表（置顶优先）；正在下载的版本列出但禁用
    private void RefreshVersionList()
    {
        string? current = (VersionComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var entries = Directory.Exists(McPaths.VersionsDir)
            ? Directory.GetDirectories(McPaths.VersionsDir)
                .Select(d => (Dir: Path.GetFileName(d)!, Meta: VersionMeta.Load(Path.GetFileName(d)!)))
                .Where(x => x.Meta != null)
                .Select(x => (x.Dir, Meta: x.Meta!))
                // 装了一半的版本（取消/失败/文件缺失）不进启动列表
                .Where(x => x.Meta.IsComplete(x.Dir))
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
                Content = BuildVersionContent(x.Dir, x.Meta, downloading),
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
        // 没有保留的上次选择时，按时间挑默认版本：最新下载 > 上次打开 > 不选
        if (toSelect == null)
        {
            string? defaultDir = PickDefaultDir(comboItems);
            if (defaultDir != null)
            {
                toSelect = comboItems.FirstOrDefault(i => (i.Tag as string) == defaultDir);
            }
        }
        VersionComboBox.SelectedItem = toSelect;
        LaunchButton.IsEnabled = VersionComboBox.SelectedItem != null;
        VersionSettingsButton.IsEnabled = VersionComboBox.SelectedItem != null;
    }

    /// <summary>版本下拉框内容：有自定义图标时显示「图标 + 名字」，否则纯文本。</summary>
    private static object BuildVersionContent(string dir, VersionMeta meta, bool downloading)
    {
        string text = downloading ? dir + "（下载中）" : dir;
        if (downloading || string.IsNullOrWhiteSpace(meta.Icon)) return text;
        try
        {
            string iconPath = Path.Combine(VersionMeta.DirPath(dir), meta.Icon);
            if (!File.Exists(iconPath)) return text;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new Image
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage { UriSource = new Uri(iconPath) },
            });
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        catch
        {
            return text; // 图标路径异常时退回纯文本
        }
    }

    /// <summary>默认版本：比较「最新下载时间」与「上次打开时间」，取较新者；都没有则不选。</summary>
    private static string? PickDefaultDir(List<ComboBoxItem> items)
    {
        var installed = items.Where(i => i.IsEnabled && i.Tag is string).ToList();
        if (installed.Count == 0) return null;

        string? newestDir = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (var item in installed)
        {
            string dir = (string)item.Tag!;
            DateTime created = Directory.GetCreationTime(VersionMeta.DirPath(dir));
            if (newestDir == null || created > newestTime)
            {
                newestDir = dir;
                newestTime = created;
            }
        }

        string lastDir = AppSettings.LastVersion;
        DateTime lastTime = AppSettings.LastLaunchTicks > 0
            ? new DateTime(AppSettings.LastLaunchTicks)
            : DateTime.MinValue;
        bool lastExists = lastDir.Length > 0 && installed.Any(i => (i.Tag as string) == lastDir);

        return lastExists && lastTime >= newestTime ? lastDir : newestDir;
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
        UpdatePlaytimeText();
    }

    /// <summary>显示当前选中版本的累计时长与上次启动时间。</summary>
    private void UpdatePlaytimeText()
    {
        if ((VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is not string dir)
        {
            PlaytimeText.Visibility = Visibility.Collapsed;
            return;
        }
        long seconds = Playtime.GetSeconds(dir);
        DateTime? last = Playtime.GetLastPlayed(dir);
        if (seconds <= 0 && last == null)
        {
            PlaytimeText.Visibility = Visibility.Collapsed;
            return;
        }
        string text = $"已玩 {Playtime.FormatSeconds(seconds)}";
        if (last.HasValue) text += $" · 上次 {last.Value:MM-dd HH:mm}";
        PlaytimeText.Text = text;
        PlaytimeText.Visibility = Visibility.Visible;
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

        // 记录上次打开的版本与时间，供下次首页默认选中
        AppSettings.LastVersion = dir;
        AppSettings.LastLaunchTicks = DateTime.Now.Ticks;

        LaunchButton.IsEnabled = false;
        VersionComboBox.IsEnabled = false;
        CancelLaunchButton.Visibility = Visibility.Visible;
        LaunchProgress.Visibility = Visibility.Visible; // 上次启动后可能被隐藏，恢复
        LaunchProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<McProgress>(OnLaunchProgress);
        _launchCts = new CancellationTokenSource();
        bool crashed = false;
        Stopwatch? playWatch = null; // 进程启动后开始计时，退出后累加本次时长
        string gameDir = meta.GameDir(dir); // 版本隔离时 = 版本目录

        try
        {
            SetLaunchStatus("检查游戏文件…");
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
            SetLaunchStatus("检查资源与运行库…");
            await Assets.HealVirtualAsync(versionJson, _launchCts.Token);
            var (_, _, nativeItems) = Libraries.Collect(versionJson, AppSettings.DownloadMirror);
            string nativesDir = Path.Combine(McPaths.VersionDir(dir), "natives");
            if (nativeItems.Count > 0 &&
                (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir, "*.dll").Any()))
            {
                SetLaunchStatus("修复运行库…");
                await Task.Run(() => Libraries.ExtractNatives(nativeItems, nativesDir));
            }

            var (javaMin, javaMax) = JavaService.RequiredRange(versionJson);
            SetLaunchStatus(javaMax == int.MaxValue
                ? $"检查 Java（需要 >= {javaMin}）…"
                : $"检查 Java（老版本需要 Java {javaMin}）…");
            // 自定义 Java 打不开/主版本不符时自动回退自动选择
            string java = await meta.ResolveJavaAsync(javaMin, javaMax, progress, _launchCts.Token);

            // 启动前解析账号：微软令牌过期先用刷新令牌续期，失败则不启动
            var account = AccountStore.Active;
            if (account?.Type == "microsoft" && account.IsExpired)
            {
                SetLaunchStatus("刷新微软登录…");
                try
                {
                    account = await MsaLogin.RefreshAsync(account, _launchCts.Token);
                    AccountStore.Add(account);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SetLaunchStatus($"登录已失效：{ex.Message}");
                    return;
                }
            }
            else if (account?.Type == "yggdrasil" && account.IsExpired)
            {
                SetLaunchStatus("刷新第三方登录…");
                try
                {
                    account = await YggdrasilLogin.RefreshAsync(account, _launchCts.Token);
                    AccountStore.Add(account);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    SetLaunchStatus("第三方登录已过期，请在账号页重新登录");
                    return;
                }
            }

            // 第三方账号：注入 authlib-injector，把验证服务器地址告诉游戏
            string? authlibAgent = null;
            if (account?.Type == "yggdrasil")
            {
                SetLaunchStatus("准备 authlib-injector…");
                authlibAgent = await AuthlibInjector.EnsureAsync(progress, _launchCts.Token);
            }

            SetLaunchStatus("启动游戏…");
            string player = account?.Name ?? (string.IsNullOrWhiteSpace(NameText.Text) ? "Steve" : NameText.Text.Trim());
            LaunchService.WriteGameLanguage(gameDir, ResolveLocale());
            var cmd = LaunchService.BuildCommand(versionJson, java, player, meta.MaxMemString(), gameDir,
                AppSettings.WindowWidth, AppSettings.WindowHeight, account, authlibAgent);
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
                    playWatch = Stopwatch.StartNew();
                    // 游戏进程已启动：进度条没有存在意义了，隐藏，只留状态文字
                    LaunchProgress.Visibility = Visibility.Collapsed;
                    SetLaunchStatus($"游戏运行中（{dir}）");
                });

            SetLaunchStatus(code == 0
                ? "游戏已正常退出"
                : $"游戏异常退出 (代码 {code})");
            crashed = code != 0;
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
            // 无论正常退出还是崩溃，本次游玩时长都计入该版本
            if (playWatch != null)
            {
                playWatch.Stop();
                Playtime.Add(dir, (long)playWatch.Elapsed.TotalSeconds);
                UpdatePlaytimeText();
            }
        }

        // 非正常退出：自动给出崩溃分析（和 PCL 一样，用户不用自己翻日志）
        if (crashed)
        {
            await ShowLogDialogAsync(gameDir);
        }
    }

    private async void LogButton_Click(object sender, RoutedEventArgs e)
    {
        string gameDir = (VersionComboBox.SelectedItem as ComboBoxItem)?.Tag is string dir
            ? (VersionMeta.Load(dir)?.GameDir(dir) ?? McPaths.GameDir)
            : McPaths.GameDir;
        await ShowLogDialogAsync(gameDir);
    }

    /// <summary>日志窗口：先给中文结论，下面是最新日志 / 崩溃报告原文。</summary>
    private async Task ShowLogDialogAsync(string gameDir)
    {
        string? text = await Task.Run(() => LogAnalyzer.ReadLog(gameDir));
        var report = LogAnalyzer.Analyze(text);

        var panel = new StackPanel { Spacing = 8, MinWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = report.Headline,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (string finding in report.Findings)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "• " + finding,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
        }

        var box = new TextBox
        {
            Text = report.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "游戏日志",
            Content = panel,
            PrimaryButtonText = "打开日志文件夹",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            string logDir = Path.Combine(McPaths.GameDir, "logs");
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDir}\"") { UseShellExecute = true });
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

    // 解析游戏语言：设置项为空时跟随系统；游戏没有的语言依次退回同名语言 / en_us
    private static string ResolveLocale() => GameLanguages.Resolve(AppSettings.GameLanguage) ?? "en_us";

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
        // 在线账号的名称由验证服务器决定，不能改；点名称直接去账号页
        var account = AccountStore.Active;
        if (account != null && account.Type != "offline")
        {
            App.MainWindow?.ShowAccount();
            return;
        }
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

        // 离线账号改名：UUID 随名字重新计算，账号记录同步保存
        var account = AccountStore.Active;
        if (account != null && account.Type == "offline")
        {
            AccountStore.Remove(account);
            account.Name = name;
            account.Uuid = LaunchService.OfflineUuid(name);
            AccountStore.Add(account);
        }
        RefreshAccount();
    }
}
