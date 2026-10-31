using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;

namespace WinUI3Shell;

public partial class App : Application
{
    private Window? _window;

    /// <summary>当前主窗口，供页面访问窗口级操作（如切换主题）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>命令行 --launch 指定的版本名（桌面快捷方式用），空表示正常启动。</summary>
    public static string StartupVersion { get; private set; } = "";

    /// <summary>取走 --launch 参数，避免页面多次加载时重复自动启动。</summary>
    public static void ConsumeStartupVersion() => StartupVersion = "";

    public App()
    {
        InitializeComponent();
        // 全局未处理异常：弹出对话框，避免静默闪退（退出码 0xC000027B）。
        UnhandledException += (s, e) =>
        {
            e.Handled = true;
            LogCrash(e.Exception);
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "程序异常",
                    Content = e.Message + "\n\n" + e.Exception?.ToString(),
                    CloseButtonText = "继续",
                    XamlRoot = MainWindow?.Content?.XamlRoot,
                };
                _ = dlg.ShowAsync();
            }
            catch { /* 对话框也失败就忽略 */ }
        };
        // 后台任务未观察到的异常（fire-and-forget Task 抛错）：记日志，不让进程崩。
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            LogCrash(e.Exception);
        };
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MnyMC");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "crash.log");
            File.AppendAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* 写日志失败不影响用户 */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 桌面快捷方式传入：--launch "版本名"，启动后自动进入该版本
        var cli = Environment.GetCommandLineArgs();
        int launchIndex = Array.FindIndex(cli, a =>
            a.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        if (launchIndex >= 0 && launchIndex + 1 < cli.Length)
        {
            StartupVersion = cli[launchIndex + 1];
        }

        // 断点不续传：上次启动被关掉/强杀留下的半截下载（.part）直接清空
        _ = System.Threading.Tasks.Task.Run(Services.Mc.Downloader.CleanupPartialDownloads);

        // 注册系统通知通道（启动器不在前台时，下载完成会弹右下角通知）
        Services.Notifications.Register();

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

    /// <summary>全局默认 Java 路径；空 = 自动选择。</summary>
    public static string GlobalJavaPath
    {
        get => GetString("GlobalJavaPath") ?? "";
        set => SetString("GlobalJavaPath", value);
    }

    /// <summary>网络代理（如 http://127.0.0.1:7890）；空 = 不使用代理。</summary>
    public static string ProxyUrl
    {
        get => GetString("ProxyUrl") ?? "";
        set => SetString("ProxyUrl", value);
    }

    /// <summary>CurseForge API Key（用户自备）；空 = CurseForge 源不可用。</summary>
    public static string CurseForgeKey
    {
        get => GetString("CurseForgeKey") ?? "";
        set => SetString("CurseForgeKey", value);
    }

    /// <summary>下载镜像：auto（断点换源）/ bmclapi / official，启动页补下载文件也用此源。</summary>
    public static string DownloadMirror
    {
        get => GetString("DownloadMirror") ?? "auto";
        set => SetString("DownloadMirror", value);
    }

    /// <summary>下载限速（KB/s），0 = 不限速。</summary>
    public static int DownloadLimitKb
    {
        get => GetInt("DownloadLimitKb", 0);
        set => SetInt("DownloadLimitKb", Math.Max(value, 0));
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
