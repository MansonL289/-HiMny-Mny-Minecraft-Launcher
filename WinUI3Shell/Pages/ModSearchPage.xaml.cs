using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>一条搜索结果（已格式化，供列表绑定）。</summary>
public sealed class ModSearchItem : System.ComponentModel.INotifyPropertyChanged
{
    public ModrinthProject Source { get; set; } = new();

    // 名字与简介先显示原文，后台翻译完成后原地刷新
    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new(nameof(Title)));
        }
    }

    public string IconUrl { get; set; } = "";
    public string AuthorText { get; set; } = "";

    // 简介先显示原文，后台翻译完成后原地刷新
    private string _description = "";
    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            PropertyChanged?.Invoke(this, new(nameof(Description)));
        }
    }

    public string MetaText { get; set; } = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>MOD 页：默认显示热门 Mod，也可搜索；点击某个 Mod 进入详情子页选版本下载。</summary>
public sealed partial class ModSearchPage : Page
{
    private readonly ObservableCollection<ModSearchItem> _results = new();
    private CancellationTokenSource? _cts;
    private bool _loadedOnce;

    public ModSearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        _ = SearchAsync();
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    /// <summary>切换加载器筛选立即重新搜索（首次加载前的初始化选择不触发）。</summary>
    private void LoaderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadedOnce)
        {
            _ = SearchAsync();
        }
    }

    private string SelectedLoader() =>
        (LoaderFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SearchButton.IsEnabled = false;
        ResultList.IsEnabled = false;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "";
        try
        {
            string query = QueryBox.Text.Trim();
            var hits = await Modrinth.SearchAsync(query, SelectedLoader(), ct);
            if (ct.IsCancellationRequested) return;
            _results.Clear();
            foreach (var hit in hits)
            {
                // 常用 Mod 用对照表里的中文名，直接显示，不走机翻
                string known = ModNames.Zh(hit.Title);
                _results.Add(new ModSearchItem
                {
                    Source = hit,
                    Title = known.Length > 0 ? $"{known} ({hit.Title})" : hit.Title,
                    IconUrl = hit.IconUrl,
                    AuthorText = string.IsNullOrEmpty(hit.Author) ? "" : "作者：" + hit.Author,
                    Description = hit.Description,
                    MetaText = $"{LoaderText(hit.Categories)} · {DownloadsText(hit.Downloads)} 次下载",
                });
            }
            ResultList.ItemsSource = _results;
            EmptyHint.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = query.Length == 0
                ? $"{_results.Count} 个热门 Mod"
                : $"共 {_results.Count} 个结果";
            _ = TranslateResultsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 连续搜索时取消上一次，属正常流程
        }
        catch (Exception ex)
        {
            // 旧请求被替换后报错不应该覆盖新请求的状态
            if (_cts != null && _cts.Token == ct)
            {
                StatusText.Text = "加载失败：" + ex.Message;
            }
        }
        finally
        {
            // 只有当前这次搜索能收尾（连续搜索时，旧请求的 finally 不能把新请求的转圈关掉）
            if (_cts != null && _cts.Token == ct)
            {
                LoadingRing.Visibility = Visibility.Collapsed;
                ResultList.IsEnabled = true;
                SearchButton.IsEnabled = true;
            }
        }
    }

    /// <summary>后台把名字和英文简介翻成中文，完成后原地刷新（失败保持原文）。</summary>
    private async Task TranslateResultsAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = _results.ToList();
            // 对照表里已有的名字不重复请求翻译（传空串，接口会跳过）
            var titles = Translator.ToChineseAsync(
                snapshot.Select(r => ModNames.Zh(r.Source.Title).Length > 0 ? "" : r.Source.Title).ToList(), ct);
            var descriptions = Translator.ToChineseAsync(snapshot.Select(r => r.Description).ToList(), ct);
            await Task.WhenAll(titles, descriptions);
            if (ct.IsCancellationRequested) return;
            for (int i = 0; i < snapshot.Count && i < titles.Result.Count && i < descriptions.Result.Count; i++)
            {
                string original = snapshot[i].Source.Title;
                if (titles.Result[i].Length > 0 && titles.Result[i] != original)
                {
                    snapshot[i].Title = $"{titles.Result[i]} ({original})";
                }
                snapshot[i].Description = descriptions.Result[i];
            }
        }
        catch { /* 翻译失败保持原文 */ }
    }

    private static string LoaderText(List<string> categories)
    {
        var names = categories.Where(c => c is "fabric" or "forge" or "neoforge" or "quilt")
            .Select(ModFlow.LoaderName).ToList();
        return names.Count == 0 ? "Mod" : string.Join(" / ", names);
    }

    private static string DownloadsText(long downloads) => downloads switch
    {
        >= 100_000_000 => $"{downloads / 100_000_000.0:0.#} 亿",
        >= 10_000 => $"{downloads / 10_000.0:0.#} 万",
        _ => downloads.ToString(),
    };

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModSearchItem item) return;
        Frame.Navigate(typeof(ModDetailPage), item.Source);
    }
}
