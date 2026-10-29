using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本设置 - 常规：改名、置顶、文件夹快捷方式、导出启动脚本、删除版本。</summary>
public sealed partial class VersionGeneralPage : Page
{
    private VersionSettingsPage? _shell;

    public VersionGeneralPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        NameBox.Text = _shell.Dir;
        PinToggle.IsOn = _shell.Meta.Pinned;
        // Mod 文件夹存在才可点，否则置灰
        ModsDirButton.IsEnabled = Directory.Exists(ModsDir());
    }

    private VersionSettingsPage Shell => _shell!;

    private string GameDir() => Shell.Meta.GameDir(Shell.Dir);
    private string ModsDir() => Path.Combine(GameDir(), "mods");

    // ========== 改名 ==========

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name == Shell.Dir) return;
        if (name.Length == 0)
        {
            StatusText.Text = "名字不能为空。";
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = "名字不能包含 \\ / : * ? \" < > | 字符。";
            return;
        }
        if (Directory.Exists(VersionMeta.DirPath(name)) || DownloadManager.IsNameOrVersionBusy(name, ""))
        {
            StatusText.Text = "已存在同名版本，请换个名字。";
            return;
        }
        try
        {
            Directory.Move(VersionMeta.DirPath(Shell.Dir), VersionMeta.DirPath(name));
            Shell.ApplyRename(name);
            StatusText.Text = $"已改名为 {name}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"改名失败: {ex.Message}";
        }
    }

    private void PinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Shell.Meta.Pinned = PinToggle.IsOn;
        Shell.SaveMeta();
    }

    // ========== 自定义图标 ==========

    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".ico");
        picker.FileTypeFilter.Add(".jpg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            string dest = Path.Combine(VersionMeta.DirPath(Shell.Dir), "icon.png");
            File.Copy(file.Path, dest, true);
            Shell.Meta.Icon = "icon.png";
            Shell.SaveMeta();
            StatusText.Text = $"已使用 {file.Name} 作为版本图标。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"更改图标失败: {ex.Message}";
        }
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (string name in new[] { "icon.png", "icon.ico" })
            {
                string path = Path.Combine(VersionMeta.DirPath(Shell.Dir), name);
                if (File.Exists(path)) File.Delete(path);
            }
            Shell.Meta.Icon = "";
            Shell.SaveMeta();
            StatusText.Text = "已清除自定义图标，恢复默认图标。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清除图标失败: {ex.Message}";
        }
    }

    // ========== 快捷方式 ==========

    private static void OpenFolder(string path, bool createIfMissing)
    {
        if (createIfMissing)
        {
            Directory.CreateDirectory(path);
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenVersionDir_Click(object sender, RoutedEventArgs e)
        => OpenFolder(VersionMeta.DirPath(Shell.Dir), true);

    private void OpenSavesDir_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(GameDir(), "saves"), true);

    private void OpenModsDir_Click(object sender, RoutedEventArgs e)
        => OpenFolder(ModsDir(), false);

    private void OpenScreenshotsDir_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.Combine(GameDir(), "screenshots"), true);

    // ========== 高级 ==========

    /// <summary>把当前版本导出成 Modrinth 整合包（.mrpack）。</summary>
    private async void ExportPack_Click(object sender, RoutedEventArgs e)
    {
        string note = "";
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"{Shell.Dir}-1.0.0.mrpack",
                SuggestedStartLocation = PickerLocationId.Desktop,
            };
            picker.FileTypeChoices.Add("Modrinth 整合包", new[] { ".mrpack" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            ExportPackButton.IsEnabled = false;
            ExportScriptButton.IsEnabled = false;
            ExportRing.Visibility = Visibility.Visible;
            ExportRing.IsActive = true;
            StatusText.Text = "正在导出整合包...";

            var progress = new Progress<McProgress>(p =>
            {
                bool finished = p.Phase.StartsWith("导出完成", StringComparison.Ordinal);
                if (finished) note = p.Phase;
                StatusText.Text = finished
                    ? p.Phase
                    : $"正在导出整合包（{p.DoneFiles}/{p.TotalFiles}）";
            });
            string path = await ModpackExport.ExportAsync(Shell.Dir, file.Path, progress, CancellationToken.None);
            StatusText.Text = note.Contains("已跳过")
                ? $"已导出到 {path}。{note}"
                : $"已导出到 {path}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败: {ex.Message}";
        }
        finally
        {
            ExportRing.IsActive = false;
            ExportRing.Visibility = Visibility.Collapsed;
            ExportPackButton.IsEnabled = true;
            ExportScriptButton.IsEnabled = true;
        }
    }

    private async void ExportScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string jsonPath = Shell.Meta.JsonPath(Shell.Dir);
            if (!File.Exists(jsonPath))
            {
                StatusText.Text = "版本文件不完整，无法导出。";
                return;
            }
            var versionJson = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(jsonPath))!;
            var (javaMin, javaMax) = JavaService.RequiredRange(versionJson);
            string java = await Shell.Meta.ResolveJavaAsync(javaMin, javaMax, null, default);
            string gameDir = GameDir();
            var cmd = LaunchService.BuildCommand(versionJson, java, "Steve", Shell.Meta.MaxMemString(), gameDir,
                AppSettings.WindowWidth, AppSettings.WindowHeight);
            if (!string.IsNullOrWhiteSpace(Shell.Meta.AutoJoinServer))
            {
                var parts = Shell.Meta.AutoJoinServer.Trim().Split(':', 2);
                cmd.Add("--server");
                cmd.Add(parts[0]);
                cmd.Add("--port");
                cmd.Add(parts.Length > 1 && int.TryParse(parts[1], out int port) ? port.ToString() : "25565");
            }

            var picker = new FileSavePicker
            {
                SuggestedFileName = $"启动 {Shell.Dir}",
                SuggestedStartLocation = PickerLocationId.Desktop,
            };
            picker.FileTypeChoices.Add("批处理文件", new[] { ".bat" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"cd /d \"{gameDir}\"");
            // cmd[0] 就是 java 路径
            sb.AppendLine(string.Join(" ", cmd.Select(Quote)));
            sb.AppendLine("pause");
            await File.WriteAllTextAsync(file.Path, sb.ToString(), Encoding.Default);
            StatusText.Text = $"已导出到 {file.Path}（玩家名写死为 Steve，可自行编辑 bat 修改）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败: {ex.Message}";
        }
    }

    private static string Quote(string s) => $"\"{s}\"";

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadManager.IsNameOrVersionBusy(Shell.Dir, Shell.Meta.Id))
        {
            StatusText.Text = "该版本正在下载，不能删除。";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定删除 {Shell.Dir} 吗？版本文件将一并删除，不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            Directory.Delete(VersionMeta.DirPath(Shell.Dir), true);
            App.MainWindow?.ShowHome();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败: {ex.Message}";
        }
    }
}
