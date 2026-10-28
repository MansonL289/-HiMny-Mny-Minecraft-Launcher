using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>账号列表项：账号本体 + 列表展示用的文案。</summary>
public sealed class AccountItem
{
    public McAccount Account { get; init; } = new();
    public string Name => Account.Name;
    public string DetailText { get; init; } = "";
    /// <summary>头像：离线账号用本地默认头像（按名字拉 mc-heads 会拉到别人的重名皮肤），在线账号拉皮肤头。</summary>
    public string AvatarUrl => Account.Type == "offline"
        ? "ms-appx:///Assets/Head.png"
        : $"https://mc-heads.net/avatar/{Uri.EscapeDataString(Account.Name)}/40";
    public bool Active { get; init; }
    public Visibility ActiveVisibility => Active ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectVisibility => Active ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>账号页：管理离线 / 微软 / 第三方账号，点「选择」按钮切换当前账号。</summary>
public sealed partial class AccountPage : Page
{
    private readonly ObservableCollection<AccountItem> _items = new();
    private bool _busy;

    public AccountPage()
    {
        InitializeComponent();
        AccountList.ItemsSource = _items;
        // 主窗口手动缓存页面实例，每次切回本页都会触发 Loaded，借此刷新
        Loaded += (_, _) => RefreshList();
        RefreshList();
    }

