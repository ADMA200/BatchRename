#include "pch.h"
#include "ExplorerCommand.h"
#include "Launcher.h"
#include "Log.h"

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

namespace {

// 规格书 §5.4：过滤虚拟对象，只取物理路径；一律静默跳过，不弹 UI
void CollectPaths(IShellItemArray* psia, std::vector<std::wstring>& out)
{
    if (!psia) return;

    ComPtr<IEnumShellItems> items;
    if (FAILED(psia->EnumItems(&items))) return;

    DWORD total = 0;
    psia->GetCount(&total);
    out.reserve(total);

    ComPtr<IShellItem> item;
    while (items->Next(1, &item, nullptr) == S_OK) {
        SFGAOF attrs = SFGAO_FILESYSTEM;
        if (SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM, &attrs)) &&
            (attrs & SFGAO_FILESYSTEM)) {
            PWSTR p = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p) {
                out.emplace_back(p);
                CoTaskMemFree(p);
            }
        }
        item.Reset();
    }
}

// 规格书 §5.4：失败必须可见。日志落盘 + 临时线程弹框（绝不在 STA 线程弹）
void ReportLaunchFailure(DWORD code)
{
    AppendLog(L"launch failed", code);

    std::thread([code]() {
        wchar_t msg[512]{};
        swprintf_s(msg,
                   L"批量重命名启动失败。\n\n错误码：0x%08X\n"
                   L"日志：%%LOCALAPPDATA%%\\BatchRename\\logs\\shell.log\n\n"
                   L"常见原因：主程序被安全软件拦截、bin\\publish 被移动或删除。",
                   code);
        MessageBoxW(nullptr, msg, L"批量重命名",
                    MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
    }).detach();
}

}  // namespace

IFACEMETHODIMP BatchRenameCommand::GetTitle(IShellItemArray* /*psia*/, PWSTR* ppszName) noexcept
{
    if (!ppszName) return E_POINTER;
    *ppszName = nullptr;

    // 固定文案：选中数量在打开的窗口里看更直观（§9 M1 验收后调整），
    // 菜单里带 "(N)" 反而让每次右键的标题都不一样，干扰视觉定位。
    return SHStrDupW(L"批量重命名", ppszName);
}

IFACEMETHODIMP BatchRenameCommand::GetIcon(IShellItemArray* /*psia*/, PWSTR* ppszIcon) noexcept
{
    if (!ppszIcon) return E_POINTER;
    *ppszIcon = nullptr;

    // 带 ,0 索引更稳；不做 PathFileExists（返回 E_NOTIMPL 会让图标位空白）
    const std::wstring icon = ResolveAppPath() + L",0";
    return SHStrDupW(icon.c_str(), ppszIcon);
}

IFACEMETHODIMP BatchRenameCommand::GetToolTip(IShellItemArray* /*psia*/, PWSTR* ppszInfotip) noexcept
{
    if (!ppszInfotip) return E_POINTER;
    *ppszInfotip = nullptr;
    return E_NOTIMPL;  // 别返回空串
}

IFACEMETHODIMP BatchRenameCommand::GetCanonicalName(GUID* pguidCommandName) noexcept
{
    if (!pguidCommandName) return E_POINTER;
    *pguidCommandName = __uuidof(BatchRenameCommand);  // ★ 是 GUID 值，不是字符串
    return S_OK;
}

IFACEMETHODIMP BatchRenameCommand::GetState(IShellItemArray* psia, BOOL fOkToBeSlow,
                                            EXPCMDSTATE* pCmdState) noexcept
{
    if (!pCmdState) return E_POINTER;
    *pCmdState = ECS_HIDDEN;
    if (!psia) return S_OK;  // 背景右键：无选中项

    DWORD total = 0;
    if (FAILED(psia->GetCount(&total)) || total == 0) return S_OK;

    // 非 slow 路径只探前 8 项
    const DWORD probe = fOkToBeSlow ? total : (total < 8u ? total : 8u);

    ComPtr<IEnumShellItems> items;
    if (FAILED(psia->EnumItems(&items))) return S_OK;

    for (DWORD i = 0; i < probe; ++i) {
        ComPtr<IShellItem> item;
        if (items->Next(1, &item, nullptr) != S_OK) break;

        SFGAOF attrs = SFGAO_FILESYSTEM;
        if (SUCCEEDED(item->GetAttributes(SFGAO_FILESYSTEM, &attrs)) &&
            (attrs & SFGAO_FILESYSTEM)) {
            *pCmdState = ECS_ENABLED;  // 命中即返回，不做全量扫描
            return S_OK;
        }
    }
    return S_OK;
}

IFACEMETHODIMP BatchRenameCommand::Invoke(IShellItemArray* psia, IBindCtx* /*pbc*/) noexcept
try {
    std::vector<std::wstring> paths;
    CollectPaths(psia, paths);

    if (paths.empty()) {
        // GetState 误判：选中项全是虚拟对象。只写日志不弹框
        AppendLog(L"empty selection", 0);
        return S_FALSE;
    }

    DWORD err = 0;
    if (!LaunchUI(paths, &err)) {
        ReportLaunchFailure(err);  // ★ 必须让用户看见
        return E_FAIL;
    }
    return S_OK;
} catch (...) {
    ReportLaunchFailure(E_UNEXPECTED);
    return E_FAIL;  // dllhost 里绝不能有未捕获异常
}

IFACEMETHODIMP BatchRenameCommand::GetFlags(EXPCMDFLAGS* pFlags) noexcept
{
    if (!pFlags) return E_POINTER;
    *pFlags = ECF_DEFAULT;  // 不加 ECF_HASSUBCOMMANDS
    return S_OK;
}

IFACEMETHODIMP BatchRenameCommand::EnumSubCommands(IEnumExplorerCommand** ppEnum) noexcept
{
    if (ppEnum) *ppEnum = nullptr;
    return E_NOTIMPL;
}

IFACEMETHODIMP BatchRenameCommand::SetSite(IUnknown* pUnkSite) noexcept
{
    m_site = pUnkSite;
    return S_OK;
}

IFACEMETHODIMP BatchRenameCommand::GetSite(REFIID riid, void** ppvSite) noexcept
{
    if (!ppvSite) return E_POINTER;
    *ppvSite = nullptr;
    if (!m_site) return E_FAIL;
    return m_site->QueryInterface(riid, ppvSite);
}

CoCreatableClass(BatchRenameCommand);
