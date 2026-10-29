using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>整合包安装：选本地 .mrpack → 预览 → 起后台任务（游戏本体 + 加载器 + 整合包文件）。</summary>
public sealed partial class ModpackPage : Page
{
    private ModpackInfo? _info;

    public ModpackPage()
    {
        InitializeComponent();
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        PickButton.IsEnabled = false;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        InfoPanel.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        StatusText.Text = "";

        try
        {
            string path = file.Path;
            var info = await Task.Run(() => Modpack.Load(path));
            _info = info;
            PackName.Text = string.IsNullOrWhiteSpace(info.Version) ? info.Name : $"{info.Name} {info.Version}";
            PackMeta.Text = info.SummaryText;
            InstallNameBox.Text = Modpack.SuggestInstallName(info);
            InfoPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _info = null;
            EmptyText.Text = "无法读取这个整合包";
            EmptyHint.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PickButton.IsEnabled = true;
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;

        string name = InstallNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "请输入安装名。";
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = "安装名不能包含 \\ / : * ? \" < > | 等字符。";
            return;
        }
        if (Directory.Exists(VersionMeta.DirPath(name)))
        {
            StatusText.Text = "已存在同名版本，请换个安装名。";
            return;
        }
        if (DownloadManager.Jobs.Any(j => !j.Done && j.InstallName == name))
        {
            StatusText.Text = "已有同名任务正在下载。";
            return;
        }

        DownloadManager.StartModpack(_info, name, AppSettings.DownloadMirror);
        StatusText.Text = "已加入下载任务，可在「下载任务」查看进度。";
        App.MainWindow?.ShowDownloadTasks();
    }
}
