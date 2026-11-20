#include "backend_internal.h"
#include <string.h>

static int X11_MatchHid(
    uint16_t vid, uint16_t pid,
    uint16_t up, uint16_t usage,
    uint16_t inLen, uint16_t outLen, uint16_t featLen,
    HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
    const wchar_t* path, const wchar_t* prod,
    ConnectionMode* outMode)
{
    (void)outLen;
    (void)featLen;
    (void)hDevice;
    (void)preparsed;
    (void)caps;
    (void)path;
    (void)prod;

    if (vid != X11_VID)
        return 0;
    if (up != X11_MONITOR_UP || usage != X11_MONITOR_USAGE || inLen != X11_MONITOR_IN_LEN)
        return 0;

    if (pid == X11_PID_WIRED) {
        *outMode = CONN_WIRED;
        return 100;
    }
    if (pid == X11_PID_24G) {
        *outMode = CONN_WIRELESS_24G;
        return 90;
    }
    return 0;
}

void X11_ApplyReport(DeviceStatus* st, uint8_t b1, uint8_t b2, uint8_t b3, uint8_t b4) {
    if (b1 != 0x55)
        return;

    uint16_t arg16 = (uint16_t)(b3 | ((uint16_t)b4 << 8));

    switch (b2) {
        case 0x10:
            if (arg16 >= 1 && arg16 <= 8)
                st->active_dpi_stage = arg16 - 1;
            break;

        case 0x20:
            if (arg16 <= 3)
                st->polling_rate_idx = arg16;
            break;

        case 0x40:
            if (st->mode == CONN_WIRELESS_24G)
                st->is_awake = true;

            if (st->mode == CONN_WIRED) {
                st->has_reading = true;
                st->last_update_tick64 = GetTickCount64();
                if (b3 == 2) {
                    st->is_charging = false;
                    st->is_full = true;
                    st->battery_pct = 100;
                } else {
                    st->is_charging = true;
                    st->is_full = false;
                }
            } else {
                if (b3 == 3) {
                    st->is_charging = true;
                    st->is_full = false;
                    st->has_reading = true;
                    st->last_update_tick64 = GetTickCount64();
                } else if (b3 == 2) {
                    st->is_charging = false;
                    st->is_full = true;
                    st->battery_pct = 100;
                    st->has_reading = true;
                    st->last_update_tick64 = GetTickCount64();
                } else if (b4 <= 100) {
                    st->is_charging = false;
                    st->is_full = false;
                    st->battery_pct = b4;
                    st->has_reading = true;
                    st->last_update_tick64 = GetTickCount64();
                }
            }
            break;

        case 0x60:
            if (arg16 >= 1 && arg16 <= 10) {
                st->battery_pct = arg16 * 10;
                st->has_reading = true;
                st->last_update_tick64 = GetTickCount64();
            }
            break;

        default:
            break;
    }
}

static DWORD WINAPI X11_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendX11.label);

    HANDLE hFile = CreateFileW(
        rt->dev.path,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        rt->notify_disconnected();
        return 0;
    }

    HANDLE readEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!readEvent) {
        CloseHandle(hFile);
        rt->notify_disconnected();
        return 0;
    }

    uint8_t buf[16];
    bool disconnected = false;

    for (;;) {
        if (WaitForSingleObject(rt->stop_event, 0) == WAIT_OBJECT_0)
            break;

        ZeroMemory(buf, sizeof(buf));
        OVERLAPPED ov;
        ZeroMemory(&ov, sizeof(ov));
        ov.hEvent = readEvent;
        ResetEvent(readEvent);

        DWORD readBytes = 0;
        BOOL ok = ReadFile(hFile, buf, X11_MONITOR_IN_LEN, &readBytes, &ov);
        if (!ok) {
            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING) {
                disconnected = true;
                break;
            }
            HANDLE waitHandles[2] = { rt->stop_event, readEvent };
            DWORD w = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);
            if (w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ov);
                break;
            }
            if (!GetOverlappedResult(hFile, &ov, &readBytes, FALSE)) {
                disconnected = true;
                break;
            }
        }

        if (readBytes >= X11_MONITOR_IN_LEN && buf[0] == 0x03) {
            X11_ApplyReport(&st, buf[1], buf[2], buf[3], buf[4]);
            rt->publish(&st);
        }
    }

    CloseHandle(readEvent);
    CloseHandle(hFile);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool X11_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    Backend_InitDeviceStatus(outStatus, dev, g_backendX11.label);

    HANDLE h = CreateFileW(
        dev->path,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        NULL);
    if (h == INVALID_HANDLE_VALUE)
        return true;

    HANDLE ev = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (ev) {
        ULONGLONG deadline = GetTickCount64() + (ULONGLONG)timeoutMs;
        while (GetTickCount64() < deadline && !outStatus->has_reading) {
            uint8_t buf[16] = {0};
            OVERLAPPED ov;
            ZeroMemory(&ov, sizeof(ov));
            ov.hEvent = ev;
            ResetEvent(ev);
            DWORD n = 0;
            BOOL ok = ReadFile(h, buf, X11_MONITOR_IN_LEN, &n, &ov);
            if (!ok) {
                if (GetLastError() != ERROR_IO_PENDING)
                    break;
                ULONGLONG now = GetTickCount64();
                DWORD rem = (now < deadline) ? (DWORD)(deadline - now) : 0;
                if (WaitForSingleObject(ev, rem) != WAIT_OBJECT_0) {
                    CancelIoEx(h, &ov);
                    break;
                }
                if (!GetOverlappedResult(h, &ov, &n, FALSE))
                    break;
            }
            if (n >= X11_MONITOR_IN_LEN && buf[0] == 0x03) {
                X11_ApplyReport(outStatus, buf[1], buf[2], buf[3], buf[4]);
            }
        }
        CloseHandle(ev);
    }

    CloseHandle(h);
    return true;
}

const MouseBackendOps g_backendX11 = {
    BACKEND_X11_PASSIVE,
    L"X11 Passive",
    X11_MatchHid,
    X11_RunThread,
    X11_ProbeOnce
};
