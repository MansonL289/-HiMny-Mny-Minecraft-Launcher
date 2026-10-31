using System;
using System.Collections.Generic;
using Microsoft.Win32;
using Windows.ApplicationModel.Activation;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WinUI3Shell.Pages;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Services;

/// <summary>系统通知（Windows 右下角弹窗）：启动器不在前台时，下载完成额外弹一条。</summary>
public static class Notifications
{
    /// <summary>启动器固定的 AppUserModelID（非打包应用弹通知必须有）。</summary>
    public const string AppId = "MnyMC.Launcher";

    /// <summary>通知 tag → 任务（点按钮时据此知道要查看/安装哪个任务）。</summary>
    private static readonly Dictionary<string, DownloadJob> Pending = new();
    private static ToastNotifier? _notifier;

    /// <summary>启动时准备好通知通道：注册 AUMID、取得通知器。失败时静默降级为只有应用内横幅。</summary>
    public static void Register()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            EnsureAumidRegistered();
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception ex)
        {
            _notifier = null;
            Log(ex);
        }
    }

    /// <summary>弹一条下载完成通知：正文 + 查看按钮（MOD / 安装器再带安装按钮）。</summary>
    public static void ShowDownloadDone(DownloadJob job)
    {
        if (_notifier == null) return;
        try
        {
            string tag = Guid.NewGuid().ToString("N");
            string text = DownloadManager.BannerText(job);
            // MOD / 安装器下载完成后可以直接安装；安装任务本身、失败或取消的不给安装入口
            bool installable = job.Error == null && !job.Cancelled && !job.IsInstallAction
                && (job.IsMod || job.IsInstaller);

            lock (Pending)
            {
                Pending[tag] = job;
                // 长时间运行时防止无限增长（旧通知点了也只剩查看）
                if (Pending.Count > 20) Pending.Clear();
                Pending[tag] = job;
            }

            var doc = new XmlDocument();
            doc.LoadXml(BuildToastXml(text, tag, installable));
            var toast = new ToastNotification(doc) { Tag = tag };
            toast.Activated += OnActivated;
            _notifier.Show(toast);
        }
        catch (Exception ex)
        {
            // 通知失败不影响下载流程，写日志便于排查
            Log(ex);
        }
    }

    /// <summary>下载完成通知的 XML（ToastGeneric + 按钮）。</summary>
    private static string BuildToastXml(string text, string tag, bool installable)
    {
        string actions = installable
            ? $"<action content=\"安装\" arguments=\"install|{tag}\" activationType=\"foreground\"/>"
            : "";
        actions += $"<action content=\"查看\" arguments=\"view|{tag}\" activationType=\"foreground\"/>";
        return $@"<toast activationType=""foreground"" launch=""view|{tag}"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>MnyLauncher</text>
      <text>{Escape(text)}</text>
    </binding>
  </visual>
  <actions>{actions}</actions>
</toast>";
    }

    private static string Escape(string text) =>
        System.Security.SecurityElement.Escape(text) ?? text;

    /// <summary>点了通知正文或按钮：回到启动器，跳到对应栏目或直接开始安装。</summary>
    private static void OnActivated(ToastNotification sender, object args)
    {
        string argument = args is ToastNotificationActivatedEventArgs e ? e.Argument : "";
        string action = "view";
        string tag = "";
        var parts = argument.Split('|', 2);
        if (parts.Length == 2)
        {
            action = parts[0];
            tag = parts[1];
        }

        DownloadJob? job = null;
        lock (Pending)
        {
            if (tag.Length > 0 && Pending.TryGetValue(tag, out var found))
            {
                job = found;
                Pending.Remove(tag);
            }
        }

        var window = App.MainWindow;
        if (window == null) return;
        // 通知回调在后台线程，切回 UI 线程再操作窗口
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            if (action == "install" && job != null)
            {
                if (window.Content?.XamlRoot is { } root)
                {
                    _ = ModFlow.InstallFromJobAsync(root, job);
                }
            }
            else
            {
                window.ShowLibraryFor(job);
            }
        });
    }

    /// <summary>在注册表里登记 AUMID（DisplayName + 图标），非打包应用靠它让通知认得自己。</summary>
    private static void EnsureAumidRegistered()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "MnyLauncher");
        string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon)) key.SetValue("IconUri", icon);
    }

    /// <summary>把通知相关的异常写到 notify.log（不参与 UI）。</summary>
    private static void Log(Exception? ex, string? note = null)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MnyMC");
            System.IO.Directory.CreateDirectory(dir);
            string text = ex?.ToString() ?? note ?? "未知通知问题";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "notify.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch { /* 写日志失败忽略 */ }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
