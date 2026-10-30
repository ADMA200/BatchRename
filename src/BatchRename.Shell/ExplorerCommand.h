#pragma once

// ★ 唯一真源：改这里，然后手动同步 packaging\AppxManifest.xml 的两处
//   1) <com:Class Id="...">
//   2) 两个 <desktop5:Verb Clsid="...">
#define BATCHRENAME_CLSID_STR  L"A1B2C3D4-1111-4A2B-9C3D-000000000001"
#define BATCHRENAME_VERB_ID    L"BatchRenameFiles"

// 规格书 §5.1：WRL，不用 ATL
// 注意：声明与定义必须带一致的 noexcept，否则 C2382（不同异常规范）
class __declspec(uuid("A1B2C3D4-1111-4A2B-9C3D-000000000001"))
    BatchRenameCommand final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          IExplorerCommand, IObjectWithSite>
{
public:
    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* psia, PWSTR* ppszName) noexcept override;
    IFACEMETHODIMP GetIcon(IShellItemArray* psia, PWSTR* ppszIcon) noexcept override;
    IFACEMETHODIMP GetToolTip(IShellItemArray* psia, PWSTR* ppszInfotip) noexcept override;
    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) noexcept override;
    IFACEMETHODIMP GetState(IShellItemArray* psia, BOOL fOkToBeSlow, EXPCMDSTATE* pCmdState) noexcept override;
    IFACEMETHODIMP Invoke(IShellItemArray* psia, IBindCtx* pbc) noexcept override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) noexcept override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) noexcept override;

    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown* pUnkSite) noexcept override;
    IFACEMETHODIMP GetSite(REFIID riid, void** ppvSite) noexcept override;

private:
    Microsoft::WRL::ComPtr<IUnknown> m_site;
};
