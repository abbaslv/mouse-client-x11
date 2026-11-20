#ifndef X11_BACKEND_INTERNAL_H
#define X11_BACKEND_INTERNAL_H

#include "tray_common.h"
#include <hidsdi.h>
#include <setupapi.h>

typedef struct DetectedDevice {
    MouseBackendId backend_id;
    ConnectionMode mode;
    int            priority;
    uint16_t       vid;
    uint16_t       pid;
    uint16_t       usage_page;
    uint16_t       usage;
    uint16_t       in_len;
    uint16_t       out_len;
    uint16_t       feat_len;
    wchar_t        path[512];
    wchar_t        product_name[64];
} DetectedDevice;

typedef struct BackendRuntime {
    DetectedDevice dev;
    HANDLE         stop_event;
    HANDLE         poll_now_event;
    HWND           notify_hwnd;
    void         (*publish)(const DeviceStatus* st);
    void         (*notify_disconnected)(void);
} BackendRuntime;

typedef struct MouseBackendOps {
    MouseBackendId id;
    const wchar_t* label;
    int  (*match_hid)(
        uint16_t vid, uint16_t pid,
        uint16_t up, uint16_t usage,
        uint16_t inLen, uint16_t outLen, uint16_t featLen,
        HANDLE hDevice, PHIDP_PREPARSED_DATA preparsed, const HIDP_CAPS* caps,
        const wchar_t* path, const wchar_t* prod,
        ConnectionMode* outMode);
    DWORD (WINAPI *run_thread)(LPVOID param);
    bool  (*probe_once)(const DetectedDevice* dev, int timeoutMs, DeviceStatus* outStatus);
} MouseBackendOps;

void Backend_InitDeviceStatus(DeviceStatus* st, const DetectedDevice* dev, const wchar_t* label);
bool Backend_StrContainsI(const wchar_t* haystack, const wchar_t* needle);

/* Exported backend tables */
extern const MouseBackendOps g_backendX11;
extern const MouseBackendOps g_backendCompx;
extern const MouseBackendOps g_backendLogitech;
extern const MouseBackendOps g_backendRazer;
extern const MouseBackendOps g_backendStdHid;
extern const MouseBackendOps g_backendBle;

/* BLE discovery (implemented in backend_ble.c, lazy-loads BluetoothAPIs.dll) */
bool BleBackend_FindMouse(DetectedDevice* outDev);
void BleBackend_ScanAll(void);

/* Pure protocol helpers exported for --self-test */
void X11_ApplyReport(DeviceStatus* st, uint8_t b1, uint8_t b2, uint8_t b3, uint8_t b4);

void Compx_BuildBatteryQuery(uint8_t out[17]);
bool Compx_ParseBatteryResponse(const uint8_t in[17], DeviceStatus* st);

bool Logi_ParseUnifiedBattery(const uint8_t params[4], DeviceStatus* st);
bool Logi_ParseBatteryStatus(const uint8_t params[4], DeviceStatus* st);
bool Logi_ParseBatteryVoltage(const uint8_t params[4], DeviceStatus* st);

void Razer_BuildFeatureReport(uint8_t buf[91], uint8_t txId, uint8_t cmdClass, uint8_t cmdId);
bool Razer_ParseBatteryReply(const uint8_t buf[91], int* outPct);
bool Razer_ParseChargingReply(const uint8_t buf[91], bool* outCharging);

int  StdHid_ScaleValue(LONG raw, LONG logMin, LONG logMax);

#endif
