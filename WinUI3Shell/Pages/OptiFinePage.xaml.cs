using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>下载页 - OptiFine / LiteLoader：给已装版本加优化加载器，不弹官方安装器 GUI。</summary>
public sealed partial class OptiFinePage : Page
{
    private List<OfChoice> _ofVersions = [];
    private List<WinUI3Shell.Services.Mc.LiteLoader.LlVersion> _llVersions = [];
    private string _mode = "optifine";

    public OptiFinePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string mode && mode == "liteloader")
        {
            _mode = "liteloader";
            OfSection.Visibility = Visibility.Collapsed;
            LlSection.Visibility = Visibility.Visible;
        }
        else
        {
            _mode = "optifine";
            OfSection.Visibility = Visibility.Visible;
            LlSection.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record OfChoice(string Text, OptiFine.OfVersion Version);

    private async void OptiFinePage_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        PageRing.Visibility = Visibility.Visible;
        BaseCombo.IsEnabled = false;
        try
        {
            // 只用「原版 + 完整」的版本作为基础，加载器版本不能打 OptiFine 补丁
            var items = new List<ComboBoxItem>();
            if (Directory.Exists(McPaths.VersionsDir))
            {
                foreach (string dir in Directory.GetDirectories(McPaths.VersionsDir).OrderByDescending(d => d))
                {
                    string name = Path.GetFileName(dir);
                    var meta = VersionMeta.Load(name);
                    if (meta == null || !meta.IsComplete(name)) continue;
                    if (meta.DetectLoader(name).Length > 0) continue;
                    items.Add(new ComboBoxItem { Content = $"{name}（{meta.McVersion(name)}）", Tag = name });
                }
            }
            BaseCombo.ItemsSource = items;
            BaseCombo.IsEnabled = items.Count > 0;
            if (items.Count > 0)
            {
                BaseCombo.SelectedIndex = 0;
            }
            else
            {
                StatusText.Text = "没有可用的原版版本，请先在「游戏」页下载一个原版版本。";
            }

            _llVersions = await WinUI3Shell.Services.Mc.LiteLoader.ListAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "获取版本列表失败：" + ex.Message;
        }
        finally
        {
            PageRing.Visibility = Visibility.Collapsed;
            PageRing.IsActive = false;
        }
        await LoadOfAsync();
    }

    private async Task LoadOfAsync()
    {
        if (!TryGetBase(out _, out string mcVersion)) return;

        if (_mode == "optifine")
        {
            _ofVersions = [];
            OfCombo.ItemsSource = null;
            OfCombo.PlaceholderText = "正在获取版本列表…";
            try
            {
                var list = await OptiFine.ListAsync(mcVersion);
                _ofVersions = list.Select(v => new OfChoice($"{v.Type} {v.Patch}", v)).ToList();
                OfCombo.ItemsSource = _ofVersions.Select(v => new ComboBoxItem { Content = v.Text, Tag = v }).ToList();
                OfCombo.PlaceholderText = _ofVersions.Count > 0 ? "选择 OptiFine 版本" : "该版本没有 OptiFine";
                if (_ofVersions.Count > 0) OfCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                OfCombo.PlaceholderText = "获取失败";
                StatusText.Text = "获取 OptiFine 版本失败：" + ex.Message;
            }
        }
        else
        {
            // LiteLoader：只有 1.12.2 及更早才有
            var candidates = _llVersions.Where(v => v.McVersion == mcVersion).ToList();
            LlCombo.ItemsSource = candidates
                .Select(v => new ComboBoxItem { Content = v.Version, Tag = v }).ToList();
            LlCombo.PlaceholderText = candidates.Count > 0 ? "选择 LiteLoader 版本" : "该版本没有 LiteLoader";
            if (candidates.Count > 0) LlCombo.SelectedIndex = 0;
            QueueLlButton.IsEnabled = candidates.Count > 0;
        }
    }

    private bool TryGetBase(out string baseDir, out string mcVersion)
    {
        baseDir = "";
        mcVersion = "";
        if ((BaseCombo.SelectedItem as ComboBoxItem)?.Tag is not string name || name.Length == 0) return false;
        var meta = VersionMeta.Load(name);
        if (meta == null) return false;
        baseDir = name;
        mcVersion = meta.McVersion(name);
        return true;
    }

    private async void BaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageRing.IsActive) return;
        await LoadOfAsync();
    }

    /// <summary>把选中的 OptiFine 安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueOf_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetBase(out _, out string mcVersion))
        {
            StatusText.Text = "请先选择一个基础版本（用于确定 OptiFine 对应的游戏版本）。";
            return;
        }
        if ((OfCombo.SelectedItem as ComboBoxItem)?.Tag is not OfChoice choice) return;

        var of = choice.Version;
        string url = $"https://bmclapi2.bangbang93.com/optifine/{mcVersion}/{of.Type}/{of.Patch}";
        DownloadManager.StartInstaller("optifine", mcVersion,
            $"OptiFine {mcVersion} {of.Type} {of.Patch}", url, of.FileName);
        StatusText.Text = $"「OptiFine {of.Type} {of.Patch}」已加入下载列表，完成后在库或顶部提示里安装。";
    }

    /// <summary>把选中的 LiteLoader 安装器加入下载列表：下载进库，完成后由顶部提示提供「安装」。</summary>
    private void QueueLl_Click(object sender, RoutedEventArgs e)
    {
        if ((LlCombo.SelectedItem as ComboBoxItem)?.Tag is not WinUI3Shell.Services.Mc.LiteLoader.LlVersion ll) return;

        DownloadManager.StartInstaller("liteloader", ll.McVersion,
            $"LiteLoader {ll.McVersion} {ll.Version}", ll.Url, ModFlow.LiteLoaderFileName(ll));
        StatusText.Text = $"「LiteLoader {ll.Version}」已加入下载列表，完成后在库或顶部提示里安装。";
    }
}
