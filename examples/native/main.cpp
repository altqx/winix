#include <windows.h>
LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM w, LPARAM l) {
    switch (message) {
    case WM_CREATE:
        CreateWindowW(L"BUTTON", L"Change", WS_CHILD | WS_VISIBLE, 20, 20, 120, 35, window, reinterpret_cast<HMENU>(100), nullptr, nullptr);
        CreateWindowW(L"EDIT", L"Ready", WS_CHILD | WS_VISIBLE | WS_BORDER, 20, 70, 220, 30, window, reinterpret_cast<HMENU>(101), nullptr, nullptr);
        return 0;
    case WM_COMMAND: if (LOWORD(w) == 100) SetWindowTextW(GetDlgItem(window, 101), L"Passed"); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, w, l);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    WNDCLASSW cls{}; cls.lpfnWndProc = window_proc; cls.hInstance = instance; cls.lpszClassName = L"WinixNative";
    RegisterClassW(&cls);
    auto window = CreateWindowW(cls.lpszClassName, L"Winix Native", WS_OVERLAPPEDWINDOW, 100, 100, 400, 220, nullptr, nullptr, instance, nullptr);
    ShowWindow(window, show);
    MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
