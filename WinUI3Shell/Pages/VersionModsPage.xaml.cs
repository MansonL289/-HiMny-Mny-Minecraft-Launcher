using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
    /// <summary>大小，若有 mods.json 记录再附带加载器与版本。</summary>
    public string DetailText { get; set; } = "";
}

/// <summary>版本设置 - Mod 管理：每行一个开关（启用/禁用）+ 删除按钮，
/// 操作直接作用在行上，无需先选中。Mod 目录跟随版本隔离设置。</summary>
public sealed partial class VersionModsPage : Page
{
    private VersionSettingsPage? _shell;
    private readonly ObservableCollection<ModItem> _mods = new();
    /// <summary>当前版本的加载器显示文本（空 = 原版，不允许装 Mod）。</summary>
    private string _loader = "";

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
    private string LoaderKind => Shell.Meta.LoaderType(Shell.Dir);

    private void RefreshList()
    {
        _mods.Clear();
        _loader = Shell.Meta.DetectLoader(Shell.Dir);
        bool hasLoader = _loader.Length > 0;
        InstallModsButton.IsEnabled = hasLoader;
        LoaderWarning.Visibility = hasLoader ? Visibility.Collapsed : Visibility.Visible;

        // .jar = 启用，.jar.disabled = 禁用；mods.json 补充加载器与版本信息
        var records = ModRegistry.Load(ModsDir)
            .ToDictionary(r => BareName(r.File), r => r, StringComparer.OrdinalIgnoreCase);
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
            string size = len >= 1024 * 1024 ? $"{len / 1048576.0:0.#} MB" : $"{len / 1024.0:0.#} KB";
            string detail = size;
            if (records.TryGetValue(name, out var record))
            {
                detail = $"{LoaderName(record.Loader)} · {record.McVersion} · {size}";
            }
            _mods.Add(new ModItem
            {
                FilePath = file,
                Name = name,
                IsEnabled = !disabled,
                DetailText = detail,
            });
        }
        bool empty = _mods.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
    }

    private static string BareName(string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    private static string LoaderName(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "" => "原版",
        _ => kind,
    };

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

    // ========== 更新检查 ==========

    private void SetUpdating(bool updating)
    {
        CheckUpdateButton.IsEnabled = !updating;
        UpdateRing.IsActive = updating;
        UpdateRing.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_loader.Length == 0)
        {
            UpdateStatus("当前版本未安装加载器，无需检查更新");
            return;
        }
        SetUpdating(true);
        UpdateStatus("正在检查 Mod 更新…");
        List<ModUpdateInfo> updates;
        try
        {
            updates = await ModUpdate.CheckAsync(ModsDir, Shell.Meta.McVersion(Shell.Dir), LoaderKind);
        }
        catch (Exception ex)
        {
            UpdateStatus($"检查更新失败: {ex.Message}");
            return;
        }
        finally
        {
            SetUpdating(false);
        }

        if (updates.Count == 0)
        {
            StatusText.Text = "所有 Mod 都是最新版";
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 320 };
        foreach (var update in updates)
        {
            var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = update.ModName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = update.SummaryText,
                FontSize = 12,
                Opacity = 0.7,
            });
            list.Items.Add(row);
        }
        var dialog = new ContentDialog
        {
            Title = $"发现 {updates.Count} 个可更新的 Mod",
            Content = list,
            PrimaryButtonText = "全部更新",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await UpdateAllAsync(updates);
    }

    /// <summary>逐个更新；单个失败不中断，最后汇总失败数量。</summary>
    private async Task UpdateAllAsync(List<ModUpdateInfo> updates)
    {
        SetUpdating(true);
        int ok = 0;
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < updates.Count; i++)
            {
                var update = updates[i];
                UpdateStatus($"正在更新 {i + 1}/{updates.Count}：{update.ModName}");
                try
                {
                    await ModUpdate.UpdateAsync(ModsDir, update, null);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{update.ModName}（{ex.Message}）");
                }
            }
        }
        finally
        {
            SetUpdating(false);
        }
        RefreshList();
        StatusText.Text = failed.Count == 0
            ? $"已更新 {ok} 个 Mod"
            : $"已更新 {ok} 个 Mod，{failed.Count} 个失败：{string.Join("；", failed)}";
    }

    private async void InstallMods_Click(object sender, RoutedEventArgs e)
    {
        if (_loader.Length == 0)
        {
            UpdateStatus("当前版本未安装加载器，无法安装 Mod");
            return;
        }
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jar");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        Directory.CreateDirectory(ModsDir);
        foreach (var file in files)
        {
            string dest = Path.Combine(ModsDir, file.Name);
            File.Copy(file.Path, dest, true);
            RegisterLocal(dest);
        }
        RefreshList();
    }

    /// <summary>把本地导入的 jar 也登记进 mods.json（没有元数据，用当前实例信息）。</summary>
    private void RegisterLocal(string path)
    {
        string fileName = Path.GetFileName(path);
        ModRegistry.Upsert(ModsDir, new ModRecord
        {
            Id = "local@" + BareName(fileName),
            Name = BareName(fileName)[..^".jar".Length],
            Loader = LoaderKind,
            McVersion = Shell.Meta.McVersion(Shell.Dir),
            File = fileName,
            Enabled = true,
        });
    }

    /// <summary>拖拽 .jar 到页面即导入。</summary>
    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (_loader.Length == 0)
        {
            UpdateStatus("当前版本未安装加载器，无法导入 Mod");
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        Directory.CreateDirectory(ModsDir);
        int count = 0;
        foreach (var item in items)
        {
            if (item is StorageFile file && file.FileType.Equals(".jar", StringComparison.OrdinalIgnoreCase))
            {
                string dest = Path.Combine(ModsDir, file.Name);
                File.Copy(file.Path, dest, true);
                RegisterLocal(dest);
                count++;
            }
        }
        RefreshList();
        UpdateStatus(count == 0 ? "没有可导入的 .jar 文件" : $"已导入 {count} 个");
    }

    private void ModToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.Tag is not ModItem item) return;
        if (ts.IsOn == item.IsEnabled) return; // 绑定初始化触发的，无需处理

        string oldPath = item.FilePath;
        string target = ts.IsOn
            ? oldPath[..^".disabled".Length]
            : oldPath + ".disabled";
        try
        {
            File.Move(oldPath, target);
            item.FilePath = target;
            item.IsEnabled = ts.IsOn;
            ModRegistry.SetEnabled(ModsDir, Path.GetFileName(oldPath), Path.GetFileName(target), ts.IsOn);
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
            ModRegistry.Remove(ModsDir, Path.GetFileName(item.FilePath));
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
