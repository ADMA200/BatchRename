using System.Diagnostics;
using BatchRename.App.Core;
using BatchRename.App.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
// 只取 VirtualKey，别用 using Windows.System：那会引入另一个 DispatcherQueueTimer 造成二义性
using VirtualKey = Windows.System.VirtualKey;

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

        // 规格书 §7.4：自定义标题栏与 Mica 融合，不留白条
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 系统 caption 高度设 Standard(32px)，与 AppTitleBar 的 32px 一致，
        // 三个窗口按钮才会和标题文字对齐；按钮背景置透明，避免出现一条灰底
        try
        {
            AppWindow.TitleBar.PreferredHeightOption =
                Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch { }

        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 720)); } catch { }

        // handledEventsToo = true：Space 会被 ListView 自己吃掉，必须在已处理阶段也能收到
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RefreshPreview();

        LoadItems(payload);

        // 崩溃恢复检查要等 XamlRoot 就绪
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                                   async () => await CheckPendingJournalAsync());
    }

    /// <summary>规格书 §7.5 键盘。</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (!_busy && ExecuteButton.IsEnabled)
                {
                    OnExecute(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;

            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;

            case VirtualKey.A when IsCtrlDown():
                foreach (var item in _items) item.IsEnabled = true;
                _resultMessage = null;
                QueueRefresh();
                e.Handled = true;
                break;

            case VirtualKey.Space:
                if (ItemList.SelectedItem is RenameItem sel)
                {
                    sel.IsEnabled = !sel.IsEnabled;
                    _resultMessage = null;
                    QueueRefresh();
                    e.Handled = true;
                }
                break;
        }
    }

    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>第二个实例转发过来的路径：替换列表，不新建窗口。</summary>
    public void UpdateFromPaths(IReadOnlyList<string> paths)
    {
        _resultMessage = null;

        _settingText = true;
        FindBox.Text = "";
        ReplaceBox.Text = "";
        _settingText = false;

        ReloadFromPaths(paths);
    }

    private void LoadItems(ShellPayload payload)
    {
        if (!payload.FromShell)
        {
            ItemList.ItemsSource = Array.Empty<RenameItem>();
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

    /// <summary>
    /// 按 journal 的 from↔to 映射**就地**替换列表项，保持原有顺序和勾选状态。
    /// 不能重建整个列表——那样没被改名的项会全部消失，
    /// 而需求是"打开时选中多少就保留多少，用状态体现是否改过"。
    /// </summary>
    private void RemapItems(IReadOnlyList<JournalEntry> entries, bool markRenamed)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(x => x.Ok))
        {
            if (markRenamed) map[e.From] = e.To;
            else map[e.To] = e.From;
        }

        var result = new List<RenameItem>(_items.Count);
        foreach (var item in _items)
        {
            if (!map.TryGetValue(item.FullPath, out var target))
            {
                result.Add(item);
                continue;
            }

            try
            {
                var isDir = Directory.Exists(target);
                var attrs = isDir
                    ? new DirectoryInfo(target).Attributes
                    : File.GetAttributes(target);

                var fresh = RenameEngine.CreateItem(target, isDir, attrs);
                fresh.IsEnabled = item.IsEnabled;
                fresh.IsRenamed = markRenamed;
                result.Add(fresh);
            }
            catch
            {
                // 目标已消失（被别的程序挪走）：保留原项，不吞掉
                result.Add(item);
            }
        }

        _items = result;
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
            SetError(null);
            ExecuteButton.IsEnabled = false;
            return;
        }

        var sw = Stopwatch.StartNew();
        var outcome = RenameEngine.Run(_items, BuildRule());
        sw.Stop();

        SetError(outcome.RegexError is not null ? "正则无效：" + outcome.RegexError : null);

        var willRename = _items.Count(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        var conflicts = _items.Count(i => i.State is ItemState.ConflictInBatch or ItemState.ConflictOnDisk);
        var invalid = _items.Count(i => i.State == ItemState.Invalid);

        // 执行/撤销后优先显示结果文案，别被预览统计冲掉
        FooterText.Text = _resultMessage
            ?? $"共 {_items.Count} 项 · 将改名 {willRename} · 无效 {invalid} · 冲突 {conflicts} · 引擎 {sw.ElapsedMilliseconds}ms";

        ExecuteButton.IsEnabled = !_busy && !outcome.HasBlockingError && willRename > 0;
    }

    /// <summary>
    /// 横幅用 Visibility 而不是 IsOpen 控制：收起的 InfoBar 仍会占一行高度，
    /// 把预览卡片顶下去，和左栏第一个控件错位。
    /// </summary>
    private void SetError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            ErrorBar.IsOpen = false;
            ErrorBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
            ErrorBar.Visibility = Visibility.Visible;
        }
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
            SetError("无法写入撤销日志：" + ex.Message);
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
                SetError($"部分失败：{first?.Error ?? "未知原因"}");
            }
            else
            {
                SetError(null);
            }

            // 列表切到执行后的真实路径，并清空规则（避免又显示成"要再改一次"）
            _settingText = true;
            FindBox.Text = "";
            ReplaceBox.Text = "";
            _settingText = false;

            // 保留全部项：只把成功的项就地换成新路径并标记「已改名」，其余原样留着
            RemapItems(journal.Entries, markRenamed: true);

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
                SetError("无法撤销：" + outcome.RejectReason);
                return;
            }

            var parts = new List<string> { $"已撤销 {outcome.Undone} 项" };
            if (outcome.Invalid > 0) parts.Add($"{outcome.Invalid} 项已失效");
            if (outcome.Failed > 0) parts.Add($"{outcome.Failed} 项失败");
            _resultMessage = string.Join("，", parts);

            if (outcome.Invalid > 0 || outcome.Failed > 0)
            {
                SetError("部分项目未能回滚（可能已被移动或占用）");
            }

            RemapItems(journal.Entries, markRenamed: false);
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
