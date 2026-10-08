#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <objbase.h>
#include <string>
#include <stdexcept>

namespace {
constexpr UINT Finished = WM_APP + 1;
HWND mainWindow{}, label{}, button{};
bool busy = false, installed = false;
std::wstring failure;

std::wstring SystemError(DWORD code) {
    wchar_t* buffer = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
        FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, code, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring result = buffer ? buffer : L"未知系统错误";
    if (buffer) LocalFree(buffer);
    return result + L"（" + std::to_wstring(code) + L"）";
}
std::wstring ProgramFiles() {
    wchar_t path[MAX_PATH];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_PROGRAM_FILES, nullptr, SHGFP_TYPE_CURRENT, path)))
        throw std::runtime_error("Cannot locate Program Files.");
    return path;
}
void WriteResource(UINT id, const std::wstring& path) {
    const auto resource = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (!resource) throw std::runtime_error("Installer payload resource is missing.");
    const auto bytes = SizeofResource(nullptr, resource);
    const auto data = LockResource(LoadResource(nullptr, resource));
    if (!data || !bytes) throw std::runtime_error("Installer payload is invalid.");
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create installer staging file.");
    DWORD written = 0;
    const BOOL ok = WriteFile(file, data, bytes, &written, nullptr);
    CloseHandle(file);
    if (!ok || written != bytes) throw std::runtime_error("Cannot write installer payload; check free disk space.");
}
int Install() {
    std::wstring stage;
    try {
        GUID guid{};
        if (FAILED(CoCreateGuid(&guid))) throw std::runtime_error("Cannot generate installation identifier.");
        wchar_t name[40]; StringFromGUID2(guid, name, 40);
        // Inherit Program Files permissions, rather than execute elevated scripts in a user-writable folder.
        stage = ProgramFiles() + L"\\.IisCertManagerSetup-" + name;
        if (!CreateDirectoryW(stage.c_str(), nullptr)) throw std::runtime_error("Cannot create protected installation folder.");
        WriteResource(101, stage + L"\\payload.zip");
        WriteResource(102, stage + L"\\bootstrap.ps1");
        SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
        const auto logPath = stage + L"\\install.log";
        HANDLE log = CreateFileW(logPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, &security,
            CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (log == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create installation log.");
        wchar_t system[MAX_PATH];
        if (!GetSystemDirectoryW(system, MAX_PATH)) { CloseHandle(log); throw std::runtime_error("Cannot locate Windows PowerShell."); }
        const auto executable = std::wstring(system) + L"\\WindowsPowerShell\\v1.0\\powershell.exe";
        std::wstring command = L"\"" + executable + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" +
            stage + L"\\bootstrap.ps1\" -Stage \"" + stage + L"\"";
        HANDLE input = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        STARTUPINFOW startup{sizeof(STARTUPINFOW)};
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdOutput = log; startup.hStdError = log; startup.hStdInput = input;
        PROCESS_INFORMATION process{};
        const BOOL started = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, stage.c_str(), &startup, &process);
        const auto startError = GetLastError();
        if (input != INVALID_HANDLE_VALUE) CloseHandle(input);
        if (!started) {
            CloseHandle(log);
            failure = L"无法启动安装步骤：" + SystemError(startError) + L"\n日志目录：" + stage;
            return 1;
        }
        CloseHandle(process.hThread);
        WaitForSingleObject(process.hProcess, INFINITE);
        DWORD code = 1; GetExitCodeProcess(process.hProcess, &code);
        CloseHandle(process.hProcess); CloseHandle(log);
        if (code != 0) {
            failure = L"安装未完成，请关闭已打开的证书管家后重试。\n错误码：" +
                std::to_wstring(code) + L"\n详细日志：" + logPath;
            return 1;
        }
        // SHFileOperation expects a double-null-terminated list. Only remove our generated staging directory.
        std::wstring remove = stage; remove.push_back(L'\0'); remove.push_back(L'\0');
        SHFILEOPSTRUCTW operation{};
        operation.wFunc = FO_DELETE; operation.pFrom = remove.c_str();
        operation.fFlags = FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT;
        SHFileOperationW(&operation);
        return 0;
    } catch (const std::exception& ex) {
        const std::string message = ex.what();
        failure = L"安装器错误：" + std::wstring(message.begin(), message.end()) + L"\n目录：" + stage;
        return 1;
    }
}
DWORD WINAPI Worker(void*) {
    const int code = Install();
    PostMessageW(mainWindow, Finished, code, 0);
    return 0;
}
LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_CREATE: {
        label = CreateWindowW(L"STATIC", L"安装 IIS 证书管家\n\n自动安装本机管理界面、后台续期服务和桌面快捷方式。\n请先启用 IIS 及 IIS 管理脚本和工具。",
            WS_CHILD | WS_VISIBLE, 24, 24, 440, 130, window, nullptr, nullptr, nullptr);
        button = CreateWindowW(L"BUTTON", L"安装 / 更新", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
            320, 176, 140, 36, window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(1)), nullptr, nullptr);
        if (!label || !button) return -1;
        const auto font = GetStockObject(DEFAULT_GUI_FONT);
        SendMessageW(label, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(button, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wparam) == 1 && !busy) {
            if (installed) {
                const auto client = ProgramFiles() + L"\\IisCertManager\\client\\IisCertManager.Client.exe";
                const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(window, L"open", client.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
                if (result <= 32) MessageBoxW(window, L"无法启动管理界面，请从桌面快捷方式重试。", L"IIS 证书管家", MB_OK | MB_ICONERROR);
                else DestroyWindow(window);
                return 0;
            }
            busy = true; EnableWindow(button, FALSE);
            SetWindowTextW(label, L"正在安装，请稍候…\n\n正在解压程序、部署后台服务并创建桌面快捷方式。");
            HANDLE worker = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
            if (worker) CloseHandle(worker);
            else { busy = false; EnableWindow(button, TRUE); MessageBoxW(window, L"无法启动安装任务，请重试。", L"安装失败", MB_OK | MB_ICONERROR); }
        }
        return 0;
    case Finished:
        busy = false; EnableWindow(button, TRUE);
        if (wparam == 0) {
            installed = true;
            SetWindowTextW(label, L"安装完成。\n\n后台服务已启动，关闭界面后仍会自动续期。\n可点击下方按钮或桌面快捷方式打开程序。");
            SetWindowTextW(button, L"打开证书管家");
        } else {
            SetWindowTextW(label, L"安装未完成，请检查提示和日志后重试。");
            MessageBoxW(window, failure.c_str(), L"安装失败", MB_OK | MB_ICONERROR);
        }
        return 0;
    case WM_TIMER: DestroyWindow(window); return 0;
    case WM_CLOSE: if (!busy) DestroyWindow(window); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    int count = 0; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    bool silent = count == 2 && std::wstring(args[1]) == L"--silent";
    bool checkUi = count == 2 && std::wstring(args[1]) == L"--ui-check";
    LocalFree(args);
    if (count != 1 && !silent && !checkUi) return 2;
    if (silent) return Install();
    WNDCLASSW klass{};
    klass.lpfnWndProc = WindowProc; klass.hInstance = instance;
    klass.lpszClassName = L"IisCertManagerNativeSetup";
    klass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    klass.hbrBackground = reinterpret_cast<HBRUSH>(static_cast<INT_PTR>(COLOR_WINDOW + 1));
    if (!RegisterClassW(&klass)) { MessageBoxW(nullptr, L"无法创建安装界面。", L"安装器错误", MB_OK | MB_ICONERROR); return 1; }
    mainWindow = CreateWindowW(klass.lpszClassName, L"IIS 证书管家 — 安装", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
        CW_USEDEFAULT, CW_USEDEFAULT, 510, 280, nullptr, nullptr, instance, nullptr);
    if (!mainWindow) { MessageBoxW(nullptr, L"无法打开安装窗口。", L"安装器错误", MB_OK | MB_ICONERROR); return 1; }
    ShowWindow(mainWindow, show); UpdateWindow(mainWindow);
    if (checkUi) SetTimer(mainWindow, 1, 500, nullptr);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        if (!IsDialogMessageW(mainWindow, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    return static_cast<int>(message.wParam);
}
