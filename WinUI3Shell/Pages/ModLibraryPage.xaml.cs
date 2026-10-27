using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>库列表项：一个 Mod（可能有多份不同加载器/版本的文件）。</summary>
public sealed class ModLibraryItem
{
    public ModLibraryEntry Entry { get; set; } = new();
    public string Name => Entry.Name;
    public string IconUrl => Entry.IconUrl;
    public string DetailText { get; set; } = "";
}

/// <summary>MOD 库：所有下载过的 Mod，可随时安装到带加载器的版本。</summary>
public sealed partial class ModLibraryPage : Page
{
    private readonly ObservableCollection<ModLibraryItem> _items = new();
    private bool _busy;
    private int _refreshSeq;

    public ModLibraryPage()
    {
        InitializeComponent();
        // 主窗口用手动页面缓存（直接赋值 Content，不触发 OnNavigatedTo），
        // 每次切回本页都会触发 Loaded，借此自动刷新
        Loaded += (_, _) => RefreshList();
    }

    private async void RefreshList()
    {
        int seq = ++_refreshSeq;
        var entries = ModLibrary.Load().OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var originals = entries.Select(e => e.Name).ToList();
        var known = originals.Select(ModNames.Zh).ToList();
        // 常用 Mod 的中文名立即生效（不联网）
        for (int i = 0; i < entries.Count; i++)
        {
            if (known[i].Length > 0)
            {
                entries[i].Name = $"{known[i]} ({originals[i]})";
            }
        }
        Rebind(entries, null);
        ModList.ItemsSource = _items;

        // 后台并行：扫描各 Mod 的 jar 是否带中文语言文件 + 把英文名翻成中文，完成后刷新
        var zhTask = Task.Run(() => entries.ToDictionary(
            e => e.Id,
            e => e.Files.Any(f => ModLang.SupportsChinese(Path.Combine(ModLibrary.DirFor(e.Id), f.FileName))),
            StringComparer.OrdinalIgnoreCase));
        var namesTask = Translator.ToChineseAsync(
            Enumerable.Range(0, originals.Count).Select(i => known[i].Length > 0 ? "" : originals[i]).ToList(),
            CancellationToken.None);
        await Task.WhenAll(zhTask, namesTask);
        // 连续切页触发的刷新只认最后一次，避免旧结果覆盖新列表
        if (seq != _refreshSeq) return;
        var zh = zhTask.Result;
        for (int i = 0; i < entries.Count && i < namesTask.Result.Count; i++)
        {
            if (known[i].Length > 0) continue;
            string original = originals[i];
            if (namesTask.Result[i].Length > 0 && namesTask.Result[i] != original)
            {
                entries[i].Name = $"{namesTask.Result[i]} ({original})";
            }
        }
        Rebind(entries, zh);
    }

    private void Rebind(List<ModLibraryEntry> entries, Dictionary<string, bool>? zh)
    {
        _items.Clear();
        foreach (var entry in entries)
        {
            _items.Add(new ModLibraryItem
            {
                Entry = entry,
                DetailText = Detail(entry, zh != null && zh.GetValueOrDefault(entry.Id)),
            });
        }
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = empty ? "" : $"共 {_items.Count} 个 Mod";
    }

    private static string Detail(ModLibraryEntry entry, bool supportsChinese = false)
    {
        var flavors = entry.Files
            .Select(f => $"{ModFlow.LoaderName(f.Loader)} {f.McVersion}")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        string text = string.Join(" · ", flavors);
        if (supportsChinese)
        {
            text += " · 支持中文";
        }
        return string.IsNullOrEmpty(entry.Author) ? text : $"{entry.Author} · {text}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    /// <summary>安装到版本（需要补下文件时也作为全局下载任务，可在「下载任务」里看进度）。</summary>
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibraryItem item } || _busy) return;
        if (DownloadManager.IsModBusy(item.Entry.Name))
        {
            StatusText.Text = "这个 Mod 正在处理中";
            return;
        }

        var targets = await ModFlow.PickTargetsAsync(XamlRoot);
        if (targets == null || targets.Count == 0) return;

        _busy = true;
        InstallBar.Value = 0;
        InstallBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "";

        var entry = item.Entry;
        _outcomes = null;
        _job = DownloadManager.StartMod(entry.Name, async (job, ct) =>
        {
            var reporter = new Progress<ModInstallProgress>(p =>
                DownloadManager.Report(job, p.Status, p.Fraction));
            var outcomes = await ModFlow.InstallAsync(entry, targets, reporter, ct);
            _outcomes = outcomes;
            int ok = outcomes.Count(o => o.Ok);
            DownloadManager.Report(job, $"已安装到 {ok} 个版本", 1);
        });
        DownloadManager.JobsChanged += OnJobChanged;
        // 任务可能在订阅前就结束（极小概率），主动同步一次
        OnJobChanged(null, EventArgs.Empty);
    }

    private DownloadJob? _job;
    private List<ModInstallOutcome>? _outcomes;

    private void OnJobChanged(object? sender, EventArgs e)
    {
        var job = _job;
        if (job == null) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            StatusText.Text = job.StatusText;
            InstallBar.Value = Math.Clamp(job.Fraction, 0, 1);
            if (!job.Done) return;
            DownloadManager.JobsChanged -= OnJobChanged;
            InstallBar.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            _busy = false;
            _job = null;
            var failed = _outcomes?.Where(o => !o.Ok).ToList() ?? new();
            _outcomes = null;
            if (failed.Count > 0 && XamlRoot != null)
            {
                await ModFlow.ShowMessageAsync(XamlRoot, job.VersionId,
                    string.Join("\n", failed.Select(f => $"{f.Dir}：{f.Text}")));
            }
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibraryItem item }) return;
        ModLibrary.Remove(item.Entry.Id);
        _items.Remove(item);
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = empty ? "" : $"共 {_items.Count} 个 Mod";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _job?.Cts.Cancel();
        StatusText.Text = "正在取消...";
    }
}
