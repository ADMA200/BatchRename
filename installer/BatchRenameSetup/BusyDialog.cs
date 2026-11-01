using System.Runtime.InteropServices;

namespace BatchRename.Setup;

// IProgressDialog（shlobj_core.h）：Windows 自带的进度对话框。
// 用系统组件而不是自绘窗口，体积零成本，观感也和其他安装程序一致。
[ComImport, Guid("EBBC7C04-315E-11D2-B62F-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProgressDialog
{
    [PreserveSig] int StartProgressDialog(IntPtr hwndParent,
        [MarshalAs(UnmanagedType.IUnknown)] object? punkEnableModless, uint dwFlags, IntPtr pvReserved);
    [PreserveSig] int StopProgressDialog();
    [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pwzTitle);
    [PreserveSig] int SetAnimation(IntPtr hInstance, uint dwId);
    [PreserveSig] bool HasUserCancelled();
    [PreserveSig] int SetProgress(uint dwCompleted, uint dwTotal);
    [PreserveSig] int SetProgress64(ulong ullCompleted, ulong ullTotal);
    [PreserveSig] int SetLine(uint dwLineNum, [MarshalAs(UnmanagedType.LPWStr)] string pwzString,
        [MarshalAs(UnmanagedType.VariantBool)] bool fCompactPath, IntPtr pvReserved);
    [PreserveSig] int SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string pwzCancelMsg, IntPtr pvReserved);
    [PreserveSig] int Timer(uint dwTimerAction, IntPtr pvReserved);
}

[ComImport, Guid("F8383852-FCD3-11D1-A6B9-006097DF5BD4"),
 ClassInterface(ClassInterfaceType.None)]
internal class ProgressDialogCoClass
{
}

/// <summary>
/// 安装期间显示「正在做某事」的滚动进度对话框。
/// 失败时静默退化（不显示），不能让它挡住安装本身。
/// </summary>
internal sealed class BusyDialog : IDisposable
{
    private const uint PROGDLG_NOMINIMIZE = 0x00000008;
    private const uint PROGDLG_MARQUEEPROGRESS = 0x00000020;

    private IProgressDialog? _dialog;

    public BusyDialog(string title, string firstLine)
    {
        try
        {
            _dialog = (IProgressDialog)new ProgressDialogCoClass();
            _dialog.StartProgressDialog(
                IntPtr.Zero, null, PROGDLG_MARQUEEPROGRESS | PROGDLG_NOMINIMIZE, IntPtr.Zero);
            _dialog.SetTitle(title);
            _dialog.SetLine(1, firstLine, false, IntPtr.Zero);
        }
        catch
        {
            _dialog = null;   // 进度框失败不影响安装
        }
    }

    public void SetLine(string text)
    {
        try
        {
            _dialog?.SetLine(1, text, false, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>跑一段耗时工作，同时保持进度框响应（泵消息）。</summary>
    public T Run<T>(Func<T> work)
    {
        var task = Task.Run(work);
        while (!task.IsCompleted)
        {
            PumpMessages();
            Thread.Sleep(25);
        }
        return task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { _dialog?.StopProgressDialog(); } catch { }
        if (_dialog is not null)
        {
            try { Marshal.ReleaseComObject(_dialog); } catch { }
        }
        _dialog = null;
    }

    // ---- 消息泵 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
        uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    private static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