    private void RefreshList()
    {
        string activeKey = AccountStore.ActiveKey;
        _items.Clear();
        foreach (var account in AccountStore.Accounts)
        {
            _items.Add(new AccountItem
            {
                Account = account,
                DetailText = account.Uuid.Length >= 8
                    ? $"{account.TypeText} · {account.Uuid[..8]}"
                    : account.TypeText,
                Active = string.Equals(AccountStore.KeyOf(account), activeKey, StringComparison.Ordinal),
            });
        }
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>打开「添加账号」下拉（首页下拉框选「添加账号」时跳转过来直接弹出）。</summary>
    public void OpenAdd()
    {
        if (_busy) return;
        AddButton.Flyout?.ShowAt(AddButton);
    }

    // 「选择」按钮：设为当前账号
    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountItem item }) return;
        AccountStore.SetActive(item.Account);
        RefreshList();
        StatusText.Text = $"当前账号：{item.Name}";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountItem item }) return;
        AccountStore.Remove(item.Account);
        RefreshList();
        StatusText.Text = $"已删除账号：{item.Name}";
    }

    // ===== 微软登录 =====

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        AddButton.IsEnabled = false;
        try
        {
            await ShowMicrosoftDialogAsync();
        }
        finally
        {
            _busy = false;
            AddButton.IsEnabled = true;
        }
    }

    private async Task ShowMicrosoftDialogAsync()
    {
        var codeText = new TextBlock
        {
            Text = "正在获取代码…",
            FontSize = 32,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var hint = new TextBlock
        {
            Text = "在浏览器打开 https://www.microsoft.com/link 输入下面的代码",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };
        var ring = new ProgressRing
        {
            Width = 24,
            Height = 24,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copyButton = new Button { Content = "复制代码", IsEnabled = false };
        var openButton = new Button { Content = "打开浏览器" };
        string userCode = "";
        string verifyUrl = "https://www.microsoft.com/link";
        copyButton.Click += (_, _) =>
        {
            if (userCode.Length > 0) CopyText(userCode);
        };
        openButton.Click += (_, _) => OpenUrl(verifyUrl);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(openButton);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(ring);
        statusRow.Children.Add(status);

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(codeText);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        panel.Children.Add(statusRow);

        var dialog = new ContentDialog
        {
            Title = "微软登录",
            Content = panel,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var cts = new CancellationTokenSource();
        bool succeeded = false;
        var showTask = dialog.ShowAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var device = await MsaLogin.StartAsync(cts.Token);
                userCode = device.UserCode;
                verifyUrl = device.VerificationUri;
                DispatcherQueue.TryEnqueue(() =>
                {
                    codeText.Text = device.UserCode;
                    hint.Text = $"在浏览器打开 {device.VerificationUri} 输入下面的代码";
                    copyButton.IsEnabled = true;
                    ring.Visibility = Visibility.Visible;
                    ring.IsActive = true;
                    status.Text = "等待在浏览器中完成登录…";
                });

                var account = await MsaLogin.PollAsync(device,
                    msg => DispatcherQueue.TryEnqueue(() => status.Text = msg), cts.Token);
                AccountStore.Add(account);
                succeeded = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    ring.IsActive = false;
                    status.Text = $"登录成功：{account.Name}";
                    dialog.Hide();
                });
            }
            catch (OperationCanceledException)
            {
                // 用户点了取消
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ring.IsActive = false;
                    ring.Visibility = Visibility.Collapsed;
                    status.Text = ex.Message;
                });
            }
        });

        await showTask;
        cts.Cancel();
        cts.Dispose();

        if (succeeded)
        {
            RefreshList();
            StatusText.Text = "已添加微软账号";
        }
    }

    // ===== 离线账号 =====

    private async void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var box = new TextBox
        {
            PlaceholderText = "账号名（字母、数字、下划线，最长 16）",
            MaxLength = 16,
        };
        bool filtering = false;
        box.TextChanged += (_, _) =>
        {
            if (filtering) return;
            string filtered = new(box.Text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
            if (filtered == box.Text) return;
            filtering = true;
            int caret = box.SelectionStart;
            box.Text = filtered;
            box.SelectionStart = Math.Min(caret, filtered.Length);
            filtering = false;
        };

        var dialog = new ContentDialog
        {
            Title = "离线账号",
            Content = box,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string name = box.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "账号名不能为空";
            return;
        }
        var account = new McAccount
        {
            Type = "offline",
            Name = name,
            Uuid = LaunchService.OfflineUuid(name),
        };
        AccountStore.Add(account);
        RefreshList();
        StatusText.Text = $"已添加离线账号：{name}";
    }

    // ===== 第三方登录 =====

    private async void YggdrasilLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var serverBox = new TextBox
        {
            PlaceholderText = "验证服务器地址，如 https://littleskin.cn/api/yggdrasil",
        };
        var emailBox = new TextBox { PlaceholderText = "邮箱" };
        var passwordBox = new PasswordBox { PlaceholderText = "密码" };
        var hint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var ring = new ProgressRing
        {
            Width = 24,
            Height = 24,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        var ringRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ringRow.Children.Add(ring);

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(serverBox);
        panel.Children.Add(emailBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(hint);
        panel.Children.Add(ringRow);

        var dialog = new ContentDialog
        {
            Title = "第三方登录",
            Content = panel,
            PrimaryButtonText = "登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var cts = new CancellationTokenSource();
        bool succeeded = false;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // 登录成功前不关闭对话框
            args.Cancel = true;
            if (_busy) return;
            string url = serverBox.Text.Trim();
            string email = emailBox.Text.Trim();
            string password = passwordBox.Password;
            if (url.Length == 0 || email.Length == 0 || password.Length == 0)
            {
                hint.Text = "请填写服务器地址、邮箱和密码";
                hint.Visibility = Visibility.Visible;
                return;
            }

            _busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            ring.Visibility = Visibility.Visible;
            ring.IsActive = true;
            hint.Visibility = Visibility.Collapsed;
            try
            {
                var account = await YggdrasilLogin.LoginAsync(url, email, password, cts.Token);
                AccountStore.Add(account);
                succeeded = true;
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
                // 用户关闭了对话框
            }
            catch (Exception ex)
            {
                hint.Text = ex.Message;
                hint.Visibility = Visibility.Visible;
            }
            finally
            {
                _busy = false;
                dialog.IsPrimaryButtonEnabled = true;
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
            }
        };

        await dialog.ShowAsync();
        cts.Cancel();
        cts.Dispose();

        if (succeeded)
        {
            RefreshList();
            StatusText.Text = "已添加第三方账号";
        }
    }

    // ===== 小工具 =====

    private static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
