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
    private UndoJournal? _lastJournal;
    private bool _busy;

    /// <summary>执行/撤销后的结果文案。非空时底栏优先显示它，不被预览统计覆盖。</summary>
    private string? _resultMessage;

    /// <summary>程序性地改输入框时置位，避免触发「用户改规则」从而清掉结果文案。</summary>
    private bool _settingText;

    public MainWindow(ShellPayload payload)
    {
        InitializeComponent();

        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700)); } catch { }

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RefreshPreview();

        LoadItems(payload);

        // 崩溃恢复检查要等 XamlRoot 就绪
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                                   async () => await CheckPendingJournalAsync());
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

    private void ReloadFromPaths(IEnumerable<string> paths)
    {
        _items = RenameEngine.CreateItems(paths);
        ItemList.ItemsSource = _items;
        RefreshPreview();
    }

    // XAML 里三种事件共用同一个处理名，靠重载解析匹配委托
    private void OnRuleChanged(object sender, TextChangedEventArgs e) => OnUserRuleChanged();

    private void OnRuleChanged(object sender, RoutedEventArgs e) => OnUserRuleChanged();

    private void OnRuleChanged(object sender, SelectionChangedEventArgs e) => OnUserRuleChanged();

    private void OnItemEnabledChanged(object sender, RoutedEventArgs e) => OnUserRuleChanged();

    /// <summary>用户主动改规则 → 清掉上一次的执行结果文案，恢复预览统计。</summary>
    private void OnUserRuleChanged()
    {
        if (_settingText) return;
        _resultMessage = null;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_items.Count == 0 || _busy) return;
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
            ExecuteButton.IsEnabled = false;
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

        // 执行/撤销后优先显示结果文案，别被预览统计冲掉
        FooterText.Text = _resultMessage
            ?? $"将改名 {willRename} · 无效 {invalid} · 冲突 {conflicts} · 引擎 {sw.ElapsedMilliseconds}ms";

        ExecuteButton.IsEnabled = !_busy && !outcome.HasBlockingError && willRename > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        ExecuteButton.IsEnabled = !busy &&
            _items.Any(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        UndoButton.IsEnabled = !busy && _lastJournal is { State: JournalState.Applied };
    }

    private async void OnExecute(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var targets = _items
            .Where(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning)
            .ToList();
        if (targets.Count == 0) return;

        var rule = BuildRule();
        var journal = UndoJournal.Create(
            UndoJournal.Summarize(rule),
            targets.Select(i => new JournalEntry
            {
                From = i.FullPath,
                To = i.NewFullPath,
                CaseOnly = i.CaseOnly,
                Ok = true
            }));

        // write-ahead：先落盘 Pending，再动手
        try
        {
            journal.Save();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "无法写入撤销日志：" + ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }

        SetBusy(true);
        try
        {
            var runner = new FileOperationRunner();
            var result = await runner.RunAsync(targets);

            journal.State = JournalState.Applied;
            journal.Save();
            UndoJournal.Prune();
            _lastJournal = journal;

            _resultMessage = result.Failed == 0
                ? $"已改名 {result.Succeeded} 项 · 撤销(Ctrl+Z)"
                : $"已改名 {result.Succeeded} 项，{result.Failed} 项失败 · 撤销(Ctrl+Z)";

            if (result.Failed > 0)
            {
                var first = result.Entries.FirstOrDefault(x => !x.Ok);
                ErrorBar.Message = $"部分失败：{first?.Error ?? "未知原因"}";
                ErrorBar.IsOpen = true;
            }
            else
            {
                ErrorBar.IsOpen = false;
            }

            // 列表切到执行后的真实路径，并清空规则（避免又显示成"要再改一次"）
            _settingText = true;
            FindBox.Text = "";
            ReplaceBox.Text = "";
            _settingText = false;

            ReloadFromPaths(journal.Entries.Where(x => x.Ok).Select(x => x.To));

            // 兜底：TextChanged 是异步派发的，_settingText 在事件到达前已复位，
            // 会被当成"用户改了规则"而清掉结果文案。低优先级再写一次，确保结果条留住。
            var msg = _resultMessage;
            if (msg is not null)
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    _resultMessage = msg;
                    FooterText.Text = msg;
                });
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastJournal is null) return;

        SetBusy(true);
        var journal = _lastJournal;
        try
        {
            var outcome = await journal.UndoAsync();

            if (outcome.Rejected)
            {
                ErrorBar.Message = "无法撤销：" + outcome.RejectReason;
                ErrorBar.IsOpen = true;
                return;
            }

            var parts = new List<string> { $"已撤销 {outcome.Undone} 项" };
            if (outcome.Invalid > 0) parts.Add($"{outcome.Invalid} 项已失效");
            if (outcome.Failed > 0) parts.Add($"{outcome.Failed} 项失败");
            _resultMessage = string.Join("，", parts);

            if (outcome.Invalid > 0 || outcome.Failed > 0)
            {
                ErrorBar.Message = "部分项目未能回滚（可能已被移动或占用）";
                ErrorBar.IsOpen = true;
            }

            ReloadFromPaths(journal.Entries.Where(x => x.Ok).Select(x => x.From));
        }
        finally
        {
            _lastJournal = null;
            UndoButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    private async Task CheckPendingJournalAsync()
    {
        UndoJournal? pending;
        try
        {
            pending = UndoJournal.FindPending();
        }
        catch
        {
            return;
        }

        if (pending is null) return;

        var okCount = pending.Entries.Count(e => e.Ok);
        var dlg = new ContentDialog
        {
            Title = "上次操作可能未完成",
            Content = $"发现一个未完成的改名批次（{okCount} 项，{pending.CreatedUtc.ToLocalTime():MM-dd HH:mm}）。\n" +
                      "回滚可以把它恢复到执行前的名字。",
            PrimaryButtonText = $"回滚 {okCount} 项",
            CloseButtonText = "忽略",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var choice = await dlg.ShowAsync();

        if (choice == ContentDialogResult.Primary)
        {
            var outcome = await pending.UndoAsync();
            FooterText.Text = outcome.Rejected
                ? "回滚失败：" + outcome.RejectReason
                : $"已回滚 {outcome.Undone} 项" + (outcome.Invalid > 0 ? $"，{outcome.Invalid} 项已失效" : "");
        }
        else
        {
            // 用户选择忽略 → 标记为已处理，下次不再提示
            pending.State = JournalState.Undone;
            pending.Save();
        }
    }
}
