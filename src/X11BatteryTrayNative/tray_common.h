#ifndef X11_TRAY_COMMON_H
#define X11_TRAY_COMMON_H

#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <stdint.h>
#include <stdbool.h>

#define X11_VID                0x1D57
#define X11_PID_WIRED          0xFA55
#define X11_PID_24G            0xFA60
#define X11_MONITOR_UP         0x000A
#define X11_MONITOR_USAGE      0x0000
#define X11_MONITOR_IN_LEN     5

#define WM_APP_TRAYICON         (WM_APP + 1)
#define WM_APP_STATUS_UPDATE    (WM_APP + 2)
#define WM_APP_HID_DISCONNECTED (WM_APP + 3)

#define TIMER_SLEEP_WATCHDOG    1
#define TIMER_UI_REFRESH        2
#define TIMER_RESCAN_DEBOUNCE   3

#define SLEEP_WATCHDOG_MS       6000
#define UI_REFRESH_MS           30000

typedef enum ConnectionMode {
    CONN_DISCONNECTED = 0,
    CONN_WIRED        = 1,
    CONN_WIRELESS_24G = 2,
    CONN_BLUETOOTH    = 3
} ConnectionMode;

typedef enum MouseBackendId {
    BACKEND_NONE            = 0,
    BACKEND_X11_PASSIVE     = 1,
    BACKEND_COMPX_17B       = 2,
    BACKEND_LOGITECH_HIDPP  = 3,
    BACKEND_RAZER_90B       = 4,
    BACKEND_STD_HID_BATTERY = 5,
    BACKEND_BLE_GATT        = 6
} MouseBackendId;

typedef enum TrayIconStyle {
    STYLE_DIGITS_BAR    = 0,
    STYLE_BATTERY_GAUGE = 1,
    STYLE_LARGE_DIGITS  = 2
} TrayIconStyle;

typedef struct DeviceStatus {
    ConnectionMode mode;
    MouseBackendId backend_id;
    wchar_t        backend_label[32];
    uint16_t       vid;
    uint16_t       pid;
    int            voltage_mv; /* 0 if unavailable */
    bool           is_awake;
    bool           is_charging;
    bool           is_full;
    bool           has_reading;
    int            battery_pct;
    int            active_dpi_stage; /* -1 if unknown, 0..7 otherwise */
    int            polling_rate_idx; /* -1 if unknown, 0..3 otherwise */
    ULONGLONG      last_update_tick64;
    wchar_t        product_name[64];
} DeviceStatus;

typedef struct TraySettings {
    TrayIconStyle style;
    bool low_alert_enabled;
    int  low_alert_threshold;
} TraySettings;

/* Registry / Theme helpers (implemented in main.c) */
void TraySettings_Load(TraySettings* s);
void TraySettings_Save(const TraySettings* s);
bool Startup_IsEnabled(void);
void Startup_SetEnabled(bool enable);
bool Theme_IsLightTaskbar(void);

/* Multi-Mouse Single-Active-Backend Manager (mouse_manager.c) */
bool MouseManager_Start(HWND notifyHwnd, DeviceStatus* outStatus);
void MouseManager_Stop(void);
void MouseManager_RefreshNow(void);
bool MouseManager_OnDeviceChange(HWND notifyHwnd, DeviceStatus* outStatus);
void MouseManager_GetStatus(DeviceStatus* outStatus);
bool MouseManager_ProbeOnce(int timeoutMs, DeviceStatus* outStatus);
void MouseManager_ScanDevices(void);
int  MouseManager_RunSelfTests(void);

/* Native 32-bit DIB Icon Renderer (icon_renderer.c) */
int   IconRenderer_GetNativeSize(void);
HICON IconRenderer_CreateTrayIcon(const DeviceStatus* st, TrayIconStyle style, bool isLightTaskbar);
bool  IconRenderer_WritePreviewBmp(const wchar_t* path);

/* Native GDI Flyout Popup (flyout_window.c) */
typedef void (*FlyoutActionFn)(void);
bool      Flyout_Init(HINSTANCE hInst, FlyoutActionFn onRescan, FlyoutActionFn onOpenCenter, FlyoutActionFn onPinTaskbar, FlyoutActionFn onExit);
void      Flyout_Destroy(void);
void      Flyout_ShowNearTray(const DeviceStatus* st);
void      Flyout_Hide(void);
void      Flyout_Update(const DeviceStatus* st);
bool      Flyout_IsVisible(void);
ULONGLONG Flyout_LastDeactivateTick64(void);

#endif
