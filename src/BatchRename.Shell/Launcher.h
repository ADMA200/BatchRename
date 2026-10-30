#pragma once

#include <string>
#include <vector>

// 规格书 §5.4：零 IO 定位主程序 exe（路径推导，不查注册表/环境变量）
std::wstring ResolveAppPath();

// 管道传路径 + 句柄白名单 + 后台写线程；失败时 *lastError 带出 GetLastError()
bool LaunchUI(const std::vector<std::wstring>& paths, DWORD* lastError);
