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

        // 旧配置只有 Isolation 布尔值：曾经打开过的按「隔离」显示
        string isolation = meta.IsolationMode == "global" && meta.Isolation ? "on" : meta.IsolationMode;
        IsolationCombo.SelectedIndex = isolation switch
        {
            "on" => 1,
            "off" => 2,
            _ => 0,
        };

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
        JvmArgsBox.Text = meta.ExtraJvmArgs;
        GameArgsBox.Text = meta.ExtraGameArgs;
        _loading = false;
    }

    private VersionSettingsPage Shell => _shell!;

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var meta = Shell.Meta;

        string isolation = (IsolationCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "global";
        meta.IsolationMode = isolation;
        // 同步旧字段：旧版启动器只认 Isolation，尽量保持行为一致
        meta.Isolation = isolation == "on";

        bool customJava = (JavaCombo.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        JavaCustomPanel.Visibility = customJava ? Visibility.Visible : Visibility.Collapsed;
        meta.JavaPath = customJava ? JavaPathBox.Text : "";

        string mode = (MemoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "global";
        MemoryBox.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        meta.MemoryMode = mode;

        meta.AutoJoinServer = ServerBox.Text.Trim();
        meta.ExtraJvmArgs = JvmArgsBox.Text.Trim();
        meta.ExtraGameArgs = GameArgsBox.Text.Trim();
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

    // 直接编辑版本 json 原文（改错会导致启动失败，保存前先校验 JSON 是否可解析）
    private async void EditRawJson_Click(object sender, RoutedEventArgs e)
    {
        var meta = Shell.Meta;
        string jsonPath = meta.JsonPath(Shell.Dir);
        if (!System.IO.File.Exists(jsonPath))
        {
            await ShowMessageAsync("找不到版本文件", "版本 json 不存在，无法编辑。");
            return;
        }

        var box = new TextBox
        {
            Text = await System.IO.File.ReadAllTextAsync(jsonPath),
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Width = 720,
            Height = 460,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "版本 JSON",
            Content = new ScrollViewer { Content = box },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            System.Text.Json.Nodes.JsonNode.Parse(box.Text);
            await System.IO.File.WriteAllTextAsync(jsonPath, box.Text);
            await ShowMessageAsync("已保存", "版本 JSON 已更新，下次启动生效。");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("保存失败", "JSON 格式有误：" + ex.Message);
        }
    }

    // 创建桌面快捷方式：目标为启动器本体，参数 --launch "版本名"
    private async void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取启动器路径");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = System.IO.Path.Combine(desktop, $"MnyMC - {Shell.Dir}.lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("系统不支持创建快捷方式");
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk })!;
            var t = shortcut.GetType();
            t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            t.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                new object[] { $"--launch \"{Shell.Dir}\"" });
            t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                new object[] { System.IO.Path.GetDirectoryName(exe)! });
            t.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                new object[] { exe + ",0" });
            t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                new object[] { $"启动 MnyMC 版本 {Shell.Dir}" });
            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            await ShowMessageAsync("已创建", "桌面快捷方式：MnyMC - " + Shell.Dir);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("创建失败", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}
