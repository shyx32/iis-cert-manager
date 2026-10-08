#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <objbase.h>
#include <commctrl.h>
#include <string>
#include <stdexcept>

namespace {
constexpr UINT Finished = WM_APP + 1, StageChanged = WM_APP + 2;
HWND mainWindow{}, label{}, button{}, progress{}, closeButton{};
HFONT bodyFont{}, titleFont{}, headingFont{};
HBRUSH headerBrush{}, whiteBrush{};
int dpi = 96;
int Scale(int value) { return MulDiv(value, dpi, 96); }
HWND Text(HWND parent, const wchar_t* value, int x, int y, int width, int height, HFONT font) {
    HWND control = CreateWindowW(L"STATIC", value, WS_CHILD | WS_VISIBLE,
        Scale(x), Scale(y), Scale(width), Scale(height), parent, nullptr, nullptr, nullptr);
    SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
    return control;
}
void ReportStage(int value) { if (mainWindow) PostMessageW(mainWindow, StageChanged, static_cast<WPARAM>(value), 0); }
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
        ReportStage(1);
        GUID guid{};
        if (FAILED(CoCreateGuid(&guid))) throw std::runtime_error("Cannot generate installation identifier.");
        wchar_t name[40]; StringFromGUID2(guid, name, 40);
        // Inherit Program Files permissions, rather than execute elevated scripts in a user-writable folder.
        stage = ProgramFiles() + L"\\.IisCertManagerSetup-" + name;
        if (!CreateDirectoryW(stage.c_str(), nullptr)) throw std::runtime_error("Cannot create protected installation folder.");
        WriteResource(101, stage + L"\\payload.zip");
        WriteResource(102, stage + L"\\bootstrap.ps1");
        ReportStage(2);
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
        Text(window, L"IIS 证书管家", 88, 24, 540, 36, titleFont);
        Text(window, L"网站证书与自动续期", 90, 68, 540, 24, bodyFont);
        Text(window, L"准备安装", 28, 148, 624, 28, headingFont);
        Text(window, L"IIS 站点读取  ·  自动签发  ·  HTTPS 部署  ·  自动续期\n支持 HTTP-01 与阿里云 DNS-01", 28, 194, 624, 58, bodyFont);
        Text(window, L"安装位置", 28, 264, 624, 22, bodyFont);
        const auto path = ProgramFiles() + L"\\IisCertManager";
        Text(window, path.c_str(), 28, 292, 624, 24, bodyFont);
        label = Text(window, L"准备就绪。将安装管理界面、后台服务和桌面快捷方式。", 28, 338, 624, 46, bodyFont);
        progress = CreateWindowW(PROGRESS_CLASSW, nullptr, WS_CHILD | PBS_MARQUEE,
            Scale(28), Scale(398), Scale(624), Scale(3), window, nullptr, nullptr, nullptr);
        const auto client = path + L"\\client\\IisCertManager.Client.exe";
        const bool upgrade = GetFileAttributesW(client.c_str()) != INVALID_FILE_ATTRIBUTES;
        button = CreateWindowW(L"BUTTON", upgrade ? L"更新安装" : L"立即安装", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            Scale(494), Scale(422), Scale(158), Scale(36), window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(1)), nullptr, nullptr);
        closeButton = CreateWindowW(L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            Scale(380), Scale(422), Scale(104), Scale(36), window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(2)), nullptr, nullptr);
        if (!label || !button || !closeButton || !progress) return -1;
        SendMessageW(button, WM_SETFONT, reinterpret_cast<WPARAM>(bodyFont), TRUE);
        SendMessageW(closeButton, WM_SETFONT, reinterpret_cast<WPARAM>(bodyFont), TRUE);
        return 0;
    }
    case WM_CTLCOLORSTATIC: {
        const auto dc = reinterpret_cast<HDC>(wparam);
        RECT bounds{}; GetWindowRect(reinterpret_cast<HWND>(lparam), &bounds);
        MapWindowPoints(nullptr, window, reinterpret_cast<POINT*>(&bounds), 2);
        const bool header = bounds.top < Scale(118);
        SetTextColor(dc, RGB(23, 43, 69));
        SetBkColor(dc, header ? RGB(248, 250, 252) : RGB(255, 255, 255));
        return reinterpret_cast<LRESULT>(header ? headerBrush : whiteBrush);
    }
    case WM_PAINT: {
        PAINTSTRUCT paint{}; const auto dc = BeginPaint(window, &paint);
        RECT area{}; GetClientRect(window, &area); area.bottom = Scale(118);
        FillRect(dc, &area, headerBrush);
        const auto icon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(103));
        DrawIconEx(dc, Scale(28), Scale(28), icon, Scale(44), Scale(44), 0, nullptr, DI_NORMAL);
        EndPaint(window, &paint); return 0;
    }
    case DM_GETDEFID: return MAKELONG(1, DC_HASDEFID);
    case WM_DRAWITEM: {
        const auto item = reinterpret_cast<DRAWITEMSTRUCT*>(lparam);
        const bool primary = item->CtlID == 1;
        const bool disabled = (item->itemState & ODS_DISABLED) != 0;
        const COLORREF fill = primary ? (disabled ? RGB(148, 176, 238) : (item->itemState & ODS_SELECTED) ? RGB(29, 78, 216) : RGB(37, 99, 235)) : RGB(255, 255, 255);
        const auto brush = CreateSolidBrush(fill);
        const auto pen = CreatePen(PS_SOLID, 1, primary ? fill : RGB(213, 221, 231));
        const auto oldBrush = SelectObject(item->hDC, brush), oldPen = SelectObject(item->hDC, pen);
        RoundRect(item->hDC, item->rcItem.left, item->rcItem.top, item->rcItem.right, item->rcItem.bottom, Scale(8), Scale(8));
        SelectObject(item->hDC, oldBrush); SelectObject(item->hDC, oldPen); DeleteObject(brush); DeleteObject(pen);
        SetBkMode(item->hDC, TRANSPARENT); SetTextColor(item->hDC, primary ? RGB(255, 255, 255) : RGB(51, 65, 85));
        const auto oldFont = SelectObject(item->hDC, bodyFont);
        wchar_t caption[128]{}; GetWindowTextW(item->hwndItem, caption, 128);
        RECT text = item->rcItem; DrawTextW(item->hDC, caption, -1, &text, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        if (item->itemState & ODS_FOCUS) { InflateRect(&text, -Scale(5), -Scale(5)); DrawFocusRect(item->hDC, &text); }
        SelectObject(item->hDC, oldFont); return TRUE;
    }
    case WM_COMMAND:
        if (LOWORD(wparam) == 2 && !busy) { DestroyWindow(window); return 0; }
        if (LOWORD(wparam) == 1 && !busy) {
            if (installed) {
                const auto client = ProgramFiles() + L"\\IisCertManager\\client\\IisCertManager.Client.exe";
                std::wstring command = L"\"" + client + L"\"";
                STARTUPINFOW startup{sizeof(STARTUPINFOW)}; PROCESS_INFORMATION process{};
                if (!CreateProcessW(client.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
                    MessageBoxW(window, (L"无法启动管理界面：" + SystemError(GetLastError())).c_str(), L"IIS 证书管家", MB_OK | MB_ICONERROR);
                else { CloseHandle(process.hThread); CloseHandle(process.hProcess); DestroyWindow(window); }
                return 0;
            }
            busy = true; EnableWindow(button, FALSE); EnableWindow(closeButton, FALSE);
            SetWindowTextW(button, L"正在安装…");
            SetWindowTextW(label, L"正在准备安装文件，请稍候…");
            ShowWindow(progress, SW_SHOW); SendMessageW(progress, PBM_SETMARQUEE, TRUE, 25);
            HANDLE worker = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
            if (worker) CloseHandle(worker);
            else {
                busy = false; EnableWindow(button, TRUE); EnableWindow(closeButton, TRUE);
                SendMessageW(progress, PBM_SETMARQUEE, FALSE, 0); ShowWindow(progress, SW_HIDE);
                SetWindowTextW(button, L"重新安装"); SetWindowTextW(label, L"无法启动安装任务，请重试。");
            }
        }
        return 0;
    case StageChanged:
        SetWindowTextW(label, wparam == 1 ? L"1 / 2  正在解压安装文件…" : L"2 / 2  正在部署程序、启动服务并创建快捷方式…");
        return 0;
    case Finished:
        busy = false; EnableWindow(button, TRUE); EnableWindow(closeButton, TRUE);
        SendMessageW(progress, PBM_SETMARQUEE, FALSE, 0); ShowWindow(progress, SW_HIDE);
        if (wparam == 0) {
            installed = true;
            SetWindowTextW(label, L"安装完成！后台服务已启动。\n打开程序后即可读取 IIS 站点并配置证书自动签发。");
            SetWindowTextW(button, L"打开证书管家"); SetWindowTextW(closeButton, L"完成");
        } else {
            SetWindowTextW(button, L"重试安装");
            SetWindowTextW(label, L"安装未完成。请检查 IIS 是否启用，并关闭已打开的证书管家。\n详细错误和日志位置见提示。");
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
    SetProcessDPIAware();
    HDC screen = GetDC(nullptr); dpi = GetDeviceCaps(screen, LOGPIXELSX); ReleaseDC(nullptr, screen);
    bodyFont = CreateFontW(-Scale(14), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    titleFont = CreateFontW(-Scale(24), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    headingFont = CreateFontW(-Scale(18), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    headerBrush = CreateSolidBrush(RGB(248, 250, 252)); whiteBrush = CreateSolidBrush(RGB(255, 255, 255));
    INITCOMMONCONTROLSEX controls{sizeof(INITCOMMONCONTROLSEX), ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
    WNDCLASSW klass{};
    klass.lpfnWndProc = WindowProc; klass.hInstance = instance;
    klass.lpszClassName = L"IisCertManagerNativeSetup";
    klass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    klass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(103));
    klass.hbrBackground = reinterpret_cast<HBRUSH>(static_cast<INT_PTR>(COLOR_WINDOW + 1));
    if (!RegisterClassW(&klass)) { MessageBoxW(nullptr, L"无法创建安装界面。", L"安装器错误", MB_OK | MB_ICONERROR); return 1; }
    RECT frame{0, 0, Scale(680), Scale(480)};
    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRect(&frame, style, FALSE);
    const int width = frame.right - frame.left, height = frame.bottom - frame.top;
    RECT desktop{}; SystemParametersInfoW(SPI_GETWORKAREA, 0, &desktop, 0);
    mainWindow = CreateWindowW(klass.lpszClassName, L"IIS 证书管家 — 安装", style,
        desktop.left + (desktop.right - desktop.left - width) / 2, desktop.top + (desktop.bottom - desktop.top - height) / 2, width, height, nullptr, nullptr, instance, nullptr);
    if (!mainWindow) { MessageBoxW(nullptr, L"无法打开安装窗口。", L"安装器错误", MB_OK | MB_ICONERROR); return 1; }
    ShowWindow(mainWindow, show); UpdateWindow(mainWindow);
    if (checkUi) SetTimer(mainWindow, 1, 500, nullptr);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        if (!IsDialogMessageW(mainWindow, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    DeleteObject(bodyFont); DeleteObject(titleFont); DeleteObject(headingFont);
    DeleteObject(headerBrush); DeleteObject(whiteBrush);
    return static_cast<int>(message.wParam);
}
