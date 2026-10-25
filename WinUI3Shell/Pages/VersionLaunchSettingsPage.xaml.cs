using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本设置 - 设置：版本隔离、Java、内存分配、自动加入服务器。改动即时保存。</summary>
public sealed partial class VersionLaunchSettingsPage : Page
{
    private VersionSettingsPage? _shell;
    private bool _loading = true;

    public VersionLaunchSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        var meta = _shell.Meta;

        IsolationToggle.IsOn = meta.Isolation;

        JavaCombo.SelectedIndex = string.IsNullOrEmpty(meta.JavaPath) ? 0 : 1;
        JavaPathBox.Text = meta.JavaPath;
        JavaCustomPanel.Visibility = JavaCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        MemoryCombo.SelectedIndex = meta.MemoryMode switch
        {
            "auto" => 1,
            "custom" => 2,
            _ => 0,
        };
        MemoryBox.Value = meta.MemoryMb;
        MemoryBox.Visibility = meta.MemoryMode == "custom" ? Visibility.Visible : Visibility.Collapsed;

        ServerBox.Text = meta.AutoJoinServer;
        _loading = false;
    }

    private VersionSettingsPage Shell => _shell!;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var meta = Shell.Meta;

        meta.Isolation = IsolationToggle.IsOn;

        bool customJava = (JavaCombo.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        JavaCustomPanel.Visibility = customJava ? Visibility.Visible : Visibility.Collapsed;
        meta.JavaPath = customJava ? JavaPathBox.Text : "";

        string mode = (MemoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "global";
        MemoryBox.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        meta.MemoryMode = mode;

        meta.AutoJoinServer = ServerBox.Text.Trim();
        Shell.SaveMeta();
    }

    private void MemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        Shell.Meta.MemoryMb = (int)args.NewValue;
        Shell.SaveMeta();
    }

    private async void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        JavaPathBox.Text = file.Path;
        if (!_loading)
        {
            Shell.Meta.JavaPath = file.Path;
            Shell.SaveMeta();
        }
    }

    private void ClearJava_Click(object sender, RoutedEventArgs e)
    {
        JavaCombo.SelectedIndex = 0; // 触发 Setting_Changed 保存为空（自动选择）
    }
}
