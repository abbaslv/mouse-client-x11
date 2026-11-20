#include "backend_internal.h"
#include <string.h>

#define COMPX_REPORT_LEN    17
#define COMPX_POLL_MS       60000
#define COMPX_REPLY_WAIT_MS 600

static int Compx_MatchHid(
    uint16_t vid, uint16_t pid,
    uint16_t up, uint16_t usage,
    uint16_t inLen, uint16_t outLen, uint16_t featLen,
    HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
    const wchar_t* path, const wchar_t* prod,
    ConnectionMode* outMode)
{
    (void)pid;
    (void)featLen;
    (void)hDevice;
    (void)preparsed;
    (void)caps;
    (void)path;

    if (vid != 0x3554 && vid != 0x248A && vid != 0x25A7)
        return 0;
    if (up != 0xFF02 || usage != 0x0002 || inLen != COMPX_REPORT_LEN || outLen != COMPX_REPORT_LEN)
        return 0;

    if (Backend_StrContainsI(prod, L"Wired"))
        *outMode = CONN_WIRED;
    else
        *outMode = CONN_WIRELESS_24G;
    return 80;
}

void Compx_BuildBatteryQuery(uint8_t out[COMPX_REPORT_LEN]) {
    ZeroMemory(out, COMPX_REPORT_LEN);
    out[0] = 0x08;
    out[1] = 0x04;
    out[16] = 0x49; /* Sum(bytes 0..16) & 0xFF == 0x55 */
}

bool Compx_ParseBatteryResponse(const uint8_t in[COMPX_REPORT_LEN], DeviceStatus* st) {
    if (in[0] != 0x08 || in[1] != 0x04)
        return false;

    uint32_t sum = 0;
    for (int i = 0; i < COMPX_REPORT_LEN; i++)
        sum += in[i];
    if ((sum & 0xFFu) != 0x55u)
        return false;

    /* Many Compx firmwares return pct=0 status=0 when the mouse is asleep */
    if (in[5] == 0 && in[6] == 0) {
        if (st->has_reading) {
            st->is_awake = false;
            return true;
        }
        return false;
    }

    if (in[6] > 100)
        return false;

    st->battery_pct = in[6];
    st->is_charging = (in[7] != 0);
    st->is_full = (st->is_charging && in[6] == 100);
    int mv = ((int)in[8] << 8) | in[9];
    st->voltage_mv = (mv >= 2500 && mv <= 4600) ? mv : 0;
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

static bool Compx_QueryOnce(HANDLE hFile, HANDLE ioEv, HANDLE stopEv, DeviceStatus* st, bool* outDisconnected) {
    uint8_t req[COMPX_REPORT_LEN];
    Compx_BuildBatteryQuery(req);

    OVERLAPPED ovW;
    ZeroMemory(&ovW, sizeof(ovW));
    ovW.hEvent = ioEv;
    ResetEvent(ioEv);

    DWORD written = 0;
    BOOL wOk = WriteFile(hFile, req, COMPX_REPORT_LEN, &written, &ovW);
    if (!wOk) {
        DWORD err = GetLastError();
        if (err == ERROR_IO_PENDING) {
            HANDLE wh[2] = { stopEv ? stopEv : ioEv, ioEv };
            DWORD count = stopEv ? 2u : 1u;
            DWORD w = WaitForMultipleObjects(count, wh, FALSE, 300);
            if (stopEv && w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ovW);
                return false;
            }
            if (w != (stopEv ? WAIT_OBJECT_0 + 1 : WAIT_OBJECT_0) ||
                !GetOverlappedResult(hFile, &ovW, &written, FALSE)) {
                CancelIoEx(hFile, &ovW);
                wOk = HidD_SetOutputReport(hFile, req, COMPX_REPORT_LEN);
                if (!wOk) {
                    if (outDisconnected && GetLastError() == ERROR_DEVICE_NOT_CONNECTED)
                        *outDisconnected = true;
                    return false;
                }
            }
        } else {
            wOk = HidD_SetOutputReport(hFile, req, COMPX_REPORT_LEN);
            if (!wOk) {
                if (outDisconnected && (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_GEN_FAILURE))
                    *outDisconnected = true;
                return false;
            }
        }
    }

    ULONGLONG deadline = GetTickCount64() + COMPX_REPLY_WAIT_MS;
    while (GetTickCount64() < deadline) {
        if (stopEv && WaitForSingleObject(stopEv, 0) == WAIT_OBJECT_0)
            return false;

        uint8_t buf[COMPX_REPORT_LEN] = {0};
        OVERLAPPED ovR;
        ZeroMemory(&ovR, sizeof(ovR));
        ovR.hEvent = ioEv;
        ResetEvent(ioEv);

        DWORD n = 0;
        BOOL rOk = ReadFile(hFile, buf, COMPX_REPORT_LEN, &n, &ovR);
        if (!rOk) {
            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING) {
                if (outDisconnected && err == ERROR_DEVICE_NOT_CONNECTED)
                    *outDisconnected = true;
                return false;
            }
            ULONGLONG now = GetTickCount64();
            DWORD rem = (now < deadline) ? (DWORD)(deadline - now) : 0;
            HANDLE wh[2] = { stopEv ? stopEv : ioEv, ioEv };
            DWORD count = stopEv ? 2u : 1u;
            DWORD w = WaitForMultipleObjects(count, wh, FALSE, rem);
            if (stopEv && w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ovR);
                return false;
            }
            if (w != (stopEv ? WAIT_OBJECT_0 + 1 : WAIT_OBJECT_0)) {
                CancelIoEx(hFile, &ovR);
                break;
            }
            if (!GetOverlappedResult(hFile, &ovR, &n, FALSE)) {
                DWORD gerr = GetLastError();
                if (outDisconnected && (gerr == ERROR_DEVICE_NOT_CONNECTED || gerr == ERROR_OPERATION_ABORTED))
                    *outDisconnected = (gerr == ERROR_DEVICE_NOT_CONNECTED);
                break;
            }
        }

        if (n >= COMPX_REPORT_LEN && Compx_ParseBatteryResponse(buf, st))
            return true;
    }

    return false;
}

static DWORD WINAPI Compx_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendCompx.label);

    HANDLE hFile = CreateFileW(
        rt->dev.path,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        rt->notify_disconnected();
        return 0;
    }

    HANDLE ioEv = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!ioEv) {
        CloseHandle(hFile);
        rt->notify_disconnected();
        return 0;
    }

    bool disconnected = false;
    HANDLE waits[2] = { rt->stop_event, rt->poll_now_event };

    for (;;) {
        if (Compx_QueryOnce(hFile, ioEv, rt->stop_event, &st, &disconnected))
            rt->publish(&st);
        if (disconnected)
            break;

        DWORD w = WaitForMultipleObjects(2, waits, FALSE, COMPX_POLL_MS);
        if (w == WAIT_OBJECT_0)
            break;
    }

    CloseHandle(ioEv);
    CloseHandle(hFile);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool Compx_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    (void)timeoutMs;
    Backend_InitDeviceStatus(outStatus, dev, g_backendCompx.label);

    HANDLE h = CreateFileW(
        dev->path,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        NULL);
    if (h == INVALID_HANDLE_VALUE)
        return true;

    HANDLE ev = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (ev) {
        Compx_QueryOnce(h, ev, NULL, outStatus, NULL);
        CloseHandle(ev);
    }
    CloseHandle(h);
    return true;
}

const MouseBackendOps g_backendCompx = {
    BACKEND_COMPX_17B,
    L"Compx 2.4G",
    Compx_MatchHid,
    Compx_RunThread,
    Compx_ProbeOnce
};
