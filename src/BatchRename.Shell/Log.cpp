#include "pch.h"
#include "Log.h"

#include <cstdio>

namespace {

constexpr unsigned long long kMaxLogBytes = 1024ull * 1024ull;  // 1MB 循环

std::wstring GetLogPath()
{
    std::wstring base;

    wchar_t buf[MAX_PATH * 2]{};
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, _countof(buf));
    if (n > 0 && n < _countof(buf)) {
        base.assign(buf);
    } else {
        // dllhost 里环境变量偶尔取不到，退回 API 拿临时目录
        wchar_t tmp[MAX_PATH + 1]{};
        if (GetTempPathW(_countof(tmp), tmp) == 0) return {};
        base.assign(tmp);
        while (!base.empty() && (base.back() == L'\\' || base.back() == L'/')) base.pop_back();
    }

    const std::wstring root = base + L"\\BatchRename";
    const std::wstring dir = root + L"\\logs";
    CreateDirectoryW(root.c_str(), nullptr);
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir + L"\\shell.log";
}

void RollIfNeeded(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return;

    const unsigned long long size =
        (static_cast<unsigned long long>(fad.nFileSizeHigh) << 32) | fad.nFileSizeLow;
    if (size < kMaxLogBytes) return;

    const std::wstring old = path + L".1";
    DeleteFileW(old.c_str());
    MoveFileW(path.c_str(), old.c_str());
}

}  // namespace

void AppendLog(const wchar_t* msg, unsigned long code)
{
    if (!msg) msg = L"(null)";

    const std::wstring path = GetLogPath();
    if (path.empty()) {
        OutputDebugStringW(msg);
        return;
    }

    RollIfNeeded(path);

    SYSTEMTIME st{};
    GetLocalTime(&st);

    wchar_t line[1024]{};
    swprintf_s(line, L"%04u-%02u-%02u %02u:%02u:%02u.%03u  [%08lX]  %s\r\n",
               st.wYear, st.wMonth, st.wDay,
               st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
               code, msg);

    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"a, ccs=UTF-8") != 0 || !f) {
        OutputDebugStringW(line);
        return;
    }
    fputws(line, f);
    fclose(f);
}
