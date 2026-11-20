#include "backend_internal.h"
#include <string.h>

#define STDHID_POLL_MS 60000

typedef struct StdHidBatCap {
    bool     found;
    bool     is_feature;
    uint8_t  report_id;
    uint16_t link_coll;
    USAGE    usage_page;
    USAGE    usage;
    LONG     log_min;
    LONG     log_max;
    uint16_t report_len;
} StdHidBatCap;

static bool IsBatteryValueCap(const HIDP_VALUE_CAPS* vc, USAGE* outUsage) {
    USAGE minU = vc->IsRange ? vc->Range.UsageMin : vc->NotRange.Usage;
    USAGE maxU = vc->IsRange ? vc->Range.UsageMax : vc->NotRange.Usage;

    if (vc->UsagePage == 0x06 && minU <= 0x20 && maxU >= 0x20) {
        *outUsage = 0x20;
        return true;
    }
    if (vc->UsagePage == 0x85) {
        if (minU <= 0x65 && maxU >= 0x65) {
            *outUsage = 0x65;
            return true;
        }
        if (minU <= 0x66 && maxU >= 0x66) {
            *outUsage = 0x66;
            return true;
        }
    }
    return false;
}

static bool FindStdBatteryCapInType(
    PHIDP_PREPARSED_DATA preparsed,
    HIDP_REPORT_TYPE rtype,
    USHORT capCount,
    uint16_t reportLen,
    StdHidBatCap* outCap)
{
    if (!preparsed || capCount == 0 || reportLen == 0)
        return false;

    USHORT count = capCount > 64 ? 64 : capCount;
    HIDP_VALUE_CAPS capsArr[64];
    if (HidP_GetValueCaps(rtype, capsArr, &count, preparsed) != HIDP_STATUS_SUCCESS)
        return false;

    for (USHORT i = 0; i < count; i++) {
        USAGE u = 0;
        if (IsBatteryValueCap(&capsArr[i], &u)) {
            outCap->found      = true;
            outCap->is_feature = (rtype == HidP_Feature);
            outCap->report_id  = capsArr[i].ReportID;
            outCap->link_coll  = capsArr[i].LinkCollection;
            outCap->usage_page = capsArr[i].UsagePage;
            outCap->usage      = u;
            outCap->log_min    = capsArr[i].LogicalMin;
            outCap->log_max    = capsArr[i].LogicalMax;
            outCap->report_len = reportLen;
            return true;
        }
    }
    return false;
}

static bool FindBestStdBatteryCap(PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps, StdHidBatCap* outCap) {
    ZeroMemory(outCap, sizeof(*outCap));
    if (!preparsed || !caps)
        return false;

    /* Exclude UPS / AC Power Device collections */
    if (caps->UsagePage == 0x84 && caps->Usage == 0x04)
        return false;

    if (FindStdBatteryCapInType(preparsed, HidP_Feature, caps->NumberFeatureValueCaps, caps->FeatureReportByteLength, outCap))
        return true;
    if (FindStdBatteryCapInType(preparsed, HidP_Input, caps->NumberInputValueCaps, caps->InputReportByteLength, outCap))
        return true;
    return false;
}

static int StdHid_MatchHid(
    uint16_t vid, uint16_t pid,
    uint16_t up, uint16_t usage,
    uint16_t inLen, uint16_t outLen, uint16_t featLen,
    HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
    const wchar_t* path, const wchar_t* prod,
    ConnectionMode* outMode)
{
    (void)vid;
    (void)pid;
    (void)up;
    (void)usage;
    (void)inLen;
    (void)outLen;
    (void)featLen;
    (void)hDevice;

    if (!preparsed || !caps)
        return 0;

    if (Backend_StrContainsI(prod, L"Keyboard") ||
        Backend_StrContainsI(prod, L"Headset") ||
        Backend_StrContainsI(prod, L"Controller") ||
        Backend_StrContainsI(prod, L"UPS"))
        return 0;

    StdHidBatCap batCap;
    if (!FindBestStdBatteryCap(preparsed, caps, &batCap))
        return 0;

    if (Backend_StrContainsI(path, L"bthle") || Backend_StrContainsI(path, L"bluetooth"))
        *outMode = CONN_BLUETOOTH;
    else if (Backend_StrContainsI(prod, L"Wired"))
        *outMode = CONN_WIRED;
    else
        *outMode = CONN_WIRELESS_24G;

    return 50;
}

int StdHid_ScaleValue(LONG raw, LONG logMin, LONG logMax) {
    LONG lo = logMin;
    LONG hi = logMax;
    if (hi <= lo) {
        lo = 0;
        hi = 100;
    }
    if (raw < lo) raw = lo;
    if (raw > hi) raw = hi;
    int pct = (int)(((raw - lo) * 100LL + (hi - lo) / 2LL) / (hi - lo));
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    return pct;
}

