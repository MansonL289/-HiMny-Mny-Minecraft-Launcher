using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>修改存档 - 基础页：游戏模式与允许作弊，游戏规则跳转到子页面调整。
/// 改动只写入内存草稿，由外层对话框统一保存。</summary>
public sealed partial class SaveBasicEditPage : Page
{
    private SaveDraft? _draft;
    private bool _loading = true;

    public SaveBasicEditPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        _draft = e.Parameter as SaveDraft;
        if (_draft == null) return;

        SaveNameText.Text = string.IsNullOrWhiteSpace(_draft.LevelName)
            ? _draft.FolderName
            : $"{_draft.LevelName}（{_draft.FolderName}）";
        ModeCombo.SelectedIndex = Math.Clamp(_draft.GameType, 0, 3);
        CheatsToggle.IsOn = _draft.AllowCommands;
        RulesText.Text = _draft.Rules.Count == 0
            ? "此存档没有游戏规则数据。"
            : $"共 {_draft.Rules.Count} 条规则，进入子页面逐条调整。";
        HintText.Text = _draft.Hardcore
            ? "极限模式存档：修改模式可能影响成就。修改会在游戏下次打开该存档时生效。"
            : "修改会在游戏下次打开该存档时生效。";
        _loading = false;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _draft == null || ModeCombo.SelectedIndex < 0) return;
        _draft.GameType = ModeCombo.SelectedIndex;
    }

    private void Cheats_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _draft == null) return;
        _draft.AllowCommands = CheatsToggle.IsOn;
    }

    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        if (_draft != null)
        {
            Frame.Navigate(typeof(SaveGamerulesPage), _draft);
        }
    }
}
