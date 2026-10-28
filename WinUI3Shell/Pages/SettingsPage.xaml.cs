using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WinUI3Shell.Pages;

/// <summary>设置外壳：内部 Frame 承载设置主页与 游戏/下载/启动器/修复与导入/储存/关于 子页面，子页面内可继续下钻。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 外壳被外层手动缓存，重复进入时保留内部导航栈
        if (SettingsFrame.Content == null)
        {
            SettingsFrame.Navigate(typeof(SettingsHubPage));
        }
    }

    private void SettingsFrame_Navigated(object sender, NavigationEventArgs e)
    {
        BackButton.Visibility = SettingsFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = e.SourcePageType.Name switch
        {
            nameof(GameSettingsPage) => "游戏",
            nameof(DownloadSettingsPage) => "下载",
            nameof(LauncherSettingsPage) => "启动器",
            nameof(RepairPage) => "修复与导入",
            nameof(StoragePage) => "储存",
            nameof(StorageSavesPage) => "储存",
            nameof(AboutPage) => "关于",
            _ => "设置",
        };
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsFrame.CanGoBack)
        {
            SettingsFrame.GoBack();
        }
    }
}
