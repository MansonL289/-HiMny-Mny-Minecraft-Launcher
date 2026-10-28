using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>设置 - 修复与导入：检查并补齐版本缺失文件，从其他启动器导入版本与存档。</summary>
public sealed partial class RepairPage : Page
{
    /// <summary>导入列表行：包一层便于绑定「缺少文件」提示。</summary>
    public sealed class ImportRow
    {
        public ImportedVersion Version { get; init; } = new();
        public string Name => Version.Name;
        public string DetailText => Version.DetailText;
        public Visibility MissingVisibility => Version.IsComplete ? Visibility.Collapsed : Visibility.Visible;
    }

    private readonly ObservableCollection<ImportRow> _rows = new();
    private bool _busy;
    private bool _needsRepair;
    private string _root = "";

    public RepairPage()
    {
        InitializeComponent();
        ImportList.ItemsSource = _rows;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        RefreshVersions(null);
    }

    /// <summary>重新读取版本下拉框；prefer 为导入完成后希望选中的目录名。</summary>
    private void RefreshVersions(string? prefer)
    {
        VersionBox.Items.Clear();
        if (Directory.Exists(McPaths.VersionsDir))
        {
            foreach (string path in Directory.GetDirectories(McPaths.VersionsDir)
                         .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = Path.GetFileName(path)!;
                if (VersionMeta.Load(name) == null) continue;
                VersionBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
        }

        if (VersionBox.Items.Count == 0)
        {
            CheckButton.IsEnabled = false;
            CheckText.Text = "还没有可检查的版本";
            return;
        }

        CheckButton.IsEnabled = true;
        int index = 0;
        for (int i = 0; i < VersionBox.Items.Count; i++)
        {
            if ((VersionBox.Items[i] as ComboBoxItem)?.Tag as string == prefer)
            {
                index = i;
                break;
            }
        }
        VersionBox.SelectedIndex = index;
    }

    private void VersionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CheckText.Text = "";
        FixStatus.Text = "";
        _needsRepair = false;
        FixButton.IsEnabled = false;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || VersionBox.SelectedItem is not ComboBoxItem { Tag: string dir }) return;

        CheckText.Text = "正在检查…";
        FixStatus.Text = "";
        _needsRepair = false;
        FixButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => Repair.Check(dir, AppSettings.DownloadMirror));
            // 检查期间用户可能切换了版本：结果只对当前选中版本生效
            if ((VersionBox.SelectedItem as ComboBoxItem)?.Tag as string != dir)
            {
                return;
            }
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            CheckText.Text = "检查失败：" + ex.Message;
        }
    }

    private void ApplyReport(RepairReport report)
    {
        _needsRepair = report.NeedsRepair;
        CheckText.Text = report.NeedsRepair
            ? string.Join("\n", report.Problems.Select(p => "• " + p))
            : report.SummaryText;
        FixButton.IsEnabled = !_busy && _needsRepair;
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || VersionBox.SelectedItem is not ComboBoxItem { Tag: string dir }) return;

        SetBusy(true, "准备修复");
        var progress = new Progress<McProgress>(OnFixProgress);
        try
        {
            await Repair.FixAsync(dir, AppSettings.DownloadMirror, progress, CancellationToken.None);
            var report = await Task.Run(() => Repair.Check(dir, AppSettings.DownloadMirror));
            ApplyReport(report);
            if (!report.NeedsRepair)
            {
                FixStatus.Text = "修复完成";
            }
            else if (report.JarMissing)
            {
                FixStatus.Text = "客户端 jar 需要重新下载版本";
            }
            else
            {
                FixStatus.Text = "修复后仍有缺失：" + report.SummaryText;
            }
        }
        catch (Exception ex)
        {
            FixStatus.Text = "修复失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void OnFixProgress(McProgress p)
    {
        FixStatus.Text = p.TotalBytes > 0
            ? $"{p.Phase}: {p.DoneBytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MB ({p.DoneFiles}/{p.TotalFiles})"
            : p.Phase;
    }

    private async void PickRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _root = folder.Path;
        RootText.Text = _root;
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        SetBusy(true, null);
        StatusText.Text = "正在扫描…";
        _rows.Clear();
        try
        {
            var found = await Task.Run(() => GameImport.Scan(_root));
            foreach (var version in found)
            {
                _rows.Add(new ImportRow { Version = version });
            }
            StatusText.Text = found.Count == 0
                ? "没有找到可导入的版本（请选择含 versions 目录的 .minecraft 目录）"
                : $"发现 {found.Count} 个版本，可多选后导入";
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

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var selected = ImportList.SelectedItems.OfType<ImportRow>().Select(r => r.Version).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先在上面的列表中选择要导入的版本";
            return;
        }

        SetBusy(true, null);
        var imported = new List<string>();
        var failed = new List<string>();
        int saveCount = 0;
        var progress = new Progress<McProgress>(OnImportProgress);
        try
        {
            foreach (var version in selected)
            {
                try
                {
                    imported.Add(await GameImport.ImportVersionAsync(version, progress, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    failed.Add($"{version.Name}（{ex.Message}）");
                }
            }

            if (ImportSavesBox.IsChecked == true)
            {
                try
                {
                    saveCount = await GameImport.ImportSavesAsync(_root,
                        SaveManager.SavesDir(McPaths.GameDir), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failed.Add("存档（" + ex.Message + "）");
                }
            }

            var parts = new List<string>();
            if (imported.Count > 0) parts.Add($"已导入 {imported.Count} 个版本");
            if (saveCount > 0) parts.Add($"已导入 {saveCount} 个存档");
            if (failed.Count > 0) parts.Add("失败 " + string.Join("；", failed));
            StatusText.Text = parts.Count == 0 ? "没有导入任何内容" : string.Join("，", parts);
        }
        finally
        {
            SetBusy(false, null);
            RefreshVersions(imported.FirstOrDefault());
        }
    }

    private void OnImportProgress(McProgress p)
    {
        StatusText.Text = p.TotalFiles > 0
            ? $"{p.Phase}: {p.DoneFiles}/{p.TotalFiles} 个文件"
            : p.Phase;
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        VersionBox.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy && VersionBox.Items.Count > 0;
        FixButton.IsEnabled = !busy && _needsRepair;
        PickButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        ImportSavesBox.IsEnabled = !busy;
        if (status != null) FixStatus.Text = status;
    }
}
