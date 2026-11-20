#include "backend_internal.h"
#include <stdio.h>
#include <string.h>
#include <wctype.h>

static const MouseBackendOps* const kHidBackends[] = {
    &g_backendX11,
    &g_backendCompx,
    &g_backendLogitech,
    &g_backendRazer,
    &g_backendStdHid,
};

static CRITICAL_SECTION       g_cs;
static bool                   g_csInit = false;
static DeviceStatus           g_latestStatus;
static const MouseBackendOps* g_activeOps = NULL;
static BackendRuntime         g_runtime;
static HANDLE                 g_thread = NULL;

static void EnsureCs(void) {
    if (!g_csInit) {
        InitializeCriticalSection(&g_cs);
        g_csInit = true;
    }
}

void Backend_InitDeviceStatus(DeviceStatus* st, const DetectedDevice* dev, const wchar_t* label) {
    ZeroMemory(st, sizeof(*st));
    st->mode             = dev ? dev->mode : CONN_DISCONNECTED;
    st->backend_id       = dev ? dev->backend_id : BACKEND_NONE;
    st->vid              = dev ? dev->vid : 0;
    st->pid              = dev ? dev->pid : 0;
    st->is_awake         = (dev != NULL && dev->mode != CONN_DISCONNECTED);
    st->battery_pct      = 100;
    st->active_dpi_stage = -1;
    st->polling_rate_idx = -1;

    if (label && label[0]) {
        wcsncpy(st->backend_label, label, ARRAYSIZE(st->backend_label) - 1);
    }
    if (dev && dev->product_name[0]) {
        wcsncpy(st->product_name, dev->product_name, ARRAYSIZE(st->product_name) - 1);
    } else {
        wcscpy(st->product_name, L"Disconnected");
    }
}

bool Backend_StrContainsI(const wchar_t* haystack, const wchar_t* needle) {
    if (!haystack || !needle || !needle[0])
        return false;
    size_t nlen = wcslen(needle);
    for (size_t i = 0; haystack[i]; i++) {
        size_t j = 0;
        while (j < nlen && haystack[i + j] && towlower(haystack[i + j]) == towlower(needle[j]))
            j++;
        if (j == nlen)
            return true;
    }
    return false;
}

static const MouseBackendOps* FindOpsById(MouseBackendId id) {
    for (size_t i = 0; i < ARRAYSIZE(kHidBackends); i++) {
        if (kHidBackends[i]->id == id)
            return kHidBackends[i];
    }
    if (g_backendBle.id == id)
        return &g_backendBle;
    return NULL;
}

static void PublishFromWorker(const DeviceStatus* st) {
    EnsureCs();
    EnterCriticalSection(&g_cs);
    g_latestStatus = *st;
    LeaveCriticalSection(&g_cs);
    if (g_runtime.notify_hwnd)
        PostMessageW(g_runtime.notify_hwnd, WM_APP_STATUS_UPDATE, 0, 0);
}

static void NotifyDisconnectedFromWorker(void) {
    if (g_runtime.notify_hwnd)
        PostMessageW(g_runtime.notify_hwnd, WM_APP_HID_DISCONNECTED, 0, 0);
}

