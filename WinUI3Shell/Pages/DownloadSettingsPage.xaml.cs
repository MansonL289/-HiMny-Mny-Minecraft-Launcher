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
        string current = AppSettings.DownloadMirror;
        for (int i = 0; i < MirrorBox.Items.Count; i++)
        {
            if (MirrorBox.Items[i] is ComboBoxItem item && item.Tag as string == current)
            {
                MirrorBox.SelectedIndex = i;
                break;
            }
        }
        _initializing = false;
    }

    private void MirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MirrorBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.DownloadMirror = item.Tag?.ToString() ?? Services.Mc.Mirror.Auto;
    }
}
