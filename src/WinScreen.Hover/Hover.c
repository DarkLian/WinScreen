#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Exact-target leave filtering only. Increment the API and mapping versions together if the ABI changes.
__declspec(dllexport) DWORD WINAPI ApiVersion(void) { return 2; }

typedef struct State { LONG64 target, expires, filtered, token, moves, point, ready, samples, ticks, frequency, maximum, readyAt, source, marker; } State;
typedef struct Session { HANDLE mapping, thread; State *state; HHOOK hook; } Session;
typedef struct Cache { LONG thread; HANDLE mapping; State *state; unsigned ordinal; } Cache;
static HINSTANCE module;
static Cache caches[64];
#ifdef _MSC_VER
#define THREAD_LOCAL __declspec(thread)
#else
#define THREAD_LOCAL __thread
#endif
static THREAD_LOCAL Cache *cache;

static void MappingName(WCHAR *name, DWORD process, DWORD thread)
{
    wsprintfW(name, L"Local\\WinScreen.Hover.v2.%lu.%lu", process, thread);
}

static LRESULT CALLBACK Filter(int code, WPARAM removed, LPARAM data)
{
    LARGE_INTEGER started = {0}, finished;
    State *sampled = NULL;
    if (code == HC_ACTION && removed == PM_REMOVE) {
        MSG *msg = (MSG *)data;
        if (!cache) {
            DWORD thread = GetCurrentThreadId();
            for (int i = 0; i < 64; ++i) {
                if (InterlockedCompareExchange(&caches[i].thread, (LONG)thread, 0) == 0) { cache = &caches[i]; break; }
            }
        }
        if (cache && !cache->state) {
            WCHAR name[80];
            MappingName(name, GetCurrentProcessId(), GetCurrentThreadId());
            cache->mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
            if (cache->mapping) {
                cache->state = (State *)MapViewOfFile(cache->mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(State));
                if (!cache->state) { CloseHandle(cache->mapping); cache->mapping = NULL; }
            }
        }
        State *remoteState = cache ? cache->state : NULL;
        if (remoteState) {
            if (!InterlockedCompareExchange64(&remoteState->ready, 0, 0)) {
                LARGE_INTEGER readyAt;
                QueryPerformanceCounter(&readyAt);
                InterlockedExchange64(&remoteState->readyAt, readyAt.QuadPart);
                InterlockedExchange64(&remoteState->ready, 1);
            }
            // Bounded sampling includes CallNextHookEx; no allocation or IPC in steady state.
            if ((++cache->ordinal & 63) == 0) { sampled = remoteState; QueryPerformanceCounter(&started); }
        }
        HWND target = remoteState ? (HWND)(LONG_PTR)InterlockedCompareExchange64(&remoteState->target, 0, 0) : NULL;
        if (target && msg->hwnd == target && msg->message == WM_MOUSEMOVE) {
            InterlockedIncrement64(&remoteState->moves);
            InterlockedExchange64(&remoteState->point, (LONG64)msg->lParam);
        }
        if (target && msg->message == WM_MOUSELEAVE && msg->hwnd == target &&
            (LONG64)(LONG_PTR)GetPropW(target, L"WinScreen.WindowIdentity.v1") == InterlockedCompareExchange64(&remoteState->token, 0, 0)) {
            HWND source = (HWND)(LONG_PTR)InterlockedCompareExchange64(&remoteState->source, 0, 0);
            LONG64 marker = InterlockedCompareExchange64(&remoteState->marker, 0, 0);
            if ((!source || (LONG64)(LONG_PTR)GetPropW(source, L"WinScreen.EnhancedHoverActive.v1") == marker) &&
                GetTickCount64() < (ULONGLONG)InterlockedCompareExchange64(&remoteState->expires, 0, 0)) {
                msg->message = WM_NULL;
                InterlockedIncrement64(&remoteState->filtered);
            }
        }
    }
    LRESULT result = CallNextHookEx(NULL, code, removed, data);
    if (sampled) {
        QueryPerformanceCounter(&finished);
        LONG64 elapsed = finished.QuadPart - started.QuadPart;
        InterlockedIncrement64(&sampled->samples);
        InterlockedAdd64(&sampled->ticks, elapsed);
        LONG64 previous;
        while (elapsed > (previous = InterlockedCompareExchange64(&sampled->maximum, 0, 0)) &&
            InterlockedCompareExchange64(&sampled->maximum, elapsed, previous) != previous) { }
    }
    return result;
}

