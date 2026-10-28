using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>截图格子里的一项：缩略图 + 文件名 + 时间。</summary>
public sealed class ScreenshotItem
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string TimeText { get; set; } = "";
    public BitmapImage? Image { get; set; }
}

/// <summary>版本设置 - 截图管理：缩略图网格，双击用默认程序打开，右键或按钮删除。
/// 截图目录跟随版本隔离设置（隔离版本在版本目录，否则全局目录）。</summary>
public sealed partial class VersionScreenshotsPage : Page
{
    private VersionSettingsPage? _shell;
    private readonly ObservableCollection<ScreenshotItem> _shots = new();

    public VersionScreenshotsPage()
    {
        InitializeComponent();
    }

    /// <summary>供 x:Bind 的网格源。</summary>
    public ObservableCollection<ScreenshotItem> Shots => _shots;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        Refresh();
    }

    private VersionSettingsPage Shell => _shell!;
    private string ShotsDir => Path.Combine(Shell.Meta.GameDir(Shell.Dir), "screenshots");

    private static bool IsImage(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        _shots.Clear();
        string dir = ShotsDir;
        var files = new List<(string Path, DateTime Time)>();
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    if (!IsImage(path)) continue;
                    try
                    {
                        files.Add((path, File.GetLastWriteTime(path)));
                    }
                    catch { /* 单个文件读取失败跳过 */ }
                }
            }
            catch { /* 目录不可读时按空列表处理 */ }
        }

        foreach (var file in files.OrderByDescending(f => f.Time))
        {
            BitmapImage image = new() { DecodePixelWidth = 160 };
            try
            {
                image.UriSource = new Uri(file.Path);
            }
            catch
            {
                continue;
            }
            _shots.Add(new ScreenshotItem
            {
                Path = file.Path,
                FileName = Path.GetFileName(file.Path),
                TimeText = file.Time.ToString("MM-dd HH:mm"),
                Image = image,
            });
        }
        UpdateEmptyState();
        UpdateSummary();
    }

    private void UpdateEmptyState()
    {
        bool empty = _shots.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShotGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSummary() => StatusText.Text = _shots.Count == 0
        ? $"截图目录：{ShotsDir}"
        : $"共 {_shots.Count} 张截图　{ShotsDir}";

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ShotsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ShotsDir}\"") { UseShellExecute = true });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Open(ScreenshotItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开失败: {ex.Message}";
        }
    }

    private void Shot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScreenshotItem item }) Open(item);
    }

    private void Shot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScreenshotItem item } element) return;
        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开" };
        open.Click += (_, _) => Open(item);
        flyout.Items.Add(open);
        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += (_, _) => _ = DeleteConfirmedAsync(item);
        flyout.Items.Add(delete);
        flyout.ShowAt(element);
    }

    private async void DeleteShot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScreenshotItem item })
        {
            await DeleteConfirmedAsync(item);
        }
    }

    private async Task DeleteConfirmedAsync(ScreenshotItem item)
    {
        var dialog = new ContentDialog
        {
            Title = "删除截图",
            Content = $"确定删除「{item.FileName}」吗？不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败: {ex.Message}";
            return;
        }
        _shots.Remove(item);
        UpdateEmptyState();
        UpdateSummary();
    }
}
