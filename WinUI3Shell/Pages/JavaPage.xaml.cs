using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>Java 列表项：一个可用的 Java 运行时。</summary>
public sealed class JavaItem
{
    public string FilePath { get; init; } = "";
    public int Major { get; init; }
    public string Title => $"Java {Major}";
    public bool IsDefault { get; init; }
    public Visibility DefaultVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SetVisibility => IsDefault ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>设置 - Java：扫描 / 手动添加 / 下载 Java，并选择全局默认运行时。</summary>
public sealed partial class JavaPage : Page
{
    private readonly ObservableCollection<JavaItem> _items = new();
    private readonly List<(string Path, int Major)> _found = new();
    private bool _busy;

    public JavaPage()
    {
        InitializeComponent();
        JavaList.ItemsSource = _items;
    }

    private async void JavaPage_Loaded(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    /// <summary>扫描本机所有 Java 并刷新列表。</summary>
    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在扫描 Java…");
        try
        {
            _found.Clear();
            _found.AddRange(await JavaService.ScanAllAsync());
            RefreshList();
            StatusText.Text = _found.Count == 0 ? "" : $"共找到 {_found.Count} 个 Java。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>按当前默认 Java 重建列表，默认项排在最前。</summary>
    private void RefreshList()
    {
        string current = AppSettings.GlobalJavaPath;
        var ordered = _found
            .OrderByDescending(j => string.Equals(j.Path, current, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(j => j.Major)
            .ToList();

        _items.Clear();
        foreach (var (path, major) in ordered)
        {
            _items.Add(new JavaItem
            {
                FilePath = path,
                Major = major,
                IsDefault = string.Equals(path, current, StringComparison.OrdinalIgnoreCase),
            });
        }

        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        JavaList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: JavaItem item }) return;
        // 已经是默认项就不用再设置
        if (string.Equals(AppSettings.GlobalJavaPath, item.FilePath, StringComparison.OrdinalIgnoreCase)) return;

        AppSettings.GlobalJavaPath = item.FilePath;
        RefreshList();
        StatusText.Text = $"已将 Java {item.Major} 设为默认。";
    }

    private async void AddJava_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        if (!string.Equals(Path.GetFileName(file.Path), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "请选择 java.exe（一般在 Java 安装目录的 bin 文件夹里）。";
            return;
        }

        SetBusy(true, "正在读取 Java 版本…");
        try
        {
            int? major = await JavaService.GetMajorAsync(file.Path);
            if (major == null)
            {
                StatusText.Text = "无法识别这个 Java，请确认选中的是 java.exe。";
                return;
            }
            if (!_found.Any(j => string.Equals(j.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            {
                _found.Add((file.Path, major.Value));
            }
            // 手动添加的 Java 直接设为默认，省得再点一次
            AppSettings.GlobalJavaPath = file.Path;
            RefreshList();
            StatusText.Text = $"已添加并设为默认：Java {major.Value}（{file.Path}）";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void DownloadJava_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out int major)) return;

        SetBusy(true, $"正在下载 Java {major}…");
        var progress = new Progress<McProgress>(p => StatusText.Text = p.TotalBytes > 0
            ? $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB"
            : p.Phase);
        try
        {
            string path = await JavaService.DownloadAsync(major, progress, CancellationToken.None);
            _found.Clear();
            _found.AddRange(await JavaService.ScanAllAsync());
            RefreshList();
            StatusText.Text = $"Java {major} 已就绪：{path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载 Java {major} 失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>切换忙碌状态：禁用工具栏按钮并在列表正中显示加载动画。</summary>
    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        AddButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            // 扫描/下载期间先收起空状态文案，避免和加载动画重叠
            EmptyHint.Visibility = Visibility.Collapsed;
        }
        if (status != null)
        {
            StatusText.Text = status;
        }
    }
}