static bool DetectBestMouse(DetectedDevice* outDev) {
    ZeroMemory(outDev, sizeof(*outDev));

    GUID hidGuid;
    HidD_GetHidGuid(&hidGuid);
    HDEVINFO devInfo = SetupDiGetClassDevsW(&hidGuid, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo != INVALID_HANDLE_VALUE) {
        BYTE detailMem[sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) + 520 * sizeof(wchar_t)];
        for (DWORD idx = 0;; idx++) {
            SP_DEVICE_INTERFACE_DATA ifData;
            ZeroMemory(&ifData, sizeof(ifData));
            ifData.cbSize = sizeof(ifData);
            if (!SetupDiEnumDeviceInterfaces(devInfo, NULL, &hidGuid, idx, &ifData))
                break;

            PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)detailMem;
            ZeroMemory(detailMem, sizeof(detailMem));
            detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
            if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, detail, sizeof(detailMem), NULL, NULL))
                continue;

            /* Open with access=0 (query-only) so exclusive mouse/kbd locks never block discovery */
            HANDLE h = CreateFileW(detail->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
            if (h == INVALID_HANDLE_VALUE)
                continue;

            HIDD_ATTRIBUTES attrs;
            ZeroMemory(&attrs, sizeof(attrs));
            attrs.Size = sizeof(attrs);
            PHIDP_PREPARSED_DATA pp = NULL;
            HIDP_CAPS caps;
            ZeroMemory(&caps, sizeof(caps));
            wchar_t prodBuf[128] = {0};

            bool okAttr = HidD_GetAttributes(h, &attrs) != FALSE;
            bool okCaps = (HidD_GetPreparsedData(h, &pp) && HidP_GetCaps(pp, &caps) == HIDP_STATUS_SUCCESS);
            HidD_GetProductString(h, prodBuf, sizeof(prodBuf));

            if (okAttr && okCaps) {
                for (size_t b = 0; b < ARRAYSIZE(kHidBackends); b++) {
                    ConnectionMode candMode = CONN_DISCONNECTED;
                    int prio = kHidBackends[b]->match_hid(
                        attrs.VendorID, attrs.ProductID,
                        caps.UsagePage, caps.Usage,
                        caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                        h, pp, &caps,
                        detail->DevicePath, prodBuf,
                        &candMode);
                    if (prio > outDev->priority) {
                        outDev->backend_id = kHidBackends[b]->id;
                        outDev->mode       = candMode;
                        outDev->priority   = prio;
                        outDev->vid        = attrs.VendorID;
                        outDev->pid        = attrs.ProductID;
                        outDev->usage_page = caps.UsagePage;
                        outDev->usage      = caps.Usage;
                        outDev->in_len     = caps.InputReportByteLength;
                        outDev->out_len    = caps.OutputReportByteLength;
                        outDev->feat_len   = caps.FeatureReportByteLength;
                        wcsncpy(outDev->path, detail->DevicePath, ARRAYSIZE(outDev->path) - 1);
                        outDev->path[ARRAYSIZE(outDev->path) - 1] = L'\0';
                        if (prodBuf[0]) {
                            wcsncpy(outDev->product_name, prodBuf, ARRAYSIZE(outDev->product_name) - 1);
                            outDev->product_name[ARRAYSIZE(outDev->product_name) - 1] = L'\0';
                        } else {
                            wcsncpy(outDev->product_name, kHidBackends[b]->label, ARRAYSIZE(outDev->product_name) - 1);
                        }
                    }
                }
            }

            if (pp) HidD_FreePreparsedData(pp);
            CloseHandle(h);
        }
        SetupDiDestroyDeviceInfoList(devInfo);
    }

    if (outDev->backend_id != BACKEND_NONE)
        return true;

    /* Lazy fallback to Bluetooth LE ONLY when zero USB/2.4G HID battery devices matched */
    return BleBackend_FindMouse(outDev);
}

void MouseManager_Stop(void) {
    if (g_runtime.stop_event)
        SetEvent(g_runtime.stop_event);
    if (g_thread) {
        WaitForSingleObject(g_thread, 1200);
        CloseHandle(g_thread);
        g_thread = NULL;
    }
    if (g_runtime.stop_event) {
        CloseHandle(g_runtime.stop_event);
        g_runtime.stop_event = NULL;
    }
    if (g_runtime.poll_now_event) {
        CloseHandle(g_runtime.poll_now_event);
        g_runtime.poll_now_event = NULL;
    }
    g_activeOps = NULL;
    ZeroMemory(&g_runtime, sizeof(g_runtime));
}

bool MouseManager_Start(HWND notifyHwnd, DeviceStatus* outStatus) {
    EnsureCs();
    MouseManager_Stop();

    DetectedDevice best;
    if (!DetectBestMouse(&best)) {
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        EnterCriticalSection(&g_cs);
        g_latestStatus = *outStatus;
        LeaveCriticalSection(&g_cs);
        return false;
    }

    const MouseBackendOps* ops = FindOpsById(best.backend_id);
    if (!ops) {
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }

    HANDLE stopEv = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE pollEv = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!stopEv || !pollEv) {
        if (stopEv) CloseHandle(stopEv);
        if (pollEv) CloseHandle(pollEv);
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }

    ZeroMemory(&g_runtime, sizeof(g_runtime));
    g_runtime.dev                 = best;
    g_runtime.stop_event          = stopEv;
    g_runtime.poll_now_event      = pollEv;
    g_runtime.notify_hwnd         = notifyHwnd;
    g_runtime.publish             = PublishFromWorker;
    g_runtime.notify_disconnected = NotifyDisconnectedFromWorker;
    g_activeOps                   = ops;

    Backend_InitDeviceStatus(outStatus, &best, ops->label);
    EnterCriticalSection(&g_cs);
    g_latestStatus = *outStatus;
    LeaveCriticalSection(&g_cs);

    g_thread = CreateThread(NULL, 65536, ops->run_thread, &g_runtime, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!g_thread) {
        MouseManager_Stop();
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }

    return true;
}

