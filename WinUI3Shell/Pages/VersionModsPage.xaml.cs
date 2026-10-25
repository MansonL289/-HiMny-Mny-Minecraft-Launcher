using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>Mod 列表项：一行一个 Mod，开关即启用状态。</summary>
public sealed class ModItem
{
    /// <summary>当前磁盘路径（启用 xxx.jar / 禁用 xxx.jar.disabled），切换后会变。</summary>
    public string FilePath { get; set; } = "";
    /// <summary>显示名（不含 .disabled 后缀）。</summary>
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string SizeText { get; set; } = "";
}

/// <summary>版本设置 - Mod 管理：每行一个开关（启用/禁用）+ 删除按钮，
/// 操作直接作用在行上，无需先选中。Mod 目录跟随版本隔离设置。</summary>
public sealed partial class VersionModsPage : Page
{
    private VersionSettingsPage? _shell;
    private readonly ObservableCollection<ModItem> _mods = new();

    public VersionModsPage()
    {
        InitializeComponent();
    }

    /// <summary>供 x:Bind 的列表源。</summary>
    public ObservableCollection<ModItem> Mods => _mods;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        RefreshList();
    }

    private VersionSettingsPage Shell => _shell!;
    private string ModsDir => Path.Combine(Shell.Meta.GameDir(Shell.Dir), "mods");

    private void RefreshList()
    {
        _mods.Clear();
        // .jar = 启用，.jar.disabled = 禁用
        var files = Directory.Exists(ModsDir)
            ? Directory.GetFiles(ModsDir, "*.jar")
                .Concat(Directory.GetFiles(ModsDir, "*.jar.disabled"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new();
        foreach (string file in files)
        {
            bool disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileName(file);
            if (disabled)
            {
                name = name[..^".disabled".Length];
            }
            long len = new FileInfo(file).Length;
            _mods.Add(new ModItem
            {
                FilePath = file,
                Name = name,
                IsEnabled = !disabled,
                SizeText = len >= 1024 * 1024 ? $"{len / 1048576.0:0.#} MB" : $"{len / 1024.0:0.#} KB",
            });
        }
        bool empty = _mods.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
    }

    private void UpdateStatus(string? extra = null)
    {
        StatusText.Text = _mods.Count == 0
            ? extra ?? ""
            : $"共 {_mods.Count} 个 Mod，{_mods.Count(m => m.IsEnabled)} 个已启用" + (extra == null ? "" : $"　{extra}");
    }

    private void OpenModsDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ModsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModsDir}\"") { UseShellExecute = true });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private async void InstallMods_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jar");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        Directory.CreateDirectory(ModsDir);
        foreach (var file in files)
        {
            File.Copy(file.Path, Path.Combine(ModsDir, file.Name), true);
        }
        RefreshList();
    }

    private void ModToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.Tag is not ModItem item) return;
        if (ts.IsOn == item.IsEnabled) return; // 绑定初始化触发的，无需处理

        string target = ts.IsOn
            ? item.FilePath[..^".disabled".Length]
            : item.FilePath + ".disabled";
        try
        {
            File.Move(item.FilePath, target);
            item.FilePath = target;
            item.IsEnabled = ts.IsOn;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ts.IsOn = item.IsEnabled; // 改回原状（会再触发一次，但被上面的判断拦截）
            UpdateStatus($"操作失败: {ex.Message}");
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModItem item }) return;
        try
        {
            File.Delete(item.FilePath);
        }
        catch (Exception ex)
        {
            UpdateStatus($"删除失败: {ex.Message}");
            return;
        }
        _mods.Remove(item);
        bool empty = _mods.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
    }
}
