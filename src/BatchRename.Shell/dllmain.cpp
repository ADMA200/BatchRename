#include "pch.h"
#include "ExplorerCommand.h"

using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hInst);
        Module<InProc>::Create();
    } else if (reason == DLL_PROCESS_DETACH) {
        Module<InProc>::GetModule().Terminate();
    }
    return TRUE;
}

// 规格书 §5.7 要求这三个导出都在。COM 激活只需要 DllGetClassObject，
// 但清单校验/加载器会探测另外两个，缺了会出现难以定位的 0x80040154。
//
// 这里用 STDAPI（与 combaseapi.h 的既有声明完全一致，避免 C2375），
// 导出由 BatchRename.Shell.def 负责，不再用 __declspec(dllexport)。
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() > 0 ? S_FALSE : S_OK;
}

STDAPI DllGetActivationFactory(HSTRING /*activatableClassId*/, void** factory)
{
    if (factory) *factory = nullptr;
    return E_NOTIMPL;  // 纯 COM 组件，不提供 WinRT 激活
}
