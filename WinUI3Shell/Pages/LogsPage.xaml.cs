using System;
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
}
