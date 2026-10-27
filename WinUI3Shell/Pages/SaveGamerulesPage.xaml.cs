using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUI3Shell.Services.Mc;

namespace WinUI3Shell.Pages;

/// <summary>修改存档 - 游戏规则子页面：布尔规则用开关，整数规则用数字框，其他值只读。
/// 改动只写入内存草稿，由外层对话框统一保存。</summary>
public sealed partial class SaveGamerulesPage : Page
{
    public SaveGamerulesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        RulesPanel.Children.Clear();
        if (e.Parameter is not SaveDraft draft) return;

        TitleText.Text = $"游戏规则（{draft.Rules.Count} 条）";
        if (draft.Rules.Count == 0)
        {
            RulesPanel.Children.Add(new TextBlock
            {
                Text = "此存档没有游戏规则数据。",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        foreach (var rule in draft.Rules)
        {
            RulesPanel.Children.Add(BuildRow(rule));
        }
    }

    private static Grid BuildRow(GameRuleEdit rule)
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 6, 0, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        grid.Children.Add(new TextBlock
        {
            Text = rule.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        switch (rule.Kind)
        {
            case GameRuleKind.Bool:
                var toggle = new ToggleSwitch
                {
                    IsOn = rule.BoolValue,
                    OnContent = "",
                    OffContent = "",
                    MinWidth = 0,
                    Tag = rule,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                toggle.Toggled += RuleBool_Toggled;
                Grid.SetColumn(toggle, 1);
                grid.Children.Add(toggle);
                break;

            case GameRuleKind.Int:
                var number = new NumberBox
                {
                    Value = rule.NumberValue,
                    Width = 200,
                    SmallChange = 1,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Tag = rule,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                number.ValueChanged += RuleNumber_ValueChanged;
                Grid.SetColumn(number, 1);
                grid.Children.Add(number);
                break;

            default:
                var text = new TextBlock
                {
                    Text = rule.TextValue,
                    FontSize = 12,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                break;
        }
        return grid;
    }

    private static void RuleBool_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: GameRuleEdit rule } toggle)
        {
            rule.BoolValue = toggle.IsOn;
        }
    }

    private static void RuleNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender.Tag is GameRuleEdit rule && !double.IsNaN(args.NewValue))
        {
            rule.NumberValue = args.NewValue;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}