__declspec(dllexport) Session *WINAPI Start(DWORD thread, HWND target, LONG64 token, HWND source, LONG64 marker)
{
    WCHAR name[80];
    DWORD pid;
    if (!thread || !target || !token || GetWindowThreadProcessId(target, &pid) != thread || pid == GetCurrentProcessId() ||
        (LONG64)(LONG_PTR)GetPropW(target, L"WinScreen.WindowIdentity.v1") != token) {
        SetLastError(ERROR_INVALID_PARAMETER); return NULL;
    }
    if ((!source != !marker) || (source && (LONG64)(LONG_PTR)GetPropW(source, L"WinScreen.EnhancedHoverActive.v1") != marker)) {
        SetLastError(ERROR_INVALID_STATE); return NULL;
    }
    Session *session = (Session *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Session));
    if (!session) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); return NULL; }
    session->thread = OpenThread(SYNCHRONIZE, FALSE, thread);
    if (!session->thread) { DWORD error = GetLastError(); HeapFree(GetProcessHeap(), 0, session); SetLastError(error); return NULL; }
    MappingName(name, pid, thread);
    session->mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(State), name);
    if (session->mapping) session->state = (State *)MapViewOfFile(session->mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(State));
    DWORD error = GetLastError();
    if (session->state) {
        if (InterlockedCompareExchange64(&session->state->target, 0, 0) != 0 &&
            GetTickCount64() < (ULONGLONG)InterlockedCompareExchange64(&session->state->expires, 0, 0)) {
            UnmapViewOfFile(session->state); CloseHandle(session->mapping);
            CloseHandle(session->thread); HeapFree(GetProcessHeap(), 0, session); SetLastError(ERROR_BUSY); return NULL;
        }
        InterlockedExchange64(&session->state->filtered, 0);
        InterlockedExchange64(&session->state->moves, 0);
        InterlockedExchange64(&session->state->point, 0);
        InterlockedExchange64(&session->state->ready, 0);
        InterlockedExchange64(&session->state->readyAt, 0);
        InterlockedExchange64(&session->state->source, (LONG64)(LONG_PTR)source);
        InterlockedExchange64(&session->state->marker, marker);
        InterlockedExchange64(&session->state->samples, 0);
        InterlockedExchange64(&session->state->ticks, 0);
        InterlockedExchange64(&session->state->maximum, 0);
        LARGE_INTEGER frequency;
        QueryPerformanceFrequency(&frequency);
        InterlockedExchange64(&session->state->frequency, frequency.QuadPart);
        InterlockedExchange64(&session->state->token, token);
        InterlockedExchange64(&session->state->expires, (LONG64)GetTickCount64() + 3000);
        InterlockedExchange64(&session->state->target, (LONG64)(LONG_PTR)target);
        session->hook = SetWindowsHookExW(WH_GETMESSAGE, Filter, module, thread);
        if (session->hook) return session;
        error = GetLastError();
        InterlockedExchange64(&session->state->target, 0);
        UnmapViewOfFile(session->state);
    }
    if (session->mapping) CloseHandle(session->mapping);
    CloseHandle(session->thread);
    HeapFree(GetProcessHeap(), 0, session);
    SetLastError(error);
    return NULL;
}

__declspec(dllexport) LONG64 WINAPI Renew(Session *session)
{
    InterlockedExchange64(&session->state->expires, (LONG64)GetTickCount64() + 3000);
    return InterlockedCompareExchange64(&session->state->filtered, 0, 0);
}

__declspec(dllexport) LONG64 WINAPI Inspect(Session *session, int point)
{
    State *state = session->state;
    LONG64 *value;
    switch (point) {
        case 0: value = &state->moves; break;
        case 1: value = &state->point; break;
        case 2: value = &state->ready; break;
        case 3: value = &state->samples; break;
        case 4: value = &state->ticks; break;
        case 5: value = &state->frequency; break;
        case 6: value = &state->maximum; break;
        case 7: value = &state->readyAt; break;
        default: return 0;
    }
    return InterlockedCompareExchange64(value, 0, 0);
}

__declspec(dllexport) BOOL WINAPI Stop(Session *session)
{
    InterlockedExchange64(&session->state->target, 0);
    if (!UnhookWindowsHookEx(session->hook)) {
        DWORD error = GetLastError();
        if (error != ERROR_INVALID_HOOK_HANDLE && error != ERROR_INVALID_HANDLE &&
            WaitForSingleObject(session->thread, 0) != WAIT_OBJECT_0) return FALSE;
    }
    UnmapViewOfFile(session->state);
    CloseHandle(session->mapping);
    CloseHandle(session->thread);
    HeapFree(GetProcessHeap(), 0, session);
    return TRUE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    if (reason == DLL_THREAD_DETACH || reason == DLL_PROCESS_DETACH) {
        for (int i = 0; i < 64; ++i) {
            if (reason == DLL_PROCESS_DETACH || caches[i].thread == (LONG)GetCurrentThreadId()) {
                if (caches[i].state) UnmapViewOfFile(caches[i].state);
                if (caches[i].mapping) CloseHandle(caches[i].mapping);
                caches[i].state = NULL;
                caches[i].mapping = NULL;
                caches[i].ordinal = 0;
                InterlockedExchange(&caches[i].thread, 0); // Publish a free slot only after cleanup.
            }
        }
    }
    return TRUE;
}
