using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>版本列表子页面：分类筛选 + 刷新；点击版本进入详情页选择附加模块并下载。</summary>
public sealed partial class DownloadVersionsPage : Page
{
    // 完整版本清单（含分类），刷新后常驻，切分类时不重新拉取
    private readonly List<(string Id, int Category)> _allVersions = new();
    private bool _refreshing;

    // Mojang 愚人节版本的 id（它们的 type 是普通 snapshot，需按 id 识别）
    private static readonly HashSet<string> AprilFools = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneBlockAtATime", "23w13a_or_b", "24w14potato", "25w14craftmine",
    };

    public DownloadVersionsPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_allVersions.Count == 0 && !_refreshing)
        {
            Refresh_Click(RefreshButton, new RoutedEventArgs());
        }
    }

    // 分类：0 原版游戏 / 1 快照版 / 2 远古版 / 3 愚人节版
    private static int CategoryOf(string id, string type)
    {
        if (AprilFools.Contains(id))
        {
            return 3;
        }
        return type switch
        {
            "release" => 0,
            "old_alpha" or "old_beta" => 2,
            _ => 1,
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        VersionList.IsEnabled = false;
        LoadingRing.Visibility = Visibility.Visible;
        DlStatus.Text = "";
        try
        {
            JsonObject manifest;
            try { manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: true); }
            catch { manifest = await Manifest.FetchAsync(AppSettings.DownloadMirror, v2: false); }

            _allVersions.Clear();
            foreach (var (id, _, _, type) in Manifest.Versions(manifest))
            {
                _allVersions.Add((id, CategoryOf(id, type)));
            }

            ApplyCategoryFilter();
            DlStatus.Text = $"最新正式版: {Manifest.LatestRelease(manifest)}，清单共 {_allVersions.Count} 个版本";
        }
        catch (Exception ex)
        {
            DlStatus.Text = $"获取版本清单失败: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            VersionList.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCategoryFilter();

    private void ApplyCategoryFilter()
    {
        if (VersionList is null || CategoryBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        int category = int.Parse(item.Tag?.ToString() ?? "0");
        VersionList.ItemsSource = _allVersions
            .Where(v => v.Category == category)
            .Select(v => v.Id)
            .ToList();
    }

    // 点击版本：跳转到详情子页面（选择附加模块并开始下载）
    private void VersionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string versionId)
        {
            Frame.Navigate(typeof(VersionDetailPage), versionId);
        }
    }
}
