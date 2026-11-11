// LanShare Explorer context menu command.
// Implements IExplorerCommand. Registered per-user in HKCU:
//   HKCU\Software\Classes\*\shell\LanShare  -> ExplorerCommandHandler = CLSID
//   HKCU\Software\Classes\CLSID\{CLSID}\InprocServer32 -> this DLL
// Windows 11 shows IExplorerCommand verbs directly in the primary context menu.

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>

#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

namespace {

const CLSID kCommandClsid =
    {0xd4a7c1e2, 0x9b3f, 0x4e6a, {0x8c, 0x21, 0x7f, 0x5b, 0x0d, 0x9a, 0x2e, 0x31}};

HINSTANCE g_instance = nullptr;
LONG g_lock_count = 0;

std::wstring ModuleDirectory() {
  wchar_t path[MAX_PATH] = {};
  const DWORD length = GetModuleFileNameW(g_instance, path, MAX_PATH);
  if (length == 0 || length == MAX_PATH) return std::wstring();
  std::wstring result(path, length);
  const size_t slash = result.find_last_of(L"\\/");
  if (slash != std::wstring::npos) result.resize(slash);
  return result;
}

std::wstring QuoteArgument(const std::wstring& argument) {
  if (argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) return argument;

  std::wstring quoted;
  quoted.push_back(L'"');
  size_t backslashes = 0;
  for (const wchar_t ch : argument) {
    if (ch == L'\\') {
      ++backslashes;
      continue;
    }
    if (ch == L'"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted.push_back(L'"');
      backslashes = 0;
      continue;
    }
    quoted.append(backslashes, L'\\');
    backslashes = 0;
    quoted.push_back(ch);
  }
  quoted.append(backslashes * 2, L'\\');
  quoted.push_back(L'"');
  return quoted;
}

class ExplorerCommand final : public IExplorerCommand {
 public:
  ExplorerCommand() : references_(1) {}

  IFACEMETHODIMP QueryInterface(REFIID riid, void** value) override {
    if (value == nullptr) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
      *value = static_cast<IExplorerCommand*>(this);
      AddRef();
      return S_OK;
    }
    *value = nullptr;
    return E_NOINTERFACE;
  }

  IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references_); }

  IFACEMETHODIMP_(ULONG) Release() override {
    const ULONG references = InterlockedDecrement(&references_);
    if (references == 0) delete this;
    return references;
  }

  IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override {
    return SHStrDupW(L"用内网快传分享", title);
  }

  IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override {
    const std::wstring directory = ModuleDirectory();
    if (directory.empty()) return E_FAIL;
    const std::wstring path = directory + L"\\LanShare.exe,0";
    return SHStrDupW(path.c_str(), icon);
  }

  IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tooltip) override {
    return SHStrDupW(L"在局域网内生成下载链接", tooltip);
  }

  IFACEMETHODIMP GetCanonicalName(GUID* canonical_name) override {
    if (canonical_name == nullptr) return E_POINTER;
    *canonical_name = GUID_NULL;
    return S_OK;
  }

  IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
    if (state == nullptr) return E_POINTER;
    DWORD count = 0;
    if (items == nullptr || FAILED(items->GetCount(&count)) || count == 0) {
      *state = ECS_HIDDEN;
      return S_OK;
    }
    *state = ECS_ENABLED;
    return S_OK;
  }

  IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
    if (flags == nullptr) return E_POINTER;
    *flags = ECF_DEFAULT;
    return S_OK;
  }

  IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override {
    if (commands == nullptr) return E_POINTER;
    *commands = nullptr;
    return E_NOTIMPL;
  }

  IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
    if (items == nullptr) return E_INVALIDARG;

    const std::wstring directory = ModuleDirectory();
    if (directory.empty()) return E_FAIL;
    const std::wstring executable = directory + L"\\LanShare.exe";

    std::wstring command_line = QuoteArgument(executable);
    DWORD count = 0;
    HRESULT result = items->GetCount(&count);
    if (FAILED(result)) return result;

    for (DWORD index = 0; index < count; ++index) {
      IShellItem* item = nullptr;
      if (FAILED(items->GetItemAt(index, &item)) || item == nullptr) continue;
      PWSTR path = nullptr;
      if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path != nullptr) {
        command_line += L" --share ";
        command_line += QuoteArgument(path);
        CoTaskMemFree(path);
      }
      item->Release();
    }

    if (count == 0) return S_OK;

    command_line += L" --from-shell";

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    std::vector<wchar_t> buffer(command_line.begin(), command_line.end());
    buffer.push_back(L'\0');

    const BOOL created = CreateProcessW(
        executable.c_str(), buffer.data(), nullptr, nullptr, FALSE, 0, nullptr,
        directory.c_str(), &startup, &process);
    if (!created) return HRESULT_FROM_WIN32(GetLastError());

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
  }

 private:
  ~ExplorerCommand() = default;
  LONG references_;
};

class ClassFactory final : public IClassFactory {
 public:
  ClassFactory() : references_(1) {}

  IFACEMETHODIMP QueryInterface(REFIID riid, void** value) override {
    if (value == nullptr) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
      *value = static_cast<IClassFactory*>(this);
      AddRef();
      return S_OK;
    }
    *value = nullptr;
    return E_NOINTERFACE;
  }

  IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references_); }

  IFACEMETHODIMP_(ULONG) Release() override {
    const ULONG references = InterlockedDecrement(&references_);
    if (references == 0) delete this;
    return references;
  }

  IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** value) override {
    if (outer != nullptr) return CLASS_E_NOAGGREGATION;
    auto* command = new (std::nothrow) ExplorerCommand();
    if (command == nullptr) return E_OUTOFMEMORY;
    const HRESULT result = command->QueryInterface(riid, value);
    command->Release();
    return result;
  }

  IFACEMETHODIMP LockServer(BOOL lock) override {
    if (lock) {
      InterlockedIncrement(&g_lock_count);
    } else {
      InterlockedDecrement(&g_lock_count);
    }
    return S_OK;
  }

 private:
  ~ClassFactory() = default;
  LONG references_;
};

}  // namespace

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    g_instance = instance;
    DisableThreadLibraryCalls(instance);
  }
  return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** value) {
  if (value == nullptr) return E_POINTER;
  *value = nullptr;
  if (!IsEqualCLSID(clsid, kCommandClsid)) return CLASS_E_CLASSNOTAVAILABLE;

  auto* factory = new (std::nothrow) ClassFactory();
  if (factory == nullptr) return E_OUTOFMEMORY;
  const HRESULT result = factory->QueryInterface(riid, value);
  factory->Release();
  return result;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
  return g_lock_count == 0 ? S_OK : S_FALSE;
}
