#include "backend_internal.h"
#include <string.h>

#define RAZER_VID        0x1532
#define RAZER_REPORT_LEN 91 /* 1 byte Report ID (0x00) + 90 byte struct razer_report */
#define RAZER_POLL_MS    60000

static int ClassifyKnownRazerPid(uint16_t pid, ConnectionMode* outMode) {
    switch (pid) {
        /* Wired charging/direct mouse modes */
        case 0x0078: /* Viper Ultimate Wired */
        case 0x007C: /* DeathAdder V2 Pro Wired */
        case 0x0086: /* Basilisk Ultimate Wired */
        case 0x008F: /* Naga Pro Wired */
        case 0x00A5: /* Viper V2 Pro Wired */
        case 0x00AA: /* Basilisk V3 Pro Wired */
        case 0x00AF: /* Cobra Pro Wired */
        case 0x00B6: /* DeathAdder V3 Pro Wired */
        case 0x00C0: /* Viper V3 Pro Wired */
        case 0x00C2: /* DeathAdder V3 Pro (variant) Wired */
            *outMode = CONN_WIRED;
            return 72;

        /* 2.4 GHz wireless / dongle modes */
        case 0x007B: /* Viper Ultimate Wireless */
        case 0x007D: /* DeathAdder V2 Pro Wireless */
        case 0x0083: /* Basilisk X HyperSpeed */
        case 0x0088: /* Basilisk Ultimate Wireless */
        case 0x0090: /* Naga Pro Wireless */
        case 0x0094: /* Orochi V2 */
        case 0x00A6: /* Viper V2 Pro Wireless */
        case 0x00AB: /* Basilisk V3 Pro Wireless */
        case 0x00B0: /* Cobra Pro Wireless */
        case 0x00B4: /* Viper V3 HyperSpeed */
        case 0x00B7: /* DeathAdder V3 Pro Wireless */
        case 0x00C1: /* Viper V3 Pro Wireless */
        case 0x00C3: /* DeathAdder V3 Pro (variant) Wireless */
            *outMode = CONN_WIRELESS_24G;
            return 72;

        default:
            return 0;
    }
}

static int Razer_MatchHid(
    uint16_t vid, uint16_t pid,
    uint16_t up, uint16_t usage,
    uint16_t inLen, uint16_t outLen, uint16_t featLen,
    HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
    const wchar_t* path, const wchar_t* prod,
    ConnectionMode* outMode)
{
    (void)up;
    (void)usage;
    (void)inLen;
    (void)outLen;
    (void)hDevice;
    (void)preparsed;
    (void)caps;
    (void)path;

    if (vid != RAZER_VID || featLen != RAZER_REPORT_LEN)
        return 0;

    if (Backend_StrContainsI(prod, L"Keyboard") ||
        Backend_StrContainsI(prod, L"Keypad") ||
        Backend_StrContainsI(prod, L"Headset"))
        return 0;

    int known = ClassifyKnownRazerPid(pid, outMode);
    if (known > 0)
        return known;

    if (Backend_StrContainsI(prod, L"Mouse") ||
        Backend_StrContainsI(prod, L"Viper") ||
        Backend_StrContainsI(prod, L"DeathAdder") ||
        Backend_StrContainsI(prod, L"Basilisk") ||
        Backend_StrContainsI(prod, L"Orochi") ||
        Backend_StrContainsI(prod, L"Naga") ||
        Backend_StrContainsI(prod, L"Cobra"))
    {
        *outMode = Backend_StrContainsI(prod, L"Wired") ? CONN_WIRED : CONN_WIRELESS_24G;
        return 68;
    }

    return 0;
}

void Razer_BuildFeatureReport(uint8_t buf[RAZER_REPORT_LEN], uint8_t txId, uint8_t cmdClass, uint8_t cmdId) {
    ZeroMemory(buf, RAZER_REPORT_LEN);
    buf[0] = 0x00;     /* Windows HID Report ID */
    buf[1] = 0x00;     /* status = new command */
    buf[2] = txId;     /* transaction id (0x1F wireless / 0x3F wired) */
    buf[6] = 0x02;     /* data_size */
    buf[7] = cmdClass; /* 0x07 */
    buf[8] = cmdId;    /* 0x80 battery / 0x84 charging */

    uint8_t crc = 0;
    for (int i = 3; i <= 88; i++)
        crc ^= buf[i];
    buf[89] = crc;
}

