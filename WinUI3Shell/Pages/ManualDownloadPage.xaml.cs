using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>手动下载子页面：列出 Forge/Fabric/NeoForge 安装器版本，用另存为方式下载。</summary>
public sealed partial class ManualDownloadPage : Page
{
    private sealed record BuildItem(string Display, string Url, string FileName);

    private string _loader = "forge";
    private readonly List<BuildItem> _builds = new();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return c;
    }

    public ManualDownloadPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loader = e.Parameter as string ?? "forge";
        TitleText.Text = _loader switch
        {
            "fabric" => "Fabric 安装器",
            "neoforge" => "NeoForge 安装器",
            _ => "Forge 安装器",
        };

        switch (_loader)
        {
            case "forge": // 需要先选择游戏版本
                McVersionCombo.Visibility = Visibility.Visible;
                await LoadMcVersionsAsync();
                break;
            default: // fabric / neoforge 直接列出
                await LoadListAsync();
                break;
        }
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McVersionCombo.SelectedItem is string)
        {
            await LoadListAsync();
        }
    }

    // 填充 MC 正式版下拉框（从版本清单里筛 release）
    private async Task LoadMcVersionsAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "";
        try
        {
            var manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror);
            McVersionCombo.ItemsSource = Manifest.Versions(manifest, "release")
                .Select(v => v.Id).ToList();
            StatusText.Text = "选择游戏版本后列出对应的 Forge 构建。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取失败: {ex.Message}";
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadListAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        SaveSelectedButton.IsEnabled = false;
        _builds.Clear();
        BuildList.ItemsSource = null;
        StatusText.Text = "";
        try
        {
            switch (_loader)
            {
                case "forge":
                {
                    string mc = McVersionCombo.SelectedItem as string ?? "";
                    if (mc.Length == 0)
                    {
                        StatusText.Text = "请先选择游戏版本。";
                        return;
                    }
                    // BMCLAPI 聚合接口：某 MC 版本对应的全部 Forge 构建
                    var arr = JsonNode.Parse(await Http.GetStringAsync(
                        $"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}"))!.AsArray();
                    foreach (var node in arr.Reverse())
                    {
                        string v = node!["version"]!.GetValue<string>();
                        string file = $"forge-{mc}-{v}-installer.jar";
                        _builds.Add(new BuildItem($"Forge {v}（MC {mc}）",
                            $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{mc}-{v}/{file}", file));
                    }
                    break;
                }
                case "fabric":
                {
                    var arr = JsonNode.Parse(await Http.GetStringAsync(
                        "https://meta.fabricmc.net/v2/versions/installer"))!.AsArray();
                    foreach (var node in arr)
                    {
                        string v = node!["version"]!.GetValue<string>();
                        string file = $"fabric-installer-{v}.jar";
                        _builds.Add(new BuildItem($"Fabric Installer {v}",
                            $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{v}/{file}", file));
                    }
                    break;
                }
                case "neoforge":
                {
                    // 官方 maven 国内不可达，走 BMCLAPI 的 maven 镜像
                    var xml = XDocument.Parse(await Http.GetStringAsync(
                        "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml"));
                    var vers = xml.Root!.Element("versioning")!.Element("versions")!
                        .Elements("version").Select(x => x.Value).Reverse().Take(50);
                    foreach (string v in vers)
                    {
                        string file = $"neoforge-{v}-installer.jar";
                        _builds.Add(new BuildItem($"NeoForge {v}",
                            $"https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{v}/{file}", file));
                    }
                    break;
                }
            }
            BuildList.ItemsSource = _builds.Select(b => b.Display).ToList();
            StatusText.Text = _builds.Count > 0
                ? $"共 {_builds.Count} 个，选中后点击“另存为下载所选”。"
                : "没有找到可用版本。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取失败: {ex.Message}";
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
        if (BuildList.SelectedIndex < 0)
        {
            return;
        }
        var item = _builds[BuildList.SelectedIndex];
        await SaveAsAsync(item.Url, item.FileName);
    }

    // 另存为下载。注：系统 FileSavePicker 不支持自定义默认目录，
    // 只能给出建议文件名；我的世界文件夹为 %USERPROFILE%\.mnymc。
    private async Task SaveAsAsync(string url, string fileName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        picker.SuggestedFileName = fileName;
        picker.FileTypeChoices.Add("JAR 文件", new List<string> { ".jar" });
        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        StatusText.Text = "正在下载…";
        try
        {
            await Downloader.DownloadManyAsync(
                new List<McDownloadTask> { new(url, file.Path) }, 1, fileName, null, default);
            StatusText.Text = $"已保存到 {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载失败: {ex.Message}";
        }
    }
}
