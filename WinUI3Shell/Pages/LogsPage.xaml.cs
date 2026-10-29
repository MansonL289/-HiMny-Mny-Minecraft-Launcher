using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>设置 - 日志：查看最新日志 / 崩溃报告，附中文结论。</summary>
public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
    }

    private async void LogsPage_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        RefreshButton.IsEnabled = false;

        var report = await Task.Run(() => LogAnalyzer.Analyze(LogAnalyzer.ReadLog(McPaths.GameDir)));
        HeadlineText.Text = report.Headline;
        FindingsList.ItemsSource = report.Findings.Select(f => "• " + f).ToList();
        LogBox.Text = report.Text;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = true;
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        string logDir = Path.Combine(McPaths.GameDir, "logs");
        Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDir}\"") { UseShellExecute = true });
    }

    /// <summary>导出诊断报告：日志 + 版本元数据 + 设置快照 + 硬件信息，打包成 zip。</summary>
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = "mnymc-diagnostics-" + DateTime.Now.ToString("yyyyMMdd"),
        };
        picker.FileTypeChoices.Add("Zip 压缩包", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        ExportButton.IsEnabled = false;
        StatusText.Text = "正在导出诊断报告…";
        try
        {
            await Diagnostics.ExportAsync(file.Path);
            StatusText.Text = "诊断报告已保存：" + file.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
