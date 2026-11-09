// Stage A only: independently implemented documented WH_GETMESSAGE experiment.
// No application integration; the test runner owns installation and teardown.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static HINSTANCE module;
static UINT control;
static HWND target;
static ULONGLONG expires;

static LRESULT CALLBACK Filter(int code, WPARAM removed, LPARAM data)
{
    if (code == HC_ACTION && removed == PM_REMOVE) {
        MSG *msg = (MSG *)data;
        if (!control) control = RegisterWindowMessageW(L"WinScreen.HoverProbe.Activation.v1");
        if (!msg->hwnd && msg->message == control) {
            target = (HWND)msg->wParam;
            expires = GetTickCount64() + 10000;
        } else if (target && msg->hwnd == target && msg->message == WM_MOUSELEAVE && GetTickCount64() < expires) {
            msg->message = WM_NULL;
        }
    }
    return CallNextHookEx(NULL, code, removed, data);
}

__declspec(dllexport) HHOOK WINAPI Install(DWORD thread)
{
    if (!thread) { SetLastError(ERROR_INVALID_PARAMETER); return NULL; }
    return SetWindowsHookExW(WH_GETMESSAGE, Filter, module, thread);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}
