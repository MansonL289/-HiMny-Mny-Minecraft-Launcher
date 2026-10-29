using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>Quilt 安装器下载子页面：列出 Quilt 官方安装器版本，用另存为方式下载。</summary>
public sealed partial class QuiltPage : Page
{
    private sealed record BuildItem(string Display, string Url, string FileName);

    private const string RepoBase = "https://maven.quiltmc.org/repository/release";
    private readonly List<BuildItem> _builds = new();

    public QuiltPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await LoadListAsync();
    }

    private async Task LoadListAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        SaveSelectedButton.IsEnabled = false;
        _builds.Clear();
        BuildList.ItemsSource = null;
        StatusText.Text = "正在获取版本列表…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var xml = XDocument.Parse(await McHttp.Client.GetStringAsync(
                $"{RepoBase}/org/quiltmc/quilt-installer/maven-metadata.xml", cts.Token));
            var versions = xml.Root?.Element("versioning")?.Element("versions")?
                .Elements("version").Select(x => x.Value).Reverse().Take(50) ?? Enumerable.Empty<string>();
            foreach (string v in versions)
            {
                string file = $"quilt-installer-{v}.jar";
                _builds.Add(new BuildItem($"Quilt Installer {v}",
                    $"{RepoBase}/org/quiltmc/quilt-installer/{v}/{file}", file));
            }
            BuildList.ItemsSource = _builds.Select(b => b.Display).ToList();
            StatusText.Text = _builds.Count > 0
                ? $"共 {_builds.Count} 个，选中后点击“另存为下载所选”。"
                : "没有找到可用的 Quilt 安装器版本。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取 Quilt 安装器列表失败: {ex.Message}（请检查网络）";
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSelectedButton.IsEnabled = BuildList.SelectedIndex >= 0;
    }

    private async void SaveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BuildList.SelectedIndex < 0) return;
        var item = _builds[BuildList.SelectedIndex];

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        picker.SuggestedFileName = item.FileName;
        picker.FileTypeChoices.Add("JAR 文件", new List<string> { ".jar" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        StatusText.Text = "正在下载…";
        try
        {
            await Downloader.DownloadManyAsync(
                new List<McDownloadTask> { new(item.Url, file.Path) }, 1, item.FileName, null, default);
            StatusText.Text = $"已保存到 {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载失败: {ex.Message}";
        }
    }
}
