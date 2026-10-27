using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>修改存档对话框：内容区是一个 Frame，基础页 + 游戏规则子页面共享同一份 SaveDraft。
/// 点「保存」才写回 level.dat，子页面切换不落盘。</summary>
public sealed partial class SaveEditDialog : ContentDialog
{
    private readonly SaveDraft _draft;

    public SaveEditDialog(SaveDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        string name = string.IsNullOrWhiteSpace(draft.LevelName) ? draft.FolderName : draft.LevelName;
        Title = $"修改存档 - {name}";
        EditFrame.Navigate(typeof(SaveBasicEditPage), draft);
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            _draft.Save();
        }
        catch (Exception ex)
        {
            // 写盘失败时留在对话框里，用户可重试或取消
            args.Cancel = true;
            ErrorText.Text = $"保存失败：{ex.Message}（游戏正在运行时请先退出）";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
