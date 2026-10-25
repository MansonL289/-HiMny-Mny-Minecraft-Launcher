using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI3Shell.Pages;

/// <summary>游戏设置：分配内存、游戏窗口大小（游戏启动时的窗口）、游戏语言。</summary>
public sealed partial class GameSettingsPage : Page
{
    // 初始化控件状态时避免触发保存逻辑
    private bool _initializing;
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public GameSettingsPage()
    {
        InitializeComponent();

        _initializing = true;

        // 游戏窗口大小：按保存值选中预设项
        string savedSize = $"{AppSettings.WindowWidth},{AppSettings.WindowHeight}";
        for (int i = 0; i < WindowSizeBox.Items.Count; i++)
        {
            if (((ComboBoxItem)WindowSizeBox.Items[i]).Tag?.ToString() == savedSize)
            {
                WindowSizeBox.SelectedIndex = i;
                break;
            }
        }
        if (WindowSizeBox.SelectedIndex < 0)
        {
            WindowSizeBox.SelectedIndex = 0; // 默认 854×480
        }

        // 游戏语言：跟随系统 + 所有系统区域设置
        LanguageBox.Items.Add(new ComboBoxItem { Content = "跟随系统", Tag = "" });
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures).OrderBy(c => c.NativeName))
        {
            LanguageBox.Items.Add(new ComboBoxItem
            {
                Content = $"{culture.NativeName} ({culture.Name})",
                Tag = culture.Name,
            });
        }
        string savedLang = AppSettings.GameLanguage;
        LanguageBox.SelectedIndex = 0;
        for (int i = 0; i < LanguageBox.Items.Count; i++)
        {
            if (((ComboBoxItem)LanguageBox.Items[i]).Tag?.ToString() == savedLang)
            {
                LanguageBox.SelectedIndex = i;
                break;
            }
        }

        MemoryModeRadio.SelectedIndex = AppSettings.MemoryMode == "custom" ? 1 : 0;
        MemoryBox.Value = AppSettings.MemoryMb;
        MemoryBox.IsEnabled = AppSettings.MemoryMode == "custom";

        _initializing = false;

        _memoryTimer.Tick += (_, _) => UpdateMemoryInfo();
        Loaded += (_, _) =>
        {
            UpdateMemoryInfo();
            _memoryTimer.Start();
        };
        Unloaded += (_, _) => _memoryTimer.Stop();
    }

    // 游戏窗口大小：只保存，启动时通过 --width/--height 传给游戏，不再调整启动器窗口
    private void WindowSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || WindowSizeBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string[] parts = (item.Tag?.ToString() ?? "854,480").Split(',');
        AppSettings.WindowWidth = int.Parse(parts[0]);
        AppSettings.WindowHeight = int.Parse(parts[1]);
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AppSettings.GameLanguage = item.Tag?.ToString() ?? "";
    }

    private void MemoryModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        AppSettings.MemoryMode = MemoryModeRadio.SelectedIndex == 1 ? "custom" : "auto";
        MemoryBox.IsEnabled = MemoryModeRadio.SelectedIndex == 1;
        UpdateMemoryInfo();
    }

    private void MemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(sender.Value))
        {
            return;
        }

        AppSettings.MemoryMb = (int)sender.Value;
        UpdateMemoryInfo(); // 自定义值变化时占用条实时跟随
    }

    // 实时内存信息与占用条：系统占用(深蓝) / 游戏分配(浅蓝) / 空闲(白)，每秒刷新
    private void UpdateMemoryInfo()
    {
        var (total, avail) = SysMemory.GetPhysicalMemoryMb();
        if (total <= 0)
        {
            return;
        }

        MemoryBox.Maximum = Math.Max(avail, 1024);
        if (MemoryBox.Value > MemoryBox.Maximum)
        {
            MemoryBox.Value = MemoryBox.Maximum;
        }

        long auto = Math.Clamp(avail * 2 / 3, 1024, Math.Max(avail, 1024));
        long game = AppSettings.MemoryMode == "custom"
            ? Math.Clamp((long)(double.IsNaN(MemoryBox.Value) ? 0 : MemoryBox.Value), 0, avail)
            : auto;
        long used = total - avail;
        long free = Math.Max(total - used - game, 0);

        MemoryUsedCol.Width = new GridLength(Math.Max(used, 1), GridUnitType.Star);
        MemoryGameCol.Width = new GridLength(Math.Max(game, 1), GridUnitType.Star);
        MemoryFreeCol.Width = new GridLength(Math.Max(free, 1), GridUnitType.Star);

        MemoryInfoText.Text = AppSettings.MemoryMode == "custom"
            ? $"总内存 {total} MB，系统占用 {used} MB，游戏分配 {game} MB，空闲 {free} MB（实时跟随）"
            : $"总内存 {total} MB，系统占用 {used} MB，将自动分配 {game} MB，空闲 {free} MB（实时跟随）";
    }
}
