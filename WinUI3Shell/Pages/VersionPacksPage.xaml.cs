using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本设置 - 资源包 / 光影：列出、启用/停用、导入、删除。</summary>
public sealed partial class VersionPacksPage : Page
{
    private VersionSettingsPage? _shell;
    private bool _loading;
    private readonly ObservableCollection<PackItem> _resources = new();
    private readonly ObservableCollection<PackItem> _shaders = new();

    public VersionPacksPage()
    {
        InitializeComponent();
        ResourceList.ItemsSource = _resources;
        ShaderList.ItemsSource = _shaders;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (VersionSettingsPage)e.Parameter;
        _ = RefreshAsync();
    }

    private VersionSettingsPage Shell => _shell!;
    private string GameDir => Shell.Meta.GameDir(Shell.Dir);

    private async Task RefreshAsync()
    {
        string dir = GameDir;
        var (resources, shaders) = await Task.Run(() => (Packs.List(dir, false), Packs.List(dir, true)));
        if (_shell == null || dir != GameDir) return;

        _loading = true;
        _resources.Clear();
        foreach (var item in resources) _resources.Add(item);
        _shaders.Clear();
        foreach (var item in shaders) _shaders.Add(item);
        _loading = false;
    }

    private async void PackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { Tag: PackItem item } toggle) return;
        try
        {
            await Task.Run(() => Packs.SetEnabled(GameDir, item, toggle.IsOn));
        }
        catch { /* 文件被占用等情况，刷新后恢复原状态 */ }
        await RefreshAsync();
    }

    private async void ImportResource_Click(object sender, RoutedEventArgs e) => await ImportAsync(false);

    private async void ImportShader_Click(object sender, RoutedEventArgs e) => await ImportAsync(true);

    private async Task ImportAsync(bool shader)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;

        foreach (var file in files)
        {
            try
            {
                await Task.Run(() => Packs.Import(GameDir, shader, file.Path));
            }
            catch { /* 单个失败不影响其它 */ }
        }
        await RefreshAsync();
    }

    private async void DeletePack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PackItem item }) return;
        var confirm = new ContentDialog
        {
            Title = "删除",
            Content = item.IsShader
                ? $"确定删除光影「{item.Name}」吗？"
                : $"确定删除资源包「{item.Name}」吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await Task.Run(() => Packs.Delete(item));
        }
        catch { /* 占用时删不掉，刷新后仍在列表里 */ }
        await RefreshAsync();
    }

    private void OpenResourceDir_Click(object sender, RoutedEventArgs e) => OpenDir(Packs.ResourcePacksDir(GameDir));

    private void OpenShaderDir_Click(object sender, RoutedEventArgs e) => OpenDir(Packs.ShaderPacksDir(GameDir));

    private static void OpenDir(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