void MouseManager_RefreshNow(void) {
    if (g_runtime.poll_now_event)
        SetEvent(g_runtime.poll_now_event);
}

bool MouseManager_OnDeviceChange(HWND notifyHwnd, DeviceStatus* outStatus) {
    EnsureCs();
    DetectedDevice next;
    bool found = DetectBestMouse(&next);

    if (!found && !g_activeOps) {
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }

    if (found && g_activeOps &&
        next.backend_id == g_runtime.dev.backend_id &&
        _wcsicmp(next.path, g_runtime.dev.path) == 0)
    {
        /* Exact same winning device already active: keep running undisturbed */
        MouseManager_GetStatus(outStatus);
        return true;
    }

    return MouseManager_Start(notifyHwnd, outStatus);
}

void MouseManager_GetStatus(DeviceStatus* outStatus) {
    EnsureCs();
    EnterCriticalSection(&g_cs);
    *outStatus = g_latestStatus;
    LeaveCriticalSection(&g_cs);
}

bool MouseManager_ProbeOnce(int timeoutMs, DeviceStatus* outStatus) {
    DetectedDevice best;
    if (!DetectBestMouse(&best)) {
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }
    const MouseBackendOps* ops = FindOpsById(best.backend_id);
    if (!ops) {
        Backend_InitDeviceStatus(outStatus, NULL, L"");
        return false;
    }
    return ops->probe_once(&best, timeoutMs, outStatus);
}

void MouseManager_ScanDevices(void) {
    GUID hidGuid;
    HidD_GetHidGuid(&hidGuid);
    HDEVINFO devInfo = SetupDiGetClassDevsW(&hidGuid, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

    wprintf(L"=== HID Battery / Mouse Interfaces ===\n");
    if (devInfo != INVALID_HANDLE_VALUE) {
        BYTE detailMem[sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) + 520 * sizeof(wchar_t)];
        for (DWORD idx = 0;; idx++) {
            SP_DEVICE_INTERFACE_DATA ifData;
            ZeroMemory(&ifData, sizeof(ifData));
            ifData.cbSize = sizeof(ifData);
            if (!SetupDiEnumDeviceInterfaces(devInfo, NULL, &hidGuid, idx, &ifData))
                break;

            PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)detailMem;
            ZeroMemory(detailMem, sizeof(detailMem));
            detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
            if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &ifData, detail, sizeof(detailMem), NULL, NULL))
                continue;

            HANDLE h = CreateFileW(detail->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
            if (h == INVALID_HANDLE_VALUE)
                continue;

            HIDD_ATTRIBUTES attrs;
            ZeroMemory(&attrs, sizeof(attrs));
            attrs.Size = sizeof(attrs);
            PHIDP_PREPARSED_DATA pp = NULL;
            HIDP_CAPS caps;
            ZeroMemory(&caps, sizeof(caps));
            wchar_t prodBuf[128] = {0};

            if (HidD_GetAttributes(h, &attrs) &&
                HidD_GetPreparsedData(h, &pp) &&
                HidP_GetCaps(pp, &caps) == HIDP_STATUS_SUCCESS)
            {
                HidD_GetProductString(h, prodBuf, sizeof(prodBuf));
                const wchar_t* matchName = L"-";
                int bestPrio = 0;
                for (size_t b = 0; b < ARRAYSIZE(kHidBackends); b++) {
                    ConnectionMode m = CONN_DISCONNECTED;
                    int p = kHidBackends[b]->match_hid(
                        attrs.VendorID, attrs.ProductID,
                        caps.UsagePage, caps.Usage,
                        caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                        h, pp, &caps,
                        detail->DevicePath, prodBuf, &m);
                    if (p > bestPrio) {
                        bestPrio = p;
                        matchName = kHidBackends[b]->label;
                    }
                }

                /* Show matched interfaces or interesting vendor/mouse/battery HID collections */
                bool interesting = (bestPrio > 0) ||
                    (caps.UsagePage == 0x01 && caps.Usage == 0x02) ||
                    (caps.UsagePage == 0x06) ||
                    (caps.UsagePage == 0x85) ||
                    (caps.UsagePage >= 0xFF00);
                if (interesting) {
                    wprintf(L"  VID=%04X PID=%04X UP=%04X U=%04X In=%u Out=%u Feat=%u Match=%ls(prio=%d) \"%ls\"\n",
                            attrs.VendorID, attrs.ProductID, caps.UsagePage, caps.Usage,
                            caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                            matchName, bestPrio, prodBuf[0] ? prodBuf : L"(unnamed)");
                }
            }

            if (pp) HidD_FreePreparsedData(pp);
            CloseHandle(h);
        }
        SetupDiDestroyDeviceInfoList(devInfo);
    }

    wprintf(L"\n=== BLE GATT Scan ===\n");
    BleBackend_ScanAll();

    DetectedDevice best;
    if (DetectBestMouse(&best)) {
        const MouseBackendOps* ops = FindOpsById(best.backend_id);
        wprintf(L"\nSelected Winner: %ls (backend=%ls, prio=%d, VID=%04X PID=%04X)\n",
                best.product_name, ops ? ops->label : L"?", best.priority, best.vid, best.pid);
    } else {
        wprintf(L"\nSelected Winner: None\n");
    }
}

#define TEST_ASSERT(cond, label) do { \
    if (!(cond)) { \
        wprintf(L"FAIL: %hs\n", label); \
        return 1; \
    } \
} while (0)

int MouseManager_RunSelfTests(void) {
    DeviceStatus st;

    /* 1. X11 Passive tests */
    DetectedDevice dX11;
    ZeroMemory(&dX11, sizeof(dX11));
    dX11.backend_id = BACKEND_X11_PASSIVE;
    dX11.mode = CONN_WIRELESS_24G;
    Backend_InitDeviceStatus(&st, &dX11, g_backendX11.label);

    X11_ApplyReport(&st, 0x55, 0x40, 0x00, 77);
    TEST_ASSERT(st.has_reading && st.battery_pct == 77 && !st.is_charging && st.is_awake, "X11 wireless battery 77%");

    X11_ApplyReport(&st, 0x55, 0x40, 0x03, 0x00);
    TEST_ASSERT(st.is_charging && st.battery_pct == 77, "X11 wireless charging preserves last pct");

    X11_ApplyReport(&st, 0x55, 0x40, 0x02, 0x00);
    TEST_ASSERT(st.is_full && !st.is_charging && st.battery_pct == 100, "X11 wireless full 100%");

    X11_ApplyReport(&st, 0x55, 0x10, 0x04, 0x00);
    X11_ApplyReport(&st, 0x55, 0x20, 0x03, 0x00);
    TEST_ASSERT(st.active_dpi_stage == 3 && st.polling_rate_idx == 3, "X11 DPI & polling decode");
    wprintf(L"PASS: X11 Passive parser\n");

    /* 2. Compx 17-byte tests */
    uint8_t cReq[17];
    Compx_BuildBatteryQuery(cReq);
    uint32_t cSum = 0;
    for (int i = 0; i < 17; i++) cSum += cReq[i];
    TEST_ASSERT(cReq[0] == 0x08 && cReq[1] == 0x04 && cReq[16] == 0x49 && ((cSum & 0xFFu) == 0x55u), "Compx query frame");

    uint8_t cResp[17] = { 0x08, 0x04, 0, 0, 0, 0x01, 64, 0x01, 0x0E, 0x94, 0, 0, 0, 0, 0, 0, 0 };
    uint32_t s = 0;
    for (int i = 0; i < 16; i++) s += cResp[i];
    cResp[16] = (uint8_t)((0x55u - (s & 0xFFu)) & 0xFFu);

    Backend_InitDeviceStatus(&st, NULL, g_backendCompx.label);
    TEST_ASSERT(Compx_ParseBatteryResponse(cResp, &st), "Compx valid response");
    TEST_ASSERT(st.has_reading && st.battery_pct == 64 && st.is_charging && st.voltage_mv == 0x0E94, "Compx fields 64% charging 3732mV");

    /* Sleep packet preserves last reading */
    uint8_t cSleep[17] = { 0x08, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x49 };
    TEST_ASSERT(Compx_ParseBatteryResponse(cSleep, &st) && st.battery_pct == 64 && !st.is_awake, "Compx sleep preserves 64%");

    cResp[16] ^= 0x01;
    TEST_ASSERT(!Compx_ParseBatteryResponse(cResp, &st), "Compx rejects bad checksum");
    wprintf(L"PASS: Compx 17B parser & checksum\n");

    /* 3. Logitech HID++ 2.0 tests */
    Backend_InitDeviceStatus(&st, NULL, g_backendLogitech.label);
    uint8_t p1004[4] = { 82, 0, 1, 0 };
    TEST_ASSERT(Logi_ParseUnifiedBattery(p1004, &st) && st.battery_pct == 82 && st.is_charging && !st.is_full, "Logitech 0x1004 SOC 82% charging");

    uint8_t p1004Coarse[4] = { 0, 0x04, 0, 0 };
    TEST_ASSERT(Logi_ParseUnifiedBattery(p1004Coarse, &st) && st.battery_pct == 75 && !st.is_charging, "Logitech 0x1004 coarse fallback");

    uint8_t p1000[4] = { 45, 20, 3, 0 };
    TEST_ASSERT(Logi_ParseBatteryStatus(p1000, &st) && st.is_full && st.battery_pct == 100, "Logitech 0x1000 full");

    uint8_t p1001Li[4] = { 0x0F, 0x0A, 0x00, 0x00 }; /* 3850 mV -> 50% */
    TEST_ASSERT(Logi_ParseBatteryVoltage(p1001Li, &st) && st.voltage_mv == 3850 && st.battery_pct == 50, "Logitech 0x1001 Li-Ion voltage 3850mV");

    uint8_t p1001Aa[4] = { 0x05, 0xDC, 0x00, 0x00 }; /* 1500 mV -> 100% */
    TEST_ASSERT(Logi_ParseBatteryVoltage(p1001Aa, &st) && st.voltage_mv == 1500 && st.battery_pct == 100, "Logitech 0x1001 AA voltage 1500mV");
    wprintf(L"PASS: Logitech HID++ 0x1004/0x1000/0x1001 parsers\n");

    /* 4. Razer 90B tests */
    uint8_t rBuf[91];
    Razer_BuildFeatureReport(rBuf, 0x1F, 0x07, 0x80);
    uint8_t rxor = 0;
    for (int i = 3; i <= 88; i++) rxor ^= rBuf[i];
    TEST_ASSERT(rBuf[0] == 0x00 && rBuf[2] == 0x1F && rBuf[7] == 0x07 && rBuf[8] == 0x80 && rBuf[89] == rxor, "Razer build CRC");

    rBuf[1] = 0x02;
    rBuf[10] = 128; /* 128/255 -> ~50% */
    int rPct = -1;
    TEST_ASSERT(Razer_ParseBatteryReply(rBuf, &rPct) && rPct == 50, "Razer parse 128/255 -> 50%");

    rBuf[10] = 1;
    bool rChg = false;
    TEST_ASSERT(Razer_ParseChargingReply(rBuf, &rChg) && rChg, "Razer parse charging=1");
    wprintf(L"PASS: Razer 90B CRC & parsers\n");

    /* 5. Standard HID scaling + priority ordering */
    TEST_ASSERT(StdHid_ScaleValue(15, 0, 20) == 75, "StdHID Scale 15 in [0..20] -> 75%");
    ConnectionMode m = CONN_DISCONNECTED;
    int pX11W = g_backendX11.match_hid(0x1D57, 0xFA55, 0x000A, 0x0000, 5, 0, 0, NULL, NULL, NULL, L"", L"X11", &m);
    int pX11G = g_backendX11.match_hid(0x1D57, 0xFA60, 0x000A, 0x0000, 5, 0, 0, NULL, NULL, NULL, L"", L"X11", &m);
    int pCpx  = g_backendCompx.match_hid(0x3554, 0xF501, 0xFF02, 0x0002, 17, 17, 0, NULL, NULL, NULL, L"", L"VXE", &m);
    int pLogi = g_backendLogitech.match_hid(0x046D, 0xC547, 0xFF00, 0x0002, 20, 20, 0, NULL, NULL, NULL, L"", L"PRO X", &m);
    int pRaz  = g_backendRazer.match_hid(0x1532, 0x00C1, 0x0001, 0x0002, 0, 0, 91, NULL, NULL, NULL, L"", L"Viper V3 Pro", &m);
    TEST_ASSERT(pX11W > pX11G && pX11G > pCpx && pCpx > pLogi && pLogi > pRaz, "Backend priority order");
    wprintf(L"PASS: StdHID scaling & backend priority ordering\n");

    return 0;
}
