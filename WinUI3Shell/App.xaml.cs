using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;

    /// <summary>当前主窗口，供页面访问窗口级操作（如切换主题）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 断点不续传：上次启动被关掉/强杀留下的半截下载（.part）直接清空
        _ = System.Threading.Tasks.Task.Run(Services.Mc.Downloader.CleanupPartialDownloads);

        var window = new MainWindow();
        MainWindow = window;
        window.ApplyTheme(AppSettings.Theme);

        _window = window;
        _window.Activate();
    }
}

/// <summary>保存在注册表 HKCU\Software\MnyMC 下的简单设置。</summary>
public static class AppSettings
{
    private const string SettingsKey = @"Software\MnyMC";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MnyMC";

    public static ElementTheme Theme
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            string? value = key?.GetValue("Theme") as string;
            return Enum.TryParse(value, out ElementTheme theme) ? theme : ElementTheme.Default;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("Theme", value.ToString());
        }
    }

    public static bool AutoStart
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) is not null;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exePath)
            {
                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else if (!value)
            {
                key.DeleteValue(RunValueName, false);
            }
        }
    }

    /// <summary>下载镜像：auto（断点换源）/ bmclapi / official，启动页补下载文件也用此源。</summary>
    public static string DownloadMirror
    {
        get => GetString("DownloadMirror") ?? "auto";
        set => SetString("DownloadMirror", value);
    }

    /// <summary>游戏窗口宽（启动时通过 --width 传给游戏）。</summary>
    public static int WindowWidth
    {
        get => GetInt("WindowWidth", 854);
        set => SetInt("WindowWidth", value);
    }

    /// <summary>游戏窗口高（启动时通过 --height 传给游戏）。</summary>
    public static int WindowHeight
    {
        get => GetInt("WindowHeight", 480);
        set => SetInt("WindowHeight", value);
    }

    /// <summary>内存分配模式：auto / custom。</summary>
    public static string MemoryMode
    {
        get => GetString("MemoryMode") ?? "auto";
        set => SetString("MemoryMode", value);
    }

    public static int MemoryMb
    {
        get => GetInt("MemoryMb", 2048);
        set => SetInt("MemoryMb", value);
    }

    /// <summary>游戏语言（如 zh-CN）；空字符串表示跟随系统。</summary>
    public static string GameLanguage
    {
        get => GetString("GameLanguage") ?? "";
        set => SetString("GameLanguage", value);
    }

    /// <summary>全局版本隔离策略：off / modded / unofficial / modded_unofficial / all。</summary>
    public static string VersionIsolation
    {
        get => GetString("VersionIsolation") ?? "off";
        set => SetString("VersionIsolation", value);
    }

    /// <summary>上次启动的版本名（首页默认选中用）。</summary>
    public static string LastVersion
    {
        get => GetString("LastVersion") ?? "";
        set => SetString("LastVersion", value);
    }

    /// <summary>上次启动的时间（Ticks），与最新下载时间比较决定默认选中。</summary>
    public static long LastLaunchTicks
    {
        get => long.TryParse(GetString("LastLaunchTicks"), out long ticks) ? ticks : 0;
        set => SetString("LastLaunchTicks", value.ToString());
    }

    /// <summary>启动参数 -Xmx 的值：自动=可用内存的 2/3，自定义=设置值（MB）。</summary>
    public static string MaxMemString()
    {
        if (MemoryMode == "custom")
        {
            return Math.Max(MemoryMb, 512) + "M";
        }
        var (_, avail) = SysMemory.GetPhysicalMemoryMb();
        return Math.Max(avail * 2 / 3, 1024) + "M";
    }

    private static int GetInt(string name, int def)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name) is int v ? v : def;
    }

    private static void SetInt(string name, int value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value);
    }

    private static string? GetString(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name) as string;
    }

    private static void SetString(string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value);
    }

    /// <summary>重置：删除本用户全部设置，并取消开机自启动。</summary>
    public static void Reset()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false);
        }
        catch { /* 删不掉就留着 */ }
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(RunValueName, false);
        }
        catch { /* 同上 */ }
    }
}

/// <summary>物理内存查询（GlobalMemoryStatusEx）。</summary>
internal static class SysMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    /// <summary>返回 (总内存 MB, 可用内存 MB)。可用 = 总内存 - 已使用。</summary>
    public static (long TotalMb, long AvailMb) GetPhysicalMemoryMb()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return (0, 0);
        }
        return ((long)(status.TotalPhys / 1048576), (long)(status.AvailPhys / 1048576));
    }
}