static bool StdHid_ReadFeatureValue(HANDLE hFile, PHIDP_PREPARSED_DATA pp, const StdHidBatCap* cap, DeviceStatus* st) {
    if (cap->report_len == 0 || cap->report_len > 256)
        return false;

    uint8_t buf[256] = {0};
    buf[0] = cap->report_id;
    if (!HidD_GetFeature(hFile, buf, cap->report_len))
        return false;

    ULONG val = 0;
    if (HidP_GetUsageValue(HidP_Feature, cap->usage_page, cap->link_coll, cap->usage, &val, pp, (PCHAR)buf, cap->report_len) != HIDP_STATUS_SUCCESS)
        return false;

    st->battery_pct = StdHid_ScaleValue((LONG)val, cap->log_min, cap->log_max);
    st->is_charging = false;
    st->is_full = (st->battery_pct >= 100);
    st->has_reading = true;
    st->is_awake = true;
    st->last_update_tick64 = GetTickCount64();
    return true;
}

static DWORD WINAPI StdHid_RunThread(LPVOID param) {
    BackendRuntime* rt = (BackendRuntime*)param;
    DeviceStatus st;
    Backend_InitDeviceStatus(&st, &rt->dev, g_backendStdHid.label);

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

    PHIDP_PREPARSED_DATA pp = NULL;
    HIDP_CAPS caps;
    ZeroMemory(&caps, sizeof(caps));
    StdHidBatCap cap;
    ZeroMemory(&cap, sizeof(cap));

    if (!HidD_GetPreparsedData(hFile, &pp) || HidP_GetCaps(pp, &caps) != HIDP_STATUS_SUCCESS || !FindBestStdBatteryCap(pp, &caps, &cap)) {
        if (pp) HidD_FreePreparsedData(pp);
        CloseHandle(hFile);
        rt->notify_disconnected();
        return 0;
    }

    bool disconnected = false;
    if (cap.is_feature) {
        HANDLE waits[2] = { rt->stop_event, rt->poll_now_event };
        for (;;) {
            if (StdHid_ReadFeatureValue(hFile, pp, &cap, &st)) {
                rt->publish(&st);
            } else if (GetLastError() == ERROR_DEVICE_NOT_CONNECTED) {
                disconnected = true;
                break;
            }
            DWORD w = WaitForMultipleObjects(2, waits, FALSE, STDHID_POLL_MS);
            if (w == WAIT_OBJECT_0)
                break;
        }
    } else {
        HANDLE readEv = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (readEv) {
            uint8_t buf[256];
            DWORD rlen = (cap.report_len <= 256) ? cap.report_len : 256;
            for (;;) {
                if (WaitForSingleObject(rt->stop_event, 0) == WAIT_OBJECT_0)
                    break;
                ZeroMemory(buf, sizeof(buf));
                OVERLAPPED ov;
                ZeroMemory(&ov, sizeof(ov));
                ov.hEvent = readEv;
                ResetEvent(readEv);

                DWORD n = 0;
                if (!ReadFile(hFile, buf, rlen, &n, &ov)) {
                    if (GetLastError() != ERROR_IO_PENDING) {
                        disconnected = true;
                        break;
                    }
                    HANDLE wh[2] = { rt->stop_event, readEv };
                    DWORD w = WaitForMultipleObjects(2, wh, FALSE, INFINITE);
                    if (w == WAIT_OBJECT_0) {
                        CancelIoEx(hFile, &ov);
                        break;
                    }
                    if (!GetOverlappedResult(hFile, &ov, &n, FALSE)) {
                        disconnected = true;
                        break;
                    }
                }

                if (n >= rlen && (cap.report_id == 0 || buf[0] == cap.report_id)) {
                    ULONG val = 0;
                    if (HidP_GetUsageValue(HidP_Input, cap.usage_page, cap.link_coll, cap.usage, &val, pp, (PCHAR)buf, n) == HIDP_STATUS_SUCCESS) {
                        st.battery_pct = StdHid_ScaleValue((LONG)val, cap.log_min, cap.log_max);
                        st.is_full = (st.battery_pct >= 100);
                        st.has_reading = true;
                        st.is_awake = true;
                        st.last_update_tick64 = GetTickCount64();
                        rt->publish(&st);
                    }
                }
            }
            CloseHandle(readEv);
        }
    }

    HidD_FreePreparsedData(pp);
    CloseHandle(hFile);
    if (disconnected)
        rt->notify_disconnected();
    return 0;
}

static bool StdHid_ProbeOnce(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus) {
    (void)timeoutMs;
    Backend_InitDeviceStatus(outStatus, dev, g_backendStdHid.label);

    HANDLE h = CreateFileW(dev->path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
    if (h == INVALID_HANDLE_VALUE)
        return true;

    PHIDP_PREPARSED_DATA pp = NULL;
    HIDP_CAPS caps;
    StdHidBatCap cap;
    if (HidD_GetPreparsedData(h, &pp)) {
        if (HidP_GetCaps(pp, &caps) == HIDP_STATUS_SUCCESS && FindBestStdBatteryCap(pp, &caps, &cap) && cap.is_feature) {
            StdHid_ReadFeatureValue(h, pp, &cap, outStatus);
        }
        HidD_FreePreparsedData(pp);
    }
    CloseHandle(h);
    return true;
}

const MouseBackendOps g_backendStdHid = {
    BACKEND_STD_HID_BATTERY,
    L"HID Battery",
    StdHid_MatchHid,
    StdHid_RunThread,
    StdHid_ProbeOnce
};
