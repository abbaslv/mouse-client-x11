#include "backend_internal.h"
#include <string.h>

#define LOGI_VID               0x046D
#define LOGI_POLL_MS           60000
#define LOGI_REPLY_TIMEOUT_MS  350

static int Logi_MatchHid(
    uint16_t vid, uint16_t pid,
    uint16_t up, uint16_t usage,
    uint16_t inLen, uint16_t outLen, uint16_t featLen,
    HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
    const wchar_t* path, const wchar_t* prod,
    ConnectionMode* outMode)
{
    (void)pid;
    (void)inLen;
    (void)outLen;
    (void)featLen;
    (void)hDevice;
    (void)preparsed;
    (void)caps;
    (void)path;

    if (vid != LOGI_VID || up != 0xFF00)
        return 0;
    if (usage != 0x0002 && usage != 0x0001)
        return 0;

    *outMode = Backend_StrContainsI(prod, L"Wired") ? CONN_WIRED : CONN_WIRELESS_24G;
    return (usage == 0x0002) ? 76 : 74;
}

static void Logi_ApplyStatusByte(uint8_t statusByte, DeviceStatus* st) {
    switch (statusByte) {
        case 0: /* Discharging */
            st->is_charging = false;
            st->is_full = false;
            break;
        case 1: /* Charging */
        case 2: /* Charging slow / final stage */
            st->is_charging = true;
            st->is_full = false;
            break;
        case 3: /* Charge complete */
            st->is_charging = false;
            st->is_full = true;
            st->battery_pct = 100;
            break;
        case 4: /* Recharging error / not charging */
        default:
            st->is_charging = false;
            st->is_full = false;
            break;
    }
}

