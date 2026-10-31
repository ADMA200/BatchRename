#pragma once

#include <string>
#include <vector>

// 规格书 §5.4：零 IO 定位主程序 exe（路径推导，不查注册表/环境变量）
std::wstring ResolveAppPath();

// 菜单图标：与 DLL 同级的 Assets\menu.ico。
// 不复用 exe 图标——exe 换过图标后 dllhost 仍可能命中 Shell 的旧图标缓存。
std::wstring ResolveMenuIconPath();

// 管道传路径 + 句柄白名单 + 后台写线程；失败时 *lastError 带出 GetLastError()
bool LaunchUI(const std::vector<std::wstring>& paths, DWORD* lastError);
