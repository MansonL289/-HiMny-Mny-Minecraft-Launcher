using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>服务端子页面：按官方版本清单选择版本，从版本 json 的 downloads.server 下载服务端 jar。</summary>
public sealed partial class ServerDownloadPage : Page
{
    private readonly List<(string Id, string Type)> _versions = new();
    private JsonObject? _manifest;
    private bool _refreshing;

    public ServerDownloadPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_versions.Count == 0 && !_refreshing)
        {
            Refresh_Click(RefreshButton, new RoutedEventArgs());
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        VersionList.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "";
        try
        {
            JsonObject manifest;
            try { manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: true); }
            catch { manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: false); }
            _manifest = manifest;

            _versions.Clear();
            foreach (var (id, _, _, type) in Manifest.Versions(manifest))
            {
                // 服务端只提供正式版与快照版（远古版没有独立服务端）
                if (type is "release" or "snapshot")
                {
                    _versions.Add((id, type));
                }
            }
            ApplyCategoryFilter();
            StatusText.Text = $"最新正式版: {Manifest.LatestRelease(manifest)}，共 {_versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            _manifest = null;
            StatusText.Text = $"获取版本清单失败: {ex.Message}（请检查网络或下载源设置）";
        }
        finally
        {
            _refreshing = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            VersionList.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCategoryFilter();

    private void ApplyCategoryFilter()
    {
        if (VersionList is null || CategoryBox.SelectedItem is not ComboBoxItem item) return;
        string type = item.Tag?.ToString() ?? "release";
        VersionList.ItemsSource = _versions
            .Where(v => v.Type == type)
            .Select(v => v.Id)
            .ToList();
        VersionList.SelectedIndex = -1;
        DownloadButton.IsEnabled = false;
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DownloadButton.IsEnabled = VersionList.SelectedItem is string;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not string versionId) return;
        DownloadButton.IsEnabled = false;
        StatusText.Text = "正在读取版本信息…";

        JsonObject versionJson;
        try
        {
            var manifest = _manifest ?? await FetchManifestAsync();
            _manifest = manifest;
            var (url, _) = Manifest.Find(manifest, versionId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            versionJson = JsonNode.Parse(await McHttp.Client.GetStringAsync(
                Mirror.Rewrite(url, AppSettings.DownloadMirror), cts.Token))!.AsObject();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取版本信息失败: {ex.Message}";
            DownloadButton.IsEnabled = true;
            return;
        }

        var server = versionJson["downloads"]?["server"]?.AsObject();
        if (server == null || server["url"] == null)
        {
            StatusText.Text = $"版本 {versionId} 没有可用的服务端文件（老版本可能不提供独立服务端下载）。";
            DownloadButton.IsEnabled = true;
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"server-{versionId}.jar",
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeChoices.Add("JAR 文件", new List<string> { ".jar" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            StatusText.Text = "已取消保存。";
            DownloadButton.IsEnabled = true;
            return;
        }

        long total = server["size"]?.GetValue<long>() ?? 0;
        long done = 0;
        DlProgress.Visibility = Visibility.Visible;
        DlProgress.IsIndeterminate = total <= 0;
        DlProgress.Value = 0;
        StatusText.Text = "正在下载服务端…";

        var task = new McDownloadTask(
            Mirror.Rewrite(server["url"]!.GetValue<string>(), AppSettings.DownloadMirror),
            file.Path,
            server["sha1"]?.GetValue<string>(),
            server["size"]?.GetValue<long>());
        try
        {
            await Downloader.DownloadFileAsync(task, n =>
            {
                done += n;
                if (total > 0)
                {
                    double percent = Math.Min(100, done * 100.0 / total);
                    DlProgress.IsIndeterminate = false;
                    DlProgress.Value = percent;
                    StatusText.Text = $"正在下载服务端… {percent:F1}%";
                }
                else
                {
                    StatusText.Text = $"正在下载服务端… {done / 1024.0 / 1024.0:F1} MB";
                }
            }, default);
            DlProgress.IsIndeterminate = false;
            DlProgress.Value = 100;
            StatusText.Text = $"已保存到 {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载失败: {ex.Message}";
        }
        finally
        {
            DlProgress.Visibility = Visibility.Collapsed;
            DownloadButton.IsEnabled = true;
        }
    }

    private static async Task<JsonObject> FetchManifestAsync()
    {
        try { return await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: true); }
        catch { return await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: false); }
    }

    private void OpenServerDir_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(McPaths.GameDir, "server");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        StatusText.Text = $"服务端文件夹: {dir}";
    }
}
