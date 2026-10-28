using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本设置外壳：标题显示 版本名-版本号，侧栏（常规/设置/Mod 管理）+ 子页面。
/// 子页面通过导航参数拿到本页实例，共享 Dir 与 Meta。</summary>
public sealed partial class VersionSettingsPage : Page
{
    /// <summary>版本目录名（= 版本名，重命名后会变）。</summary>
    public string Dir { get; private set; }
    public VersionMeta Meta { get; }

    public VersionSettingsPage(string dir, string? section = null)
    {
        InitializeComponent();
        Dir = dir;
        Meta = VersionMeta.Load(dir) ?? new VersionMeta { Id = dir };
        TitleText.Text = $"{dir} - {Meta.Id}";
        Navigate(section ?? "general");
        // 让侧栏高亮同步到实际显示的子页
        foreach (var obj in SideBar.MenuItems)
        {
            if (obj is NavigationViewItem item && item.Tag as string == (section ?? "general"))
            {
                SideBar.SelectedItem = item;
                break;
            }
        }
    }

    public void SaveMeta() => Meta.Save(Dir);

    /// <summary>重命名后更新标题并重载当前子页（子页持有的目录路径已失效）。</summary>
    public void ApplyRename(string newDir)
    {
        Dir = newDir;
        TitleText.Text = $"{newDir} - {Meta.Id}";
        if (SideBar.SelectedItem is NavigationViewItem item)
        {
            Navigate(item.Tag as string);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.MainWindow?.ShowHome();

    private void SideBar_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            Navigate(item.Tag as string);
        }
    }

    private void Navigate(string? tag)
    {
        Type? page = tag switch
        {
            "launch" => typeof(VersionLaunchSettingsPage),
            "saves" => typeof(VersionSavesPage),
            "screenshots" => typeof(VersionScreenshotsPage),
            "packs" => typeof(VersionPacksPage),
            "mods" => typeof(VersionModsPage),
            _ => typeof(VersionGeneralPage),
        };
        if (SubFrame.Content?.GetType() != page)
        {
            SubFrame.Navigate(page, this);
        }
    }
}
