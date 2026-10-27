using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>设置主页：游戏 / 下载 / 启动器 / 储存 / 关于 五个分类入口。</summary>
public sealed partial class SettingsHubPage : Page
{
    public SettingsHubPage()
    {
        InitializeComponent();
    }

    private void OpenGame_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(GameSettingsPage));

    private void OpenDownload_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(DownloadSettingsPage));

    private void OpenLauncher_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(LauncherSettingsPage));

    private void OpenStorage_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(StoragePage));

    private void OpenAbout_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(AboutPage));
}
