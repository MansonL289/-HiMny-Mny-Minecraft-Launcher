using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>设置主页：游戏 / 下载 / 启动器 / 修复与导入 / 日志 / 储存 / 关于 七个分类入口。</summary>
public sealed partial class SettingsHubPage : Page
{
    public SettingsHubPage()
    {
        InitializeComponent();
    }

    private void OpenGame_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(GameSettingsPage));

    private void OpenDownload_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DownloadSettingsPage));

    private void OpenLauncher_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(LauncherSettingsPage));

    private void OpenRepair_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(RepairPage));

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(LogsPage));

    private void OpenStorage_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(StoragePage));

    private void OpenAbout_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(AboutPage));
}
