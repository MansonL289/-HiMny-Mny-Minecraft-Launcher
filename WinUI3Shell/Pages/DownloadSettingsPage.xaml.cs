using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>下载设置：下载源（镜像）。</summary>
public sealed partial class DownloadSettingsPage : Page
{
    private bool _initializing;

    public DownloadSettingsPage()
    {
        InitializeComponent();

        _initializing = true;
        MirrorBox.SelectedIndex = AppSettings.DownloadMirror == Services.Mc.Mirror.Official ? 1 : 0;
        _initializing = false;
    }

    private void MirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MirrorBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.DownloadMirror = item.Tag?.ToString() ?? Services.Mc.Mirror.Bmclapi;
    }
}
