#pragma once

// 规格书 §5.4 注 2：AppendLog 只能是尽力而为，写失败就 OutputDebugString 后忽略。
// 绝不可在 GetState 里调用。
void AppendLog(const wchar_t* msg, unsigned long code);
