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
    private CancellationTokenSource? _execCts;

    /// <summary>执行/撤销后的结果文案。非空时底栏优先显示它，不被预览统计覆盖。</summary>
    private string? _resultMessage;

    /// <summary>程序性地改输入框时置位，避免触发「用户改规则」从而清掉结果文案。</summary>
    private bool _settingText;

    /// <summary>上一次在列表里按下的 x（视口坐标）；-1 = 不是列表点击。- 见 OnItemRowClick。</summary>
    private double _lastPointerX = -1;

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

        // 状态列颜色：XAML 侧已用 {ThemeResource} 声明成 5 个画刷，这里只把"取画刷"注入给转换器。
        // 不缓存、不自己判主题——ThemeResource 换值时改的是同一个 brush 实例的 Color，
        // 已绑定的行会自动刷新。
        StateToBrushConverter.Resolver = key =>
            RootGrid.Resources.TryGetValue(key, out var v) ? v as Microsoft.UI.Xaml.Media.Brush : null;

        // 行内勾选列：ListViewItem 会吃掉子元素的指针事件，只能在已处理阶段旁听，
        // 记下按下位置供 ItemClick 判断「点的是不是勾选列」。
        ItemList.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnListItemPointerPressed), handledEventsToo: true);

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

    /// <summary>
    /// 行点击：落在勾选列（左侧 56px）才切换启用状态，点其它位置只是选中。
    /// 为什么不用行内 CheckBox：它是模板化控件，每个已实现行都要摊一份模板与动画资源——
    /// 实测 5000 项时独占 46–49MB（179.8MB → 137MB）。
    /// 为什么不用行内 Tapped：ListViewItem 会吃掉子元素的 Tapped；ItemClick 是官方整行命中通道，
    /// 但它不给点击位置，所以用 PointerPressed（handledEventsToo）记下 x 再在 ItemClick 里判断。
    /// </summary>
    private void OnItemRowClick(object sender, ItemClickEventArgs e)
    {
        if (_busy) return;
        if (_lastPointerX is < 0 or >= CheckColumnHitWidth) return;   // 非勾选列：保留默认的"选中"
        if (e.ClickedItem is not RenameItem item) return;

        item.IsEnabled = !item.IsEnabled;
        SyncSelectAllBox();
        _resultMessage = null;
        QueueRefresh();
    }

    /// <summary>勾选列的命中宽度：列宽 40 + 行内左边距，留一点余量。</summary>
    private const double CheckColumnHitWidth = 56;

    private void OnListItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerX = e.GetCurrentPoint(ItemList).Position.X;
    }

    /// <summary>
    /// 失败清单对话框（规格书 §8.9.5 / M3 遗留）。
    /// 底栏那行只能塞下首条原因，所以多项失败时用对话框把每一项的"原名 → 新名 + 原因"列出来；
    /// 超过 200 条只列前 200，其余用「复制清单」带走（避免一次塞几千个 TextBlock）。
    /// </summary>
    private async Task ShowFailureDialogAsync(IReadOnlyList<RenameEntryResult> failures)
    {
        if (failures.Count == 0) return;

        const int maxRows = 200;
        var panel = new StackPanel { Spacing = 10 };
        foreach (var f in failures.Take(maxRows))
        {
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{System.IO.Path.GetFileName(f.From)} → {System.IO.Path.GetFileName(f.To)}\n{f.Error ?? "未知原因"}"
            });
        }
        if (failures.Count > maxRows)
        {
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"…其余 {failures.Count - maxRows} 项省略，可用「复制清单」查看全部"
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"有 {failures.Count} 项改名失败",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "关闭",
            SecondaryButtonText = "复制清单",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Secondary) return;

        try
        {
            var text = string.Join(Environment.NewLine,
                failures.Select(f => $"{f.From} -> {f.To}\t{f.Error ?? "未知原因"}"));
            var pack = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pack.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pack);
        }
        catch (Exception ex)
        {
            AppLog.Error("复制失败清单到剪贴板失败", ex);
        }
    }

    /// <summary>
    /// 全选 / 全不选（规格书 §7.4 底栏）。
    ///
    /// 为什么用 Click 而不是 Checked/Unchecked/Indeterminate：
    /// ① 中间态（null）只是**显示**用的——部分勾选时告诉用户"并非全选"。它不该被用户点出来，
    ///    而 `IsThreeState="True"` 的点击循环是 false → true → null → false，所以"全勾时点一下"
    ///    会落到 null；旧实现遇到 null 直接 return，结果框停在中间态、列表一点没变（假死）。
    /// ② 三个事件对**程序性赋值**同样会触发，靠 `_syncingSelectAll` 兜着，容易再踩。
    /// 现在只认 Click，且意图完全从列表真实状态推断：不是全勾 → 全勾；已经全勾 → 全不勾，
    /// 点完立刻 SyncSelectAllBox() 把显示纠正回 true/false，中间态不会留下。
    /// </summary>
    /// <summary>§10.3：26H2 把第三方扩展默认收进「应用扩展」子菜单。
    ///
    /// 不直接用 `ms-settings:` 跳「右键菜单」专属页——那个页面是 26H2 新增的，
    /// 没有稳定的 URI；而 `LaunchUriAsync` 对无效 URI 会返回 true 却打开设置首页，
    /// 结果是用户看到一个毫无帮助的默认页（实测踩过）。
    /// 改为给可操作的步骤 + 一个「打开个性化设置」按钮兜底。
    /// </summary>
    private async void OnOpenContextMenuSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "命令没出现在右键菜单？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "26H2 默认把第三方扩展收进「应用扩展」子菜单。\n\n" +
                       "最省事：右键菜单最底部有个「自定义菜单」按钮，点它直达设置页，\n" +
                       "把「批量重命名」设为显示在主菜单。\n\n" +
                       "手动进入：设置 > 个性化 > 右键菜单。\n\n" +
                       "或者直接绕开：右键 > 发送到 > 批量重命名（不受折叠影响，多选也支持）。"
            },
            PrimaryButtonText = "打开个性化设置",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:personalization"));
        }
        catch
        {
            SetError("无法打开设置页，请手动进入：设置 > 个性化 > 右键菜单");
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var allOn = _items.All(i => i.IsEnabled);
        foreach (var item in _items) item.IsEnabled = !allOn;

        SyncSelectAllBox();
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

        // 只在「显示」层用中间态：部分勾选时给 null，全勾/全不勾给 true/false。
        // 不设 _syncingSelectAll 之类护栏了——全选框现在只监听 Click，程序性赋值不会再回调进来。
        var enabled = _items.Count(i => i.IsEnabled);
        SelectAllBox.IsChecked = enabled == 0 ? false : enabled == _items.Count ? true : null;
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
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Maximum = 1;
        BusyBar.Value = 0;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        ExecuteButton.IsEnabled = !busy &&
            _items.Any(i => i.IsEnabled && i.State is ItemState.Ok or ItemState.Warning);
        UndoButton.IsEnabled = !busy && _lastJournal is { State: JournalState.Applied };
    }

    /// <summary>执行期的「取消」：置位令牌 → sink 在下一项的 PreRenameItem 返回失败 → Shell 停止后续。</summary>
    private void OnCancelExecute(object sender, RoutedEventArgs e)
    {
        if (_execCts is null) return;

        CancelButton.IsEnabled = false;
        FooterText.Text = "正在取消…";
        try { _execCts.Cancel(); } catch { }
    }

    /// <summary>进度回调来自 STA 工作线程，必须切回 UI 线程。</summary>
    private void ReportProgress(int done, int total)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_busy) return;
            BusyBar.Maximum = Math.Max(1, total);
            BusyBar.Value = done;
            FooterText.Text = $"正在改名 {done} / {total} …";
        });
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

        List<RenameEntryResult>? failures = null;   // 等 busy 关掉之后再弹失败清单
        SetBusy(true);
        try
        {
            var runner = new FileOperationRunner();
            // AddUndoRecord: true —— 整批登记到资源管理器撤销栈，关掉本程序后 Ctrl+Z 仍然有效
            _execCts?.Dispose();
            _execCts = new CancellationTokenSource();

            var result = await runner.RunAsync(
                targets,
                new RenameRunOptions(WindowHandle, AddUndoRecord: true),
                _execCts.Token,
                ReportProgress);

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

            if (result.Cancelled)
            {
                _resultMessage = $"已取消，完成 {result.Succeeded} 项";
                SetError(null);   // 用户主动取消不算错误
            }
            else if (result.Failed == 0)
            {
                _resultMessage = $"已改名 {result.Succeeded} 项 · 撤销(Ctrl+Z)";
                SetError(null);
            }
            else
            {
                _resultMessage = $"已改名 {result.Succeeded} 项，{result.Failed} 项失败 · 撤销(Ctrl+Z)";
                var first = result.Entries.FirstOrDefault(x => !x.Ok);
                SetError($"部分失败：{first?.Error ?? "未知原因"}");

                // 底栏只能放一条原因，多项失败时列全（M3 遗留 §8.9.5）。
                // 注意不能在这里直接 await：SetBusy(false) 在 finally 里，会被对话框挡住，
                // 表现为对话框弹出时底栏还在"正在改名 N / N"、取消按钮还亮着。
                failures = result.Entries.Where(x => !x.Ok).ToList();
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

        // busy 已经关掉、取消按钮也收了，这时候列失败清单才不会被"正在改名"挡住
        if (failures is { Count: > 0 })
        {
            await ShowFailureDialogAsync(failures);
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