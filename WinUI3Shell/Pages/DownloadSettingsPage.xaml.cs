using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>下载设置：下载源（镜像）与下载限速。</summary>
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

        string limit = AppSettings.DownloadLimitKb.ToString();
        LimitBox.SelectedIndex = 0; // 兜底「不限速」
        for (int i = 0; i < LimitBox.Items.Count; i++)
        {
            if (LimitBox.Items[i] is ComboBoxItem item && item.Tag as string == limit)
            {
                LimitBox.SelectedIndex = i;
                break;
            }
        }
        _initializing = false;
        CfKeyBox.Password = AppSettings.CurseForgeKey;
    }

    private void MirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MirrorBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.DownloadMirror = item.Tag?.ToString() ?? Services.Mc.Mirror.Auto;
    }

    private void LimitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LimitBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.DownloadLimitKb = int.TryParse(item.Tag?.ToString(), out int kb) ? kb : 0;
    }

    private void CfKeyBox_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AppSettings.CurseForgeKey = CfKeyBox.Password.Trim();
    }
}
