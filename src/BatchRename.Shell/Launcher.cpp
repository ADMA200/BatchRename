#include "pch.h"
#include "Launcher.h"

using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;

namespace {

// 零 IO 取本 DLL 所在目录（尾带反斜杠）
std::wstring ModuleDir()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&ModuleDir), &self);

    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW(self, buf, MAX_PATH);

    std::wstring dir(buf);
    return dir.substr(0, dir.find_last_of(L"\\/") + 1);
}

}  // namespace

std::wstring ResolveAppPath()
{
    return ModuleDir() + L"BatchRename.App\\BatchRename.App.exe";
}

std::wstring ResolveMenuIconPath()
{
    return ModuleDir() + L"Assets\\menu.ico";
}

bool LaunchUI(const std::vector<std::wstring>& paths, DWORD* lastError)
{
    if (lastError) *lastError = 0;

    // 可继承管道，1MB 缓冲
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = nullptr;
    sa.bInheritHandle = TRUE;

    HANDLE readEnd = nullptr;
    HANDLE writeEnd = nullptr;
    if (!CreatePipe(&readEnd, &writeEnd, &sa, 1u << 20)) {
        if (lastError) *lastError = GetLastError();
        return false;
    }
    // 关键：要清的是父进程这一端（writeEnd），不是给子进程的 readEnd。
    // PROC_THREAD_ATTRIBUTE_HANDLE_LIST 白名单里的句柄必须保留 HANDLE_FLAG_INHERIT，
    // 否则 CreateProcess 直接返回 ERROR_INVALID_PARAMETER(87)。
    SetHandleInformation(writeEnd, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOEXW si{};
    si.StartupInfo.cb = sizeof(si);
    si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    si.StartupInfo.hStdInput = readEnd;
    si.StartupInfo.hStdOutput = nullptr;
    si.StartupInfo.hStdError = nullptr;

    SIZE_T attrSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attrSize);
    std::vector<BYTE> attrBuf(attrSize);
    auto attrs = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrBuf.data());
    if (!InitializeProcThreadAttributeList(attrs, 1, 0, &attrSize)) {
        if (lastError) *lastError = GetLastError();
        CloseHandle(readEnd);
        CloseHandle(writeEnd);
        return false;
    }

    // ★ 白名单：子进程只继承 readEnd，杜绝句柄泄漏到长命进程
    if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                   &readEnd, sizeof(HANDLE), nullptr, nullptr)) {
        if (lastError) *lastError = GetLastError();
        DeleteProcThreadAttributeList(attrs);
        CloseHandle(readEnd);
        CloseHandle(writeEnd);
        return false;
    }
    si.lpAttributeList = attrs;

    const std::wstring exe = ResolveAppPath();
    std::wstring cmd = L"\"" + exe + L"\"";

    PROCESS_INFORMATION pi{};
    const BOOL ok = CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr,
                                   TRUE,  // bInheritHandles
                                   EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                                   nullptr, nullptr, &si.StartupInfo, &pi);
    const DWORD err = ok ? 0 : GetLastError();

    DeleteProcThreadAttributeList(attrs);
    CloseHandle(readEnd);  // 父进程不再需要

    if (!ok) {
        CloseHandle(writeEnd);
        if (lastError) *lastError = err;
        return false;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    // 写端交给 detached 线程：万级文件时绝不能阻塞 STA 线程
    auto payload = std::make_shared<std::vector<std::wstring>>(paths);
    Module<InProc>::GetModule().IncrementObjectCount();  // 线程在跑，DLL 不许卸载
    std::thread([payload, writeEnd]() {
        for (const auto& p : *payload) {
            DWORD written = 0;
            WriteFile(writeEnd, p.data(),
                      static_cast<DWORD>(p.size() * sizeof(wchar_t)), &written, nullptr);
            const wchar_t nul = L'\0';
            WriteFile(writeEnd, &nul, sizeof(wchar_t), &written, nullptr);
        }
        CloseHandle(writeEnd);  // 关闭即 EOF，子进程结束读取
        Module<InProc>::GetModule().DecrementObjectCount();
    }).detach();

    return true;
}
