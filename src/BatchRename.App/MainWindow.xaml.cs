using System.Diagnostics;
using BatchRename.App.Core;
using BatchRename.App.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BatchRename.App;

public sealed partial class MainWindow : Window
{
    private List<RenameItem> _items = new();
    private readonly DispatcherQueueTimer _debounce;

    public MainWindow(ShellPayload payload)
    {
        InitializeComponent();

        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700)); } catch { }

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RefreshPreview();

        LoadItems(payload);
    }

    private void LoadItems(ShellPayload payload)
    {
        if (!payload.FromShell)
        {
            ItemList.ItemsSource = Array.Empty<RenameItem>();
            SummaryText.Text = "无参启动";
            FooterText.Text = "通过资源管理器右键「批量重命名」使用（拖放模式在 M5 实现）";
            return;
        }

        var sw = Stopwatch.StartNew();
        _items = RenameEngine.CreateItems(payload.Paths);
        ItemList.ItemsSource = _items;
        sw.Stop();

        RefreshPreview();
        FooterText.Text = $"{_items.Count} 项 · 载入 {sw.ElapsedMilliseconds}ms";
    }

    // XAML 里三种事件共用同一个处理名，靠重载解析匹配委托
    private void OnRuleChanged(object sender, TextChangedEventArgs e) => QueueRefresh();

    private void OnRuleChanged(object sender, RoutedEventArgs e) => QueueRefresh();

    private void OnRuleChanged(object sender, SelectionChangedEventArgs e) => QueueRefresh();

    private void OnItemEnabledChanged(object sender, RoutedEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_items.Count == 0) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private RenameRule BuildRule() => new()
    {
        Find = FindBox.Text ?? "",
        Replace = ReplaceBox.Text ?? "",
        UseRegex = RegexBox.IsChecked == true,
        CaseSensitive = CaseSensitiveBox.IsChecked == true,
        Scope = ScopeBox.SelectedIndex switch
        {
            1 => RenameScope.StemOnly,
            2 => RenameScope.ExtOnly,
            _ => RenameScope.Both
        },
        CaseMode = CaseBox.SelectedIndex switch
        {
            1 => CaseMode.Upper,
            2 => CaseMode.Lower,
            3 => CaseMode.Title,
            _ => CaseMode.None
        }
    };

    private void RefreshPreview()
    {
        if (_items.Count == 0)
        {
            ErrorBar.IsOpen = false;
            return;
        }

        var sw = Stopwatch.StartNew();
        var outcome = RenameEngine.Run(_items, BuildRule());
        sw.Stop();

        if (outcome.RegexError is not null)
        {
            ErrorBar.Message = "正则无效：" + outcome.RegexError;
            ErrorBar.IsOpen = true;
        }
        else
        {
            ErrorBar.IsOpen = false;
        }

        var willRename = _items.Count(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        var conflicts = _items.Count(i => i.State is ItemState.ConflictInBatch or ItemState.ConflictOnDisk);
        var invalid = _items.Count(i => i.State == ItemState.Invalid);

        SummaryText.Text = $"{_items.Count} 项";
        FooterText.Text = $"将改名 {willRename} · 无效 {invalid} · 冲突 {conflicts} · 引擎 {sw.ElapsedMilliseconds}ms";
    }
}
