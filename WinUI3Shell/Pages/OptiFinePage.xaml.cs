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
    private bool _busy;
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
        if (!TryGetBase(out string baseDir, out string mcVersion)) return;

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
                NameBox.Text = $"{baseDir}-OptiFine";
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
            InstallLlButton.IsEnabled = candidates.Count > 0;
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

    private async void InstallOf_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryGetBase(out string baseDir, out string mcVersion))
        {
            await ShowAsync("请选择基础版本", "需要选一个已下载完整的原版版本。");
            return;
        }
        if ((OfCombo.SelectedItem as ComboBoxItem)?.Tag is not OfChoice choice)
        {
            await ShowAsync("请选择 OptiFine 版本", "版本列表为空，可能是网络问题。");
            return;
        }
        string installName = NameBox.Text.Trim();
        if (installName.Length == 0) installName = $"{baseDir}-OptiFine";
        if (Directory.Exists(VersionMeta.DirPath(installName)))
        {
            await ShowAsync("名称已存在", $"版本「{installName}」已经存在，请换一个安装名称。");
            return;
        }

        _busy = true;
        SetBusy(OfRing, InstallOfButton, true);
        try
        {
            var progress = new Progress<McProgress>(p =>
                StatusText.Text = p.TotalFiles > 0 ? $"{p.Phase} ({p.DoneFiles}/{p.TotalFiles})" : p.Phase);
            await OptiFine.InstallAsync(baseDir, mcVersion, choice.Version, installName, progress, CancellationToken.None);
            StatusText.Text = $"已安装到版本「{installName}」，回首页即可选择启动。";
            GameLanguages.Invalidate();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            SetBusy(OfRing, InstallOfButton, false);
            await LoadOfAsync();
        }
    }

    private async void InstallLl_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryGetBase(out string baseDir, out _))
        {
            await ShowAsync("请选择基础版本", "需要选一个已下载完整的原版版本。");
            return;
        }
        if ((LlCombo.SelectedItem as ComboBoxItem)?.Tag is not WinUI3Shell.Services.Mc.LiteLoader.LlVersion ll)
        {
            await ShowAsync("请选择 LiteLoader 版本", "版本列表为空，可能是网络问题。");
            return;
        }
        string installName = $"{baseDir}-LiteLoader";
        if (Directory.Exists(VersionMeta.DirPath(installName)))
        {
            await ShowAsync("名称已存在", $"版本「{installName}」已经存在，请先删除旧版本。");
            return;
        }

        _busy = true;
        SetBusy(LlRing, InstallLlButton, true);
        try
        {
            var progress = new Progress<McProgress>(p =>
                StatusText.Text = p.TotalFiles > 0 ? $"{p.Phase} ({p.DoneFiles}/{p.TotalFiles})" : p.Phase);
            await WinUI3Shell.Services.Mc.LiteLoader.InstallAsync(baseDir, ll, installName, progress, CancellationToken.None);
            StatusText.Text = $"已安装到版本「{installName}」，回首页即可选择启动。";
            GameLanguages.Invalidate();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            SetBusy(LlRing, InstallLlButton, false);
        }
    }

    private static void SetBusy(ProgressRing ring, Button button, bool busy)
    {
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = !busy;
    }

    private async Task ShowAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}