bool Logi_ParseUnifiedBattery(const uint8_t params[4], DeviceStatus* st) {
    int pct = (int)params[0];
    if (pct > 100 || (pct == 0 && params[1] != 0)) {
        uint8_t levels = params[1];
        if (levels & 0x08) pct = 100;
        else if (levels & 0x04) pct = 75;
        else if (levels & 0x02) pct = 30;
        else if (levels & 0x01) pct = 10;
        else pct = 0;
    }
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;

    st->battery_pct = pct;
    Logi_ApplyStatusByte(params[2], st);
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

bool Logi_ParseBatteryStatus(const uint8_t params[4], DeviceStatus* st) {
    int pct = (int)params[0];
    if (pct > 100)
        return false;

    st->battery_pct = pct;
    Logi_ApplyStatusByte(params[2], st);
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

static int EstimatePctFromVoltageMv(int mv) {
    int pct;
    if (mv > 2000) {
        /* 1S Li-Ion discharge curve approx 3500..4200 mV */
        pct = ((mv - 3500) * 100) / 700;
    } else {
        /* Single AA/AAA primary cell approx 1050..1500 mV */
        pct = ((mv - 1050) * 100) / 450;
    }
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    return pct;
}

bool Logi_ParseBatteryVoltage(const uint8_t params[4], DeviceStatus* st) {
    int mv = ((int)params[0] << 8) | params[1];
    if (mv < 700 || mv > 5000)
        return false;

    st->voltage_mv = mv;
    st->battery_pct = EstimatePctFromVoltageMv(mv);
    uint8_t flags = params[2];
    bool extPower = (flags & 0x80u) != 0;
    uint8_t chgState = flags & 0x07u;
    if (extPower && chgState == 3) {
        st->is_charging = false;
        st->is_full = true;
        st->battery_pct = 100;
    } else if (extPower) {
        st->is_charging = true;
        st->is_full = false;
    } else {
        st->is_charging = false;
        st->is_full = false;
    }
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

typedef struct LogiEndpoint {
    uint8_t  dev_idx;
    uint16_t bat_feature_id;
    uint8_t  bat_feature_idx;
    uint16_t out_len;
    uint16_t in_len;
} LogiEndpoint;

static bool Logi_Transact(
    HANDLE hFile, HANDLE ioEv, HANDLE stopEv,
    uint16_t outLen, uint16_t inLen,
    uint8_t devIdx, uint8_t featIdx, uint8_t funcId,
    const uint8_t args[3], uint8_t outReply[20])
{
    uint8_t req[20];
    ZeroMemory(req, sizeof(req));
    bool useLong = (outLen >= 20);
    DWORD txLen = useLong ? 20u : (outLen >= 7 ? 7u : (DWORD)outLen);
    if (txLen < 7u) txLen = 7u;

    uint8_t funcSw = (uint8_t)(((funcId & 0x0Fu) << 4) | 0x0Au);
    req[0] = useLong ? 0x11 : 0x10;
    req[1] = devIdx;
    req[2] = featIdx;
    req[3] = funcSw;
    if (args) {
        req[4] = args[0];
        req[5] = args[1];
        req[6] = args[2];
    }

    OVERLAPPED ovW;
    ZeroMemory(&ovW, sizeof(ovW));
    ovW.hEvent = ioEv;
    ResetEvent(ioEv);
    DWORD written = 0;
    if (!WriteFile(hFile, req, txLen, &written, &ovW)) {
        if (GetLastError() != ERROR_IO_PENDING) {
            if (!HidD_SetOutputReport(hFile, req, txLen))
                return false;
        } else {
            HANDLE wh[2] = { stopEv ? stopEv : ioEv, ioEv };
            DWORD w = WaitForMultipleObjects(stopEv ? 2u : 1u, wh, FALSE, 250);
            if (stopEv && w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ovW);
                return false;
            }
            if (w != (stopEv ? WAIT_OBJECT_0 + 1 : WAIT_OBJECT_0) ||
                !GetOverlappedResult(hFile, &ovW, &written, FALSE)) {
                CancelIoEx(hFile, &ovW);
                return false;
            }
        }
    }

    DWORD rxCap = (inLen >= 20) ? 20u : ((inLen >= 7) ? (DWORD)inLen : 20u);
    ULONGLONG deadline = GetTickCount64() + LOGI_REPLY_TIMEOUT_MS;
    while (GetTickCount64() < deadline) {
        if (stopEv && WaitForSingleObject(stopEv, 0) == WAIT_OBJECT_0)
            return false;

        uint8_t buf[32] = {0};
        OVERLAPPED ovR;
        ZeroMemory(&ovR, sizeof(ovR));
        ovR.hEvent = ioEv;
        ResetEvent(ioEv);

        DWORD n = 0;
        if (!ReadFile(hFile, buf, rxCap, &n, &ovR)) {
            if (GetLastError() != ERROR_IO_PENDING)
                return false;
            ULONGLONG now = GetTickCount64();
            DWORD rem = (now < deadline) ? (DWORD)(deadline - now) : 0;
            HANDLE wh[2] = { stopEv ? stopEv : ioEv, ioEv };
            DWORD w = WaitForMultipleObjects(stopEv ? 2u : 1u, wh, FALSE, rem);
            if (stopEv && w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ovR);
                return false;
            }
            if (w != (stopEv ? WAIT_OBJECT_0 + 1 : WAIT_OBJECT_0)) {
                CancelIoEx(hFile, &ovR);
                return false;
            }
            if (!GetOverlappedResult(hFile, &ovR, &n, FALSE))
                return false;
        }

        if (n < 7 || (buf[0] != 0x10 && buf[0] != 0x11))
            continue;
        if (buf[1] != devIdx)
            continue;

        /* HID++ 2.0 error reply or HID++ 1.0 error reply */
        if (buf[2] == 0xFF && buf[3] == featIdx && (buf[4] & 0xF0u) == (funcSw & 0xF0u))
            return false;
        if (buf[2] == 0x8F)
            return false;

        if (buf[2] == featIdx && (buf[3] & 0xF0u) == (funcSw & 0xF0u)) {
            memcpy(outReply, buf, 20);
            return true;
        }
    }

    return false;
}

static uint8_t Logi_GetFeatureIndex(HANDLE hFile, HANDLE ioEv, HANDLE stopEv, const LogiEndpoint* ep, uint8_t devIdx, uint16_t featId) {
    uint8_t args[3] = { (uint8_t)(featId >> 8), (uint8_t)(featId & 0xFF), 0x00 };
    uint8_t rep[20] = {0};
    if (!Logi_Transact(hFile, ioEv, stopEv, ep->out_len, ep->in_len, devIdx, 0x00, 0x00, args, rep))
        return 0;
    return rep[4];
}

static bool Logi_IsSlotMouse(HANDLE hFile, HANDLE ioEv, HANDLE stopEv, const LogiEndpoint* ep, uint8_t devIdx) {
    /* Optional 0x0005 IFeatureSet / DeviceType check: fn 2 getDeviceType -> 0=Keyboard, 3=Mouse */
    uint8_t idx = Logi_GetFeatureIndex(hFile, ioEv, stopEv, ep, devIdx, 0x0005);
    if (idx == 0)
        return true; /* Not implemented -> do not exclude */
    uint8_t rep[20] = {0};
    if (!Logi_Transact(hFile, ioEv, stopEv, ep->out_len, ep->in_len, devIdx, idx, 0x02, NULL, rep))
        return true;
    /* In 0x0005 fn 2, device type 3 = Mouse */
    return rep[4] == 0x03;
}

static bool Logi_DiscoverMouse(HANDLE hFile, HANDLE ioEv, HANDLE stopEv, LogiEndpoint* ep) {
    static const uint8_t  slots[7]  = { 0xFF, 1, 2, 3, 4, 5, 6 };
    static const uint16_t feats[3]  = { 0x1004, 0x1000, 0x1001 };

    for (int s = 0; s < 7; s++) {
        if (stopEv && WaitForSingleObject(stopEv, 0) == WAIT_OBJECT_0)
            return false;

        uint8_t devIdx = slots[s];
        /* Quick Root ping (fn 1 = getProtocolVersion) */
        uint8_t pingRep[20] = {0};
        uint8_t pingArgs[3] = { 0, 0, 0xAA };
        if (!Logi_Transact(hFile, ioEv, stopEv, ep->out_len, ep->in_len, devIdx, 0x00, 0x01, pingArgs, pingRep))
            continue;

        if (!Logi_IsSlotMouse(hFile, ioEv, stopEv, ep, devIdx))
            continue;

        for (int f = 0; f < 3; f++) {
            uint8_t fidx = Logi_GetFeatureIndex(hFile, ioEv, stopEv, ep, devIdx, feats[f]);
            if (fidx != 0) {
                ep->dev_idx = devIdx;
                ep->bat_feature_id = feats[f];
                ep->bat_feature_idx = fidx;
                return true;
            }
        }
    }
    return false;
}

static bool Logi_QueryBattery(HANDLE hFile, HANDLE ioEv, HANDLE stopEv, const LogiEndpoint* ep, DeviceStatus* st) {
    uint8_t fn = (ep->bat_feature_id == 0x1004) ? 0x01 : 0x00;
    uint8_t rep[20] = {0};
    if (!Logi_Transact(hFile, ioEv, stopEv, ep->out_len, ep->in_len, ep->dev_idx, ep->bat_feature_idx, fn, NULL, rep))
        return false;

    if (ep->bat_feature_id == 0x1004) return Logi_ParseUnifiedBattery(&rep[4], st);
    if (ep->bat_feature_id == 0x1000) return Logi_ParseBatteryStatus(&rep[4], st);
    return Logi_ParseBatteryVoltage(&rep[4], st);
}

static DWORD WINAPI Logi_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendLogitech.label);

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
    HANDLE readEv = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!ioEv || !readEv) {
        if (ioEv) CloseHandle(ioEv);
        if (readEv) CloseHandle(readEv);
        CloseHandle(hFile);
        rt->notify_disconnected();
        return 0;
    }

    LogiEndpoint ep = { 0xFF, 0, 0, rt->dev.out_len, rt->dev.in_len };
    bool discovered = Logi_DiscoverMouse(hFile, ioEv, rt->stop_event, &ep);
    if (discovered && Logi_QueryBattery(hFile, ioEv, rt->stop_event, &ep, &st)) {
        rt->publish(&st);
    }

    DWORD rxCap = (ep.in_len >= 20) ? 20u : ((ep.in_len >= 7) ? (DWORD)ep.in_len : 20u);
    bool disconnected = false;
    uint8_t buf[32];

    for (;;) {
        if (WaitForSingleObject(rt->stop_event, 0) == WAIT_OBJECT_0)
            break;

        ZeroMemory(buf, sizeof(buf));
        OVERLAPPED ovR;
        ZeroMemory(&ovR, sizeof(ovR));
        ovR.hEvent = readEv;
        ResetEvent(readEv);

        DWORD n = 0;
        BOOL pending = FALSE;
        BOOL ok = ReadFile(hFile, buf, rxCap, &n, &ovR);
        if (!ok) {
            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING) {
                disconnected = true;
                break;
            }
            pending = TRUE;
        }

        if (pending) {
            HANDLE waits[3] = { rt->stop_event, rt->poll_now_event, readEv };
            DWORD w = WaitForMultipleObjects(3, waits, FALSE, LOGI_POLL_MS);
            if (w == WAIT_OBJECT_0) {
                CancelIoEx(hFile, &ovR);
                break;
            }
            if (w == WAIT_OBJECT_0 + 1 || w == WAIT_TIMEOUT) {
                CancelIoEx(hFile, &ovR);
                if (!discovered)
                    discovered = Logi_DiscoverMouse(hFile, ioEv, rt->stop_event, &ep);
                if (discovered && Logi_QueryBattery(hFile, ioEv, rt->stop_event, &ep, &st))
                    rt->publish(&st);
                continue;
            }
            if (w != WAIT_OBJECT_0 + 2 || !GetOverlappedResult(hFile, &ovR, &n, FALSE)) {
                disconnected = (GetLastError() == ERROR_DEVICE_NOT_CONNECTED);
                if (disconnected) break;
                continue;
            }
        }

        if (discovered && n >= 7 && (buf[0] == 0x10 || buf[0] == 0x11) &&
            buf[1] == ep.dev_idx && buf[2] == ep.bat_feature_idx && (buf[3] & 0xF0u) == 0x00u)
        {
            bool parsed = (ep.bat_feature_id == 0x1004) ? Logi_ParseUnifiedBattery(&buf[4], &st)
                        : (ep.bat_feature_id == 0x1000) ? Logi_ParseBatteryStatus(&buf[4], &st)
                        : Logi_ParseBatteryVoltage(&buf[4], &st);
            if (parsed)
                rt->publish(&st);
        }
    }

    CloseHandle(readEv);
    CloseHandle(ioEv);
    CloseHandle(hFile);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool Logi_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    (void)timeoutMs;
    Backend_InitDeviceStatus(outStatus, dev, g_backendLogitech.label);

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
        LogiEndpoint ep = { 0xFF, 0, 0, dev->out_len, dev->in_len };
        if (Logi_DiscoverMouse(h, ev, NULL, &ep))
            Logi_QueryBattery(h, ev, NULL, &ep, outStatus);
        CloseHandle(ev);
    }
    CloseHandle(h);
    return true;
}

const MouseBackendOps g_backendLogitech = {
    BACKEND_LOGITECH_HIDPP,
    L"Logitech HID++",
    Logi_MatchHid,
    Logi_RunThread,
    Logi_ProbeOnce
};