bool Razer_ParseBatteryReply(const uint8_t buf[RAZER_REPORT_LEN], int* outPct) {
    if (buf[1] != 0x02) /* 0x02 = Successful */
        return false;
    int raw = (int)buf[10]; /* arguments[1] = 0..255 */
    int pct = (raw * 100 + 127) / 255;
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    *outPct = pct;
    return true;
}

bool Razer_ParseChargingReply(const uint8_t buf[RAZER_REPORT_LEN], bool* outCharging) {
    if (buf[1] != 0x02)
        return false;
    *outCharging = (buf[10] != 0);
    return true;
}

static bool Razer_FeatureExchange(HANDLE hFile, uint8_t txId, uint8_t cmdClass, uint8_t cmdId, uint8_t reply[RAZER_REPORT_LEN], HANDLE stopEv) {
    uint8_t req[RAZER_REPORT_LEN];
    Razer_BuildFeatureReport(req, txId, cmdClass, cmdId);

    if (!HidD_SetFeature(hFile, req, RAZER_REPORT_LEN))
        return false;

    if (stopEv) {
        if (WaitForSingleObject(stopEv, 40) == WAIT_OBJECT_0)
            return false;
    } else {
        Sleep(40);
    }

    ZeroMemory(reply, RAZER_REPORT_LEN);
    reply[0] = 0x00;
    return HidD_GetFeature(hFile, reply, RAZER_REPORT_LEN) != FALSE;
}

static bool Razer_QueryStatus(HANDLE hFile, uint8_t* pTxId, DeviceStatus* st, HANDLE stopEv) {
    uint8_t rep[RAZER_REPORT_LEN];
    static const uint8_t txCandidates[2] = { 0x1F, 0x3F };
    bool gotBat = false;
    int pct = 0;

    uint8_t order[2] = { *pTxId, (*pTxId == txCandidates[0]) ? txCandidates[1] : txCandidates[0] };
    for (int t = 0; t < 2; t++) {
        if (Razer_FeatureExchange(hFile, order[t], 0x07, 0x80, rep, stopEv) && Razer_ParseBatteryReply(rep, &pct)) {
            *pTxId = order[t];
            gotBat = true;
            break;
        }
        if (stopEv && WaitForSingleObject(stopEv, 0) == WAIT_OBJECT_0)
            return false;
    }

    if (!gotBat)
        return false;

    bool charging = st->is_charging;
    if (Razer_FeatureExchange(hFile, *pTxId, 0x07, 0x84, rep, stopEv)) {
        bool c = false;
        if (Razer_ParseChargingReply(rep, &c))
            charging = c;
    }

    /* Sleeping Razer wireless mice sometimes reply 0%; keep last known level while disconnected/asleep */
    if (pct == 0 && !charging && st->has_reading) {
        st->is_awake = false;
        return true;
    }

    st->battery_pct = pct;
    st->is_charging = charging;
    st->is_full = (charging && pct >= 100);
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

static HANDLE Razer_OpenDevice(const wchar_t* path) {
    HANDLE h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE)
        h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    return h;
}

static DWORD WINAPI Razer_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendRazer.label);

    HANDLE hFile = Razer_OpenDevice(rt->dev.path);
    if (hFile == INVALID_HANDLE_VALUE) {
        rt->notify_disconnected();
        return 0;
    }

    uint8_t txId = (rt->dev.mode == CONN_WIRED) ? 0x3F : 0x1F;
    HANDLE waits[2] = { rt->stop_event, rt->poll_now_event };
    bool disconnected = false;

    for (;;) {
        if (Razer_QueryStatus(hFile, &txId, &st, rt->stop_event)) {
            rt->publish(&st);
        } else {
            DWORD err = GetLastError();
            if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_INVALID_HANDLE) {
                disconnected = true;
                break;
            }
        }

        DWORD w = WaitForMultipleObjects(2, waits, FALSE, RAZER_POLL_MS);
        if (w == WAIT_OBJECT_0)
            break;
    }

    CloseHandle(hFile);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool Razer_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    (void)timeoutMs;
    Backend_InitDeviceStatus(outStatus, dev, g_backendRazer.label);

    HANDLE h = Razer_OpenDevice(dev->path);
    if (h == INVALID_HANDLE_VALUE)
        return true;

    uint8_t txId = (dev->mode == CONN_WIRED) ? 0x3F : 0x1F;
    Razer_QueryStatus(h, &txId, outStatus, NULL);
    CloseHandle(h);
    return true;
}

const MouseBackendOps g_backendRazer = {
    BACKEND_RAZER_90B,
    L"Razer 2.4G",
    Razer_MatchHid,
    Razer_RunThread,
    Razer_ProbeOnce
};
