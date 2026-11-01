using System.Diagnostics;
using BatchRename.App.Core;
using BatchRename.App.Models;
using BatchRename.App.Services;
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
    private readonly DispatcherQueueTimer _diskDebounce;
    private UndoJournal? _lastJournal;
    private bool _busy;

    /// <summary>程序性改「全选」勾选状态时置位，避免反过来把列表全勾/全清。</summary>
    private bool _syncingSelectAll;

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

        // AppWindow.Resize 收的是**物理像素**，不做 DPI 换算：
        // 150% 缩放下直接给 1180×720，实际只有 787×480 DIP，窗口明显偏小（REVIEW P2-16）
        try
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0) scale = 1.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(720 * scale)));
        }
        catch { }

        // handledEventsToo = true：Space 会被 ListView 自己吃掉，必须在已处理阶段也能收到
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);

        // 规格书 §8.11 两层防抖：
        //   第一层 150ms —— 只跑文本规则 + 批内互撞/环（纯内存，敲字即刷新）
        //   第二层 400ms —— 停手后才做磁盘存在性检查（要枚举目录，慢盘/网络盘上是秒级）
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(150);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => RefreshPreview(checkDisk: false);

        _diskDebounce = DispatcherQueue.CreateTimer();
        _diskDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _diskDebounce.IsRepeating = false;
        _diskDebounce.Tick += (_, _) => RefreshPreview(checkDisk: true);

        LoadItems(payload);
        RestoreLastJournal();

        // 崩溃恢复检查要等 XamlRoot 就绪
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                                   async () => await CheckPendingJournalAsync());
    }

    /// <summary>规格书 §7.5 键盘。</summary>
    private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
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
                // 🔧 有"算好但没执行"的改动时先确认，别让一个误触把整批规则白敲
                if (HasPendingChanges()) await ConfirmCloseAsync();
                else Close();
                e.Handled = true;
                break;

            case VirtualKey.A when IsCtrlDown():
                foreach (var item in _items) item.IsEnabled = true;
                SyncSelectAllBox();
                _resultMessage = null;
                QueueRefresh();
                e.Handled = true;
                break;

            case VirtualKey.Space:
                if (ItemList.SelectedItem is RenameItem sel)
                {
                    sel.IsEnabled = !sel.IsEnabled;
                    SyncSelectAllBox();
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>有未执行改动时，Esc/关闭前问一句。</summary>
    private async Task ConfirmCloseAsync()
    {
        var pending = _items.Count(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        var dlg = new ContentDialog
        {
            Title = "还有未执行的改名",
            Content = $"当前有 {pending} 项改动尚未执行，关闭窗口就会丢弃。\n" +
                      "已经改过的部分仍可以用「撤销」或资源管理器的 Ctrl+Z 回退。",
            PrimaryButtonText = "关闭",
            CloseButtonText = "继续编辑",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary) Close();
    }

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
    /// 启动时把上一次的 journal 挂回来：关掉软件再打开，「撤销」按钮仍然可用。
    /// 只认 Applied —— Pending 由 CheckPendingJournalAsync 单独处理。
    /// </summary>
    private void RestoreLastJournal()
    {
        try
        {
            if (UndoJournal.LoadLast() is { State: JournalState.Applied } last)
            {
                _lastJournal = last;
                UndoButton.IsEnabled = true;
            }
        }
        catch
        {
            // 记录损坏不影响启动
        }
    }

    /// <summary>主窗口句柄，交给 IFileOperation 当 owner（UAC 提示框、Shell 错误框要靠它）。</summary>
    private IntPtr WindowHandle
    {
        get
        {
            try { return WinRT.Interop.WindowNative.GetWindowHandle(this); }
            catch { return IntPtr.Zero; }
        }
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

    private void OnItemEnabledChanged(object sender, RoutedEventArgs e)
    {
        SyncSelectAllBox();
        OnUserRuleChanged();
    }

    /// <summary>全选 / 全不选（规格书 §7.4 底栏）。</summary>
    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        // 程序性同步（_syncingSelectAll）与"部分选中"（null）都不要反过来改列表
        if (_syncingSelectAll || SelectAllBox.IsChecked is null) return;
        if (_items.Count == 0) return;

        var on = SelectAllBox.IsChecked == true;
        foreach (var item in _items) item.IsEnabled = on;

        _resultMessage = null;
        QueueRefresh();
    }

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
        _diskDebounce.Stop();
        _diskDebounce.Start();
    }

    /// <summary>有没有"已经算好但还没执行"的改动（Esc 关闭前用它决定要不要二次确认）。</summary>
    private bool HasPendingChanges() =>
        _items.Any(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);

    /// <summary>把「全选」复选框同步成列表的真实状态（全选 / 全不选 / 部分选中）。</summary>
    private void SyncSelectAllBox()
    {
        if (_items.Count == 0) return;

        var enabled = _items.Count(i => i.IsEnabled);
        _syncingSelectAll = true;
        SelectAllBox.IsChecked = enabled == 0 ? false : enabled == _items.Count ? true : null;
        _syncingSelectAll = false;
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

    private void RefreshPreview(bool checkDisk = true)
    {
        if (_items.Count == 0)
        {
            SetError(null);
            ExecuteButton.IsEnabled = false;
            return;
        }

        var sw = Stopwatch.StartNew();
        var outcome = RenameEngine.Run(_items, BuildRule(), AutoNumberBox.IsChecked == true, checkDisk);
        sw.Stop();

        SetError(outcome.RegexError is not null ? "正则无效：" + outcome.RegexError : null);

        var willRename = _items.Count(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        var conflicts = _items.Count(i => i.State is ItemState.ConflictInBatch or ItemState.ConflictOnDisk);
        var invalid = _items.Count(i => i.State == ItemState.Invalid);

        // 执行/撤销后优先显示结果文案，别被预览统计冲掉
        FooterText.Text = _resultMessage
            ?? $"共 {_items.Count} 项 · 将改名 {willRename} · 无效 {invalid} · 冲突 {conflicts} · 引擎 {sw.ElapsedMilliseconds}ms"
               + (checkDisk || willRename == 0 ? "" : " · 磁盘校验中…");

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

        // 先吃掉还在防抖窗口里的最后一次输入：否则打完字 150ms 内点「执行」，
        // 用的还是上一轮规则算出来的名字（REVIEW P2-9）
        _debounce.Stop();
        RefreshPreview();

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
            // AddUndoRecord: true —— 整批登记到资源管理器撤销栈，关掉本程序后 Ctrl+Z 仍然有效
            var result = await runner.RunAsync(
                targets, new RenameRunOptions(WindowHandle, AddUndoRecord: true));

            // 规格书 §8.10.2：state=Applied 时必须带上逐项真实结果。
            // 不回写的话，失败项在 journal 里仍标记为 Ok=true，
            // 撤销会去回滚那些根本没改名的项，白算一次"已失效"。
            var byFrom = new Dictionary<string, RenameEntryResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in result.Entries) byFrom[r.From] = r;
            foreach (var entry in journal.Entries)
            {
                if (byFrom.TryGetValue(entry.From, out var r)) entry.Ok = r.Ok;
            }

            journal.State = JournalState.Applied;
            journal.Save();
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

            // 🔧 回归修复：执行后**不再清空**查找/替换框。
            // v1.4 清空是为了"避免又显示成要再改一次"，但那建立在"改过的项被锁定"的前提上；
            // P1-7 之后已改名项照常参与规则计算，清空只会把用户刚输入的内容丢掉——
            // 失败项无法直接重试，想再套一轮规则也得从头敲。
            // 规则留着时：已改名项若不再命中规则会回显「已改名」（RenameEngine.Run 末尾），
            // 若仍然命中就如实显示"还会再改一次"，由用户自己决定要不要按执行。

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
        catch (Exception ex)
        {
            // 🔧 P0-3：这里原来是裸 async void —— 任何异常都会冒到 DispatcherQueue，
            // WinUI 3 会直接终止进程（没有对话框、没有日志），同时 journal 永远停在 Pending。
            // 现在：按磁盘现状回写逐项 ok，保住"做过哪几步"的证据，并让窗口活下来。
            AppLog.Error($"执行失败 ({journal.Entries.Count} 项, {journal.RuleSummary})", ex);

            try
            {
                journal.ReconcileFromDisk();
                journal.Error = ex.GetType().Name + ": " + ex.Message;
                journal.Save();          // State 仍是 Pending → 下次启动提示回滚
            }
            catch (Exception saveEx)
            {
                AppLog.Error("异常回写 journal 失败", saveEx);
            }

            _resultMessage = "执行中断：" + ex.Message;
            FooterText.Text = _resultMessage;
            SetError("执行中断：" + ex.Message + "（已完成的部分会在下次启动时提示回滚）");
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
            if (outcome.AlreadyUndone > 0) parts.Add($"{outcome.AlreadyUndone} 项已是原名");
            if (outcome.Invalid > 0) parts.Add($"{outcome.Invalid} 项已失效");
            if (outcome.Failed > 0) parts.Add($"{outcome.Failed} 项失败");
            _resultMessage = string.Join("，", parts);

            if (outcome.Failed > 0)
            {
                // 部分失败：记录留在 Applied，允许再点一次（已回滚的项会被自动跳过）
                SetError("部分项目未能回滚（可能被占用或已移动），可以再点一次「撤销」重试");
            }
            else if (outcome.Invalid > 0)
            {
                SetError("部分项目已失效（可能已被改名或删除）");
            }

            RemapItems(journal.Entries, markRenamed: false);

            // 只有全部回滚到位才丢掉这条记录
            if (outcome.Completed) _lastJournal = null;
        }
        catch (Exception ex)
        {
            SetError("撤销失败：" + ex.Message);
        }
        finally
        {
            // SetBusy(false) 会按 _lastJournal 的状态重新决定按钮是否可用
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
        var detail = string.IsNullOrEmpty(pending.Error)
            ? ""
            : $"\n\n中断原因：{pending.Error}";
        var dlg = new ContentDialog
        {
            Title = "上次操作可能未完成",
            Content = $"发现一个未完成的改名批次（{okCount} 项，{pending.CreatedUtc.ToLocalTime():MM-dd HH:mm}）。\n" +
                      "回滚可以把它恢复到执行前的名字。" + detail,
            PrimaryButtonText = $"回滚 {okCount} 项",
            CloseButtonText = "忽略",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var choice = await dlg.ShowAsync();

        if (choice == ContentDialogResult.Primary)
        {
            // 旧版这里调的是 UndoAsync，而它对 Pending 明确定义为"拒绝"——
            // 于是回滚按钮 100% 失败（REVIEW P0-2）。回滚走独立的入口。
            var outcome = await pending.RollbackPendingAsync();
            FooterText.Text = outcome.Rejected
                ? "回滚失败：" + outcome.RejectReason
                : $"已回滚 {outcome.Undone} 项"
                  + (outcome.Invalid > 0 ? $"，{outcome.Invalid} 项已失效" : "")
                  + (outcome.Failed > 0 ? $"，{outcome.Failed} 项失败（下次启动会再次提示）" : "");
        }
        else
        {
            // 用户选择忽略 → 丢弃这条记录，下次不再提示
            pending.Discard();
        }
    }
}
