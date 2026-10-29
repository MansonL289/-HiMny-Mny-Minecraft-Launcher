using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>服务器列表行：包装 ServerEntry + Ping 结果，属性供 XAML 绑定。</summary>
public sealed class ServerRow
{
    public ServerRow(ServerEntry entry, ServerStatus? status)
    {
        Entry = entry;
        Status = status;
    }

    public ServerEntry Entry { get; }
    public ServerStatus? Status { get; }

    public string Name => Entry.Name;
    public string Ip => Entry.Ip;
    public string StatusText => Status is { Online: true } s
        ? (s.Version.Length > 0 ? $"在线 {s.OnlinePlayers}/{s.MaxPlayers} · {s.Version}" : $"在线 {s.OnlinePlayers}/{s.MaxPlayers}")
        : "离线";
    public string Motd => Status is { Online: true } s
        ? s.Motd.Replace('\r', ' ').Replace('\n', ' ')
        : "";
    public Brush StatusBrush => Status is { Online: true }
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50))
        : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}

/// <summary>版本设置 - 服务器管理：读写 servers.dat，并发 Ping 显示在线状态。</summary>
public sealed partial class VersionServersPage : Page
{
    private VersionSettingsPage? _shell;
    private readonly List<ServerEntry> _entries = new();
    private readonly List<ServerStatus?> _statuses = new();
    private readonly ObservableCollection<ServerRow> _rows = new();
    private bool _refreshing;

    public VersionServersPage()
    {
        InitializeComponent();
    }

    /// <summary>供 x:Bind 的列表源。</summary>
    public ObservableCollection<ServerRow> Rows => _rows;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        _ = RefreshAsync();
    }

    private string GameDir => _shell!.Meta.GameDir(_shell!.Dir);

    private async Task SaveAsync()
    {
        var list = new List<ServerEntry>(_entries);
        await Task.Run(() => Servers.Save(GameDir, list));
    }

    private void RebuildRows()
    {
        _rows.Clear();
        for (int i = 0; i < _entries.Count; i++)
        {
            _rows.Add(new ServerRow(_entries[i], i < _statuses.Count ? _statuses[i] : null));
        }
        bool empty = _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ServerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAsync()
    {
        if (_shell == null || _refreshing) return;
        _refreshing = true;
        string gameDir = GameDir;
        LoadingRing.Visibility = Visibility.Visible;
        ServerList.IsEnabled = false;
        StatusText.Text = "正在读取服务器列表…";

        try
        {
            List<ServerEntry> list;
            try
            {
                list = await Task.Run(() => Servers.Load(gameDir));
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取 servers.dat 失败: {ex.Message}";
                return;
            }
            if (_shell == null) return;

            _entries.Clear();
            _entries.AddRange(list);
            _statuses.Clear();
            for (int i = 0; i < _entries.Count; i++) _statuses.Add(null);
            RebuildRows();

            if (list.Count > 0)
            {
                StatusText.Text = "正在检测服务器状态…";
                var statuses = new ServerStatus?[list.Count];
                // 最多 6 个并发，避免一次连太多服务器
                using var sem = new SemaphoreSlim(6);
                var tasks = list.Select(async (entry, index) =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        statuses[index] = await Servers.PingAsync(entry.Ip);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);
                if (_shell == null || gameDir != GameDir) return; // 页面已切走，丢弃结果

                _statuses.Clear();
                _statuses.AddRange(statuses);
            }

            RebuildRows();
            StatusText.Text = _entries.Count == 0
                ? $"尚未添加服务器　{gameDir}"
                : $"共 {_entries.Count} 个服务器　{gameDir}";
        }
        finally
        {
            _refreshing = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ServerList.IsEnabled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var input = await ShowServerDialogAsync("添加服务器", "添加", "", "");
        if (input == null || _shell == null) return;

        _entries.Add(new ServerEntry { Name = input.Value.Name, Ip = input.Value.Ip });
        _statuses.Add(null);
        RebuildRows();
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存 servers.dat 失败: {ex.Message}";
        }

        // 添加后立即 Ping 一次
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在检测服务器状态…";
        var status = await Servers.PingAsync(input.Value.Ip);
        if (_shell == null) return;
        if (_statuses.Count > 0) _statuses[^1] = status;
        LoadingRing.Visibility = Visibility.Collapsed;
        RebuildRows();
        StatusText.Text = status is { Online: true }
            ? $"{input.Value.Name} 在线"
            : $"{input.Value.Name} 离线（地址错误或服务器未开服）";
    }

    /// <summary>双击行编辑服务器名称/地址。</summary>
    private async void Row_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row } || _shell == null) return;
        var input = await ShowServerDialogAsync("编辑服务器", "保存", row.Name, row.Ip);
        if (input == null) return;
        int index = _entries.IndexOf(row.Entry);
        if (index < 0) return;

        row.Entry.Name = input.Value.Name;
        row.Entry.Ip = input.Value.Ip;
        RebuildRows();
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存 servers.dat 失败: {ex.Message}";
        }

        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "正在检测服务器状态…";
        var status = await Servers.PingAsync(row.Entry.Ip);
        if (_shell == null) return;
        if (index < _statuses.Count) _statuses[index] = status;
        LoadingRing.Visibility = Visibility.Collapsed;
        RebuildRows();
        StatusText.Text = status is { Online: true }
            ? $"{row.Entry.Name} 在线"
            : $"{row.Entry.Name} 离线（地址错误或服务器未开服）";
    }

    /// <summary>服务器名称/地址输入对话框；取消返回 null。</summary>
    private async Task<(string Name, string Ip)?> ShowServerDialogAsync(
        string title, string primaryText, string name, string ip)
    {
        var nameBox = new TextBox { Header = "名称", Text = name, PlaceholderText = "服务器显示名" };
        var ipBox = new TextBox { Header = "地址", Text = ip, PlaceholderText = "例如 example.com 或 example.com:25565" };
        var error = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(ipBox);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        string resultName = "", resultIp = "";
        dialog.PrimaryButtonClick += (_, args) =>
        {
            resultName = nameBox.Text.Trim();
            resultIp = ipBox.Text.Trim();
            string? message = null;
            if (resultName.Length == 0) message = "请填写服务器名称。";
            else if (resultIp.Length == 0) message = "请填写服务器地址。";
            else if (resultIp.Any(char.IsWhiteSpace)) message = "地址不能包含空格。";
            if (message != null)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (resultName, resultIp);
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRow row }) return;
        if (_shell == null) return;
        int index = _entries.IndexOf(row.Entry);
        if (index < 0) return;

        _entries.RemoveAt(index);
        if (index < _statuses.Count) _statuses.RemoveAt(index);
        RebuildRows();
        try
        {
            await SaveAsync();
            StatusText.Text = $"已删除「{row.Name}」，共 {_entries.Count} 个服务器";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存 servers.dat 失败: {ex.Message}";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = GameDir;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        StatusText.Text = $"servers.dat 所在文件夹：{dir}";
    }
}
