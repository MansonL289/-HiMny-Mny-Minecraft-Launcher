using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>储存 - 单个版本的存档批量删除（从储存页点击版本进入）。</summary>
public sealed partial class StorageSavesPage : Page
{
    /// <summary>存档列表行。</summary>
    public sealed class SaveRow
    {
        public string SavePath { get; set; } = "";
        public string LevelName { get; set; } = "";
        public string DetailText { get; set; } = "";
        public string SizeText { get; set; } = "";
        public bool IsChecked { get; set; }
    }

    private string _dir = "";
    private List<SaveRow> _rows = new();

    public StorageSavesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _dir = e.Parameter as string ?? "";
        TitleText.Text = $"存档 - {_dir}";
        RefreshSaves();
    }

    private void RefreshSaves()
    {
        StatusText.Text = "";
        string gameDir = VersionMeta.Load(_dir)?.GameDir(_dir) ?? McPaths.GameDir;
        _rows = SaveManager.Enumerate(gameDir).Select(s => new SaveRow
        {
            SavePath = s.SavePath,
            LevelName = string.IsNullOrWhiteSpace(s.LevelName) ? s.FolderName : s.LevelName,
            DetailText = $"{s.FolderName} · {FormatLastPlayed(s.LastPlayed)}",
            SizeText = SizeText(s.SizeBytes),
        }).ToList();
        SaveList.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = false;
    }

    private void SaveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is string path)
        {
            var row = _rows.FirstOrDefault(r => r.SavePath == path);
            if (row != null)
            {
                row.IsChecked = box.IsChecked == true;
            }
        }
        DeleteButton.IsEnabled = _rows.Any(r => r.IsChecked);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        bool target = !_rows.All(r => r.IsChecked);
        foreach (var row in _rows)
        {
            row.IsChecked = target;
        }
        SaveList.ItemsSource = null;
        SaveList.ItemsSource = _rows;
        DeleteButton.IsEnabled = target;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "删除存档",
            Content = $"确定删除 {selected.Count} 个存档吗？存档将不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var failed = new List<string>();
        foreach (var row in selected)
        {
            try
            {
                SaveManager.Delete(row.SavePath);
            }
            catch
            {
                failed.Add(row.LevelName);
            }
        }
        int count = selected.Count;
        RefreshSaves();
        StatusText.Text = failed.Count == 0
            ? $"已删除 {count} 个存档"
            : "删除失败：" + string.Join("、", failed);
    }

    private static string FormatLastPlayed(long ticks)
    {
        if (ticks <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ticks).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "";
        }
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824.0:0.#} GB",
        >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
