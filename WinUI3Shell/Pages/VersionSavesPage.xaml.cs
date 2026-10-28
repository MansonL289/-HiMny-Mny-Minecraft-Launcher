using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本设置 - 存档管理：列出存档，支持查看全部信息、修改模式/作弊/游戏规则、删除。
/// 存档目录跟随版本隔离设置。</summary>
public sealed partial class VersionSavesPage : Page
{
    private VersionSettingsPage? _shell;
    private readonly ObservableCollection<SaveInfo> _saves = new();

    public VersionSavesPage()
    {
        InitializeComponent();
    }

    /// <summary>供 x:Bind 的列表源。</summary>
    public ObservableCollection<SaveInfo> Saves => _saves;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        _ = RefreshAsync();
    }

    private VersionSettingsPage Shell => _shell!;
    private string SavesDir => SaveManager.SavesDir(Shell.Meta.GameDir(Shell.Dir));

    private async Task RefreshAsync()
    {
        string dir = SavesDir;
        StatusText.Text = "正在读取存档…";
        List<SaveInfo> list;
        try
        {
            list = await Task.Run(() => SaveManager.Enumerate(Shell.Meta.GameDir(Shell.Dir)));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取存档失败: {ex.Message}";
            return;
        }
        // 页面可能已切走，丢弃过期结果
        if (_shell == null || dir != SavesDir) return;

        _saves.Clear();
        foreach (var info in list) _saves.Add(info);
        UpdateEmptyState();
        StatusText.Text = _saves.Count == 0
            ? $"存档目录：{dir}"
            : $"共 {_saves.Count} 个存档　{dir}";
    }

    private void UpdateEmptyState()
    {
        bool empty = _saves.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SaveList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenSavesDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SavesDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SavesDir}\"") { UseShellExecute = true });
    }

    private void OpenBackupsDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(McPaths.BackupsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{McPaths.BackupsDir}\"") { UseShellExecute = true });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ========== 更多菜单（存档备份 / 导出 / 打开文件夹 / 删除） ==========

    private void MoreSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveInfo info } button) return;

        var flyout = new MenuFlyout();
        var backup = new MenuFlyoutItem { Text = "备份到本地" };
        backup.Click += async (_, _) => await BackupAsync(info);
        flyout.Items.Add(backup);

        var export = new MenuFlyoutItem { Text = "导出为 zip" };
        export.Click += async (_, _) => await ExportAsync(info);
        flyout.Items.Add(export);

        var open = new MenuFlyoutItem { Text = "打开文件夹" };
        open.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{info.SavePath}\"") { UseShellExecute = true });
        flyout.Items.Add(open);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += (_, _) => _ = DeleteConfirmedAsync(info);
        flyout.Items.Add(delete);

        flyout.ShowAt(button);
    }

    private async Task BackupAsync(SaveInfo info)
    {
        StatusText.Text = "正在备份…";
        try
        {
            string zip = await SaveBackup.BackupAsync(info.SavePath);
            StatusText.Text = $"已备份：{zip}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"备份失败: {ex.Message}";
        }
    }

    private async Task ExportAsync(SaveInfo info)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = info.FolderName,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
        };
        picker.FileTypeChoices.Add("存档压缩包", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        StatusText.Text = "正在导出…";
        try
        {
            await SaveBackup.ExportAsync(info.SavePath, file.Path);
            StatusText.Text = $"已导出：{file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败: {ex.Message}";
        }
    }

    private async void ImportSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;

        StatusText.Text = "正在导入…";
        int ok = 0;
        var failed = new List<string>();
        foreach (var file in files)
        {
            try
            {
                await SaveBackup.ImportAsync(file.Path, SavesDir);
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add($"{file.Name}（{ex.Message}）");
            }
        }
        await RefreshAsync();
        StatusText.Text = failed.Count == 0
            ? $"已导入 {ok} 个存档"
            : $"已导入 {ok} 个，失败 {failed.Count} 个：{string.Join("；", failed)}";
    }


    // ========== 查看 ==========

    private async void ViewSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveInfo info }) return;
        await ShowInfoDialogAsync(info);
    }

    private async Task ShowInfoDialogAsync(SaveInfo info)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };

        AddSection(panel, "基本信息");
        AddRow(panel, "存档名", string.IsNullOrWhiteSpace(info.LevelName) ? "（未命名）" : info.LevelName);
        AddRow(panel, "文件夹", info.FolderName);
        AddRow(panel, "游戏模式", info.GameModeText);
        AddRow(panel, "难度", info.DifficultyText);
        AddRow(panel, "允许作弊", info.CheatsText);
        AddRow(panel, "极限模式", info.HardcoreText);
        AddRow(panel, "存档版本", info.VersionText);

        AddSection(panel, "世界");
        AddRow(panel, "最后游玩", info.LastPlayedText);
        AddRow(panel, "游戏内时间", info.DayText);
        AddRow(panel, "累计时长", info.PlayTimeText);
        AddRow(panel, "出生点", info.SpawnText);
        AddRow(panel, "世界种子", info.SeedText);

        AddSection(panel, "文件");
        AddRow(panel, "存档大小", info.SizeText);
        AddRow(panel, "区域文件", info.RegionText);
        AddRow(panel, "玩家数据", info.PlayerDataText);
        AddRow(panel, "存档路径", info.SavePath);

        AddSection(panel, $"游戏规则（{info.Rules.Count} 条）");
        if (info.Rules.Count == 0)
        {
            AddRow(panel, "—", "此存档没有游戏规则数据");
        }
        else
        {
            foreach (var rule in info.Rules)
            {
                AddRow(panel, rule.DisplayName, rule.ValueText);
            }
        }

        var dialog = new ContentDialog
        {
            Title = info.DisplayName,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 460,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static void AddSection(StackPanel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 10, 0, 2),
        });
    }

    private static void AddRow(StackPanel panel, string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(112) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        panel.Children.Add(grid);
    }

    // ========== 修改 ==========

    private async void EditSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveInfo info }) return;
        var draft = await Task.Run(() => SaveDraft.Load(info.SavePath));
        if (draft == null)
        {
            StatusText.Text = "无法读取该存档的 level.dat（文件损坏或被占用）。";
            return;
        }

        var dialog = new SaveEditDialog(draft) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            StatusText.Text = "已保存修改，游戏下次打开该存档时生效。";
            await RefreshAsync();
        }
    }

    // ========== 删除 ==========

    private async Task DeleteConfirmedAsync(SaveInfo info)
    {
        var confirm = new ContentDialog
        {
            Title = "删除存档",
            Content = $"确定删除存档「{info.DisplayName}」吗？存档文件夹将一并删除，不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await Task.Run(() => SaveManager.Delete(info.SavePath));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败: {ex.Message}";
            return;
        }
        _saves.Remove(info);
        UpdateEmptyState();
        StatusText.Text = _saves.Count == 0
            ? $"存档目录：{SavesDir}"
            : $"共 {_saves.Count} 个存档　{SavesDir}";
    }
}
