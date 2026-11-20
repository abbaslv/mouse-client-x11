#include "tray_common.h"
#include <shellapi.h>
#include <dbt.h>
#include <stdio.h>
#include <string.h>

#define MAIN_WND_CLASS       L"AttackSharkX11BatteryTrayHiddenWnd"
#define REG_SETTINGS_KEY     L"Software\\AttackSharkX11Clean\\Tray"
#define REG_RUN_KEY          L"Software\\Microsoft\\Windows\\CurrentVersion\\Run"
#define REG_RUN_VALUE        L"AttackSharkX11Battery"
#define REG_THEME_KEY        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"

#define IDM_STYLE_DIGITS_BAR 2001
#define IDM_STYLE_LARGE      2002
#define IDM_STYLE_GAUGE      2003
#define IDM_LOW_ALERT        2004
#define IDM_AUTOSTART        2005
#define IDM_PIN_TASKBAR      2006
#define IDM_RESCAN           2007
#define IDM_OPEN_CENTER      2008
#define IDM_EXIT             2009

static const GUID X11_GUID_DEVINTERFACE_HID = {
    0x4D1E55B2, 0xF16F, 0x11CF, { 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30 }
};
static const GUID X11_GUID_DEVINTERFACE_BLE = {
    0x781aee18, 0x7733, 0x4ce4, { 0xad, 0xd0, 0x91, 0xf4, 0x1c, 0x67, 0xb5, 0x92 }
};

static HINSTANCE       g_hInst = NULL;
static HWND            g_mainHwnd = NULL;
static HDEVNOTIFY      g_devNotify = NULL;
static HDEVNOTIFY      g_bleNotify = NULL;
static UINT            g_taskbarCreatedMsg = 0;
static NOTIFYICONDATAW g_nid;
static HICON           g_trayIcon = NULL;

static DeviceStatus    g_status;
static TraySettings    g_settings;
static char            g_lastIconKey[64] = {0};
static bool            g_lowAlertSent = false;

/* ---- Registry & Theme helpers ---- */
void TraySettings_Load(TraySettings* s) {
    s->style = STYLE_DIGITS_BAR;
    s->low_alert_enabled = true;
    s->low_alert_threshold = 20;

    HKEY hKey = NULL;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, REG_SETTINGS_KEY, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return;

    DWORD val = 0, sz = sizeof(DWORD);
    if (RegQueryValueExW(hKey, L"IconStyle", NULL, NULL, (BYTE*)&val, &sz) == ERROR_SUCCESS && val <= 2)
        s->style = (TrayIconStyle)val;
    sz = sizeof(DWORD);
    if (RegQueryValueExW(hKey, L"LowAlertEnabled", NULL, NULL, (BYTE*)&val, &sz) == ERROR_SUCCESS)
        s->low_alert_enabled = (val != 0);
    sz = sizeof(DWORD);
    if (RegQueryValueExW(hKey, L"LowAlertThreshold", NULL, NULL, (BYTE*)&val, &sz) == ERROR_SUCCESS && val >= 5 && val <= 50)
        s->low_alert_threshold = (int)val;

    RegCloseKey(hKey);
}

void TraySettings_Save(const TraySettings* s) {
    HKEY hKey = NULL;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, REG_SETTINGS_KEY, 0, NULL, 0, KEY_WRITE, NULL, &hKey, NULL) != ERROR_SUCCESS)
        return;

    DWORD st = (DWORD)s->style;
    DWORD al = s->low_alert_enabled ? 1u : 0u;
    DWORD th = (DWORD)s->low_alert_threshold;
    RegSetValueExW(hKey, L"IconStyle", 0, REG_DWORD, (const BYTE*)&st, sizeof(DWORD));
    RegSetValueExW(hKey, L"LowAlertEnabled", 0, REG_DWORD, (const BYTE*)&al, sizeof(DWORD));
    RegSetValueExW(hKey, L"LowAlertThreshold", 0, REG_DWORD, (const BYTE*)&th, sizeof(DWORD));
    RegCloseKey(hKey);
}

bool Startup_IsEnabled(void) {
    HKEY hKey = NULL;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, REG_RUN_KEY, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return false;
    wchar_t buf[520] = {0};
    DWORD sz = sizeof(buf);
    LONG rc = RegQueryValueExW(hKey, REG_RUN_VALUE, NULL, NULL, (BYTE*)buf, &sz);
    RegCloseKey(hKey);
    return rc == ERROR_SUCCESS && buf[0] != L'\0';
}

void Startup_SetEnabled(bool enable) {
    HKEY hKey = NULL;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, REG_RUN_KEY, 0, NULL, 0, KEY_WRITE, NULL, &hKey, NULL) != ERROR_SUCCESS)
        return;
    if (enable) {
        wchar_t exe[512] = {0};
        wchar_t quoted[520] = {0};
        GetModuleFileNameW(NULL, exe, 511);
        swprintf(quoted, 520, L"\"%ls\"", exe);
        DWORD bytes = (DWORD)((wcslen(quoted) + 1) * sizeof(wchar_t));
        RegSetValueExW(hKey, REG_RUN_VALUE, 0, REG_SZ, (const BYTE*)quoted, bytes);
    } else {
        RegDeleteValueW(hKey, REG_RUN_VALUE);
    }
    RegCloseKey(hKey);
}

bool Theme_IsLightTaskbar(void) {
    HKEY hKey = NULL;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, REG_THEME_KEY, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return false;
    DWORD val = 0, sz = sizeof(DWORD);
    LONG rc = RegQueryValueExW(hKey, L"SystemUsesLightTheme", NULL, NULL, (BYTE*)&val, &sz);
    RegCloseKey(hKey);
    return (rc == ERROR_SUCCESS && val == 1);
}

static void FormatTooltip(const DeviceStatus* st, wchar_t* out, size_t maxChars) {
    if (st->mode == CONN_DISCONNECTED) {
        wcsncpy(out, L"Мышь: Отключена", maxChars - 1);
    } else {
        const wchar_t* name = st->product_name[0] ? st->product_name : L"Mouse";
        const wchar_t* ml = (st->mode == CONN_WIRED) ? L"USB"
                          : (st->mode == CONN_BLUETOOTH) ? L"BT" : L"2.4G";
        if (!st->has_reading) {
            swprintf(out, maxChars, L"%.32ls (%ls): Ожидание данных", name, ml);
        } else if (st->is_charging && !st->is_full) {
            swprintf(out, maxChars, L"%.32ls (%ls): Зарядка (%d%%)", name, ml, st->battery_pct);
        } else if (st->is_full) {
            swprintf(out, maxChars, L"%.32ls (%ls): 100%% (Заряжена)", name, ml);
        } else if (!st->is_awake && st->mode == CONN_WIRELESS_24G) {
            swprintf(out, maxChars, L"%.32ls (%ls): %d%% (Сон)", name, ml, st->battery_pct);
        } else {
            swprintf(out, maxChars, L"%.32ls (%ls): %d%%", name, ml, st->battery_pct);
        }
    }
    out[maxChars - 1] = L'\0';
}

static void UpdateTrayAndFlyout(void) {
    bool light = Theme_IsLightTaskbar();
    char key[64];
    snprintf(key, sizeof(key), "%d|%d|%d|%d|%d|%d|%d|%d",
             (int)g_status.mode, g_status.has_reading ? 1 : 0, g_status.battery_pct,
             g_status.is_charging ? 1 : 0, g_status.is_full ? 1 : 0,
             (int)g_settings.style, light ? 1 : 0, IconRenderer_GetNativeSize());

    bool iconChanged = strcmp(key, g_lastIconKey) != 0;
    if (iconChanged) {
        strncpy(g_lastIconKey, key, sizeof(g_lastIconKey) - 1);
        HICON nextIcon = IconRenderer_CreateTrayIcon(&g_status, g_settings.style, light);
        HICON prevIcon = g_trayIcon;
        g_trayIcon = nextIcon;
        g_nid.hIcon = nextIcon;
        if (prevIcon) DestroyIcon(prevIcon);
    }

    FormatTooltip(&g_status, g_nid.szTip, ARRAYSIZE(g_nid.szTip));
    g_nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);

    Flyout_Update(&g_status);

    /* Low battery one-shot warning */
    if (g_status.mode == CONN_DISCONNECTED) {
        g_lowAlertSent = false;
    } else if (g_status.has_reading && !g_status.is_charging && !g_status.is_full) {
        if (g_status.battery_pct > g_settings.low_alert_threshold + 5) {
            g_lowAlertSent = false;
        } else if (g_settings.low_alert_enabled && !g_lowAlertSent &&
                   g_status.battery_pct <= g_settings.low_alert_threshold) {
            g_lowAlertSent = true;
            g_nid.uFlags = NIF_INFO;
            const wchar_t* title = g_status.product_name[0] ? g_status.product_name : L"Mouse Battery";
            wcsncpy(g_nid.szInfoTitle, title, ARRAYSIZE(g_nid.szInfoTitle) - 1);
            g_nid.szInfoTitle[ARRAYSIZE(g_nid.szInfoTitle) - 1] = L'\0';
            swprintf(g_nid.szInfo, ARRAYSIZE(g_nid.szInfo), L"Низкий заряд мыши: %d%%", g_status.battery_pct);
            g_nid.dwInfoFlags = NIIF_WARNING;
            Shell_NotifyIconW(NIM_MODIFY, &g_nid);
        }
    }
}

static void SyncWatchdogTimer(void) {
    if (g_status.backend_id == BACKEND_X11_PASSIVE &&
        g_status.mode == CONN_WIRELESS_24G &&
        g_status.is_awake) {
        SetTimer(g_mainHwnd, TIMER_SLEEP_WATCHDOG, SLEEP_WATCHDOG_MS, NULL);
    } else if (g_status.backend_id != BACKEND_X11_PASSIVE || g_status.mode != CONN_WIRELESS_24G) {
        KillTimer(g_mainHwnd, TIMER_SLEEP_WATCHDOG);
    }
}

static void RescanMouseNow(void) {
    if (g_status.mode == CONN_DISCONNECTED) {
        MouseManager_Start(g_mainHwnd, &g_status);
    } else {
        MouseManager_OnDeviceChange(g_mainHwnd, &g_status);
        MouseManager_RefreshNow();
    }
    SyncWatchdogTimer();
    UpdateTrayAndFlyout();
}

static void OpenTaskbarSettings(void) {
    ShellExecuteW(NULL, L"open", L"ms-settings:taskbar", NULL, NULL, SW_SHOWNORMAL);
}

static void OpenControlCenter(void) {
    wchar_t base[512] = {0};
    GetModuleFileNameW(NULL, base, 511);
    wchar_t* slash = wcsrchr(base, L'\\');
    if (slash) *slash = L'\0';

    wchar_t candidate[600];
    swprintf(candidate, 600, L"%ls\\X11Mouse.exe", base);
    if (GetFileAttributesW(candidate) != INVALID_FILE_ATTRIBUTES) {
        ShellExecuteW(NULL, L"open", candidate, NULL, NULL, SW_SHOWNORMAL);
        return;
    }

    /* Walk up to repo root if launched from bin\ or src\ */
    wchar_t probeDir[512];
    wcscpy(probeDir, base);
    for (int i = 0; i < 5; i++) {
        swprintf(candidate, 600, L"%ls\\src\\X11Mouse.App\\bin\\Release\\net8.0-windows\\X11Mouse.exe", probeDir);
        if (GetFileAttributesW(candidate) != INVALID_FILE_ATTRIBUTES) {
            ShellExecuteW(NULL, L"open", candidate, NULL, NULL, SW_SHOWNORMAL);
            return;
        }
        slash = wcsrchr(probeDir, L'\\');
        if (!slash) break;
        *slash = L'\0';
    }

    MessageBoxW(NULL, L"Не найден X11Mouse.exe рядом с утилитой или в src\\X11Mouse.App\\bin\\Release.", L"Attack Shark X11", MB_OK | MB_ICONINFORMATION);
}

static void RequestExit(void) {
    PostMessageW(g_mainHwnd, WM_CLOSE, 0, 0);
}

static void ShowContextMenu(void) {
    HMENU root = CreatePopupMenu();
    HMENU styleMenu = CreatePopupMenu();

    wchar_t hdr[128];
    FormatTooltip(&g_status, hdr, ARRAYSIZE(hdr));
    AppendMenuW(root, MF_STRING | MF_GRAYED, 0, hdr);
    AppendMenuW(root, MF_SEPARATOR, 0, NULL);

    AppendMenuW(styleMenu, MF_STRING | (g_settings.style == STYLE_DIGITS_BAR ? MF_CHECKED : 0), IDM_STYLE_DIGITS_BAR, L"Цифры и полоса");
    AppendMenuW(styleMenu, MF_STRING | (g_settings.style == STYLE_LARGE_DIGITS ? MF_CHECKED : 0), IDM_STYLE_LARGE, L"Крупные цифры");
    AppendMenuW(styleMenu, MF_STRING | (g_settings.style == STYLE_BATTERY_GAUGE ? MF_CHECKED : 0), IDM_STYLE_GAUGE, L"Иконка батареи");
    AppendMenuW(root, MF_POPUP, (UINT_PTR)styleMenu, L"Стиль иконки");

    AppendMenuW(root, MF_STRING | (g_settings.low_alert_enabled ? MF_CHECKED : 0), IDM_LOW_ALERT, L"Уведомлять при <= 20%");
    AppendMenuW(root, MF_STRING | (Startup_IsEnabled() ? MF_CHECKED : 0), IDM_AUTOSTART, L"Автозапуск с Windows");
    AppendMenuW(root, MF_STRING, IDM_PIN_TASKBAR, L"Закрепить на панели задач...");
    AppendMenuW(root, MF_SEPARATOR, 0, NULL);
    AppendMenuW(root, MF_STRING, IDM_RESCAN, L"Обновить / Найти мышь");
    if (g_status.backend_id == BACKEND_X11_PASSIVE || g_status.mode == CONN_DISCONNECTED)
        AppendMenuW(root, MF_STRING, IDM_OPEN_CENTER, L"Открыть панель X11");
    AppendMenuW(root, MF_SEPARATOR, 0, NULL);
    AppendMenuW(root, MF_STRING, IDM_EXIT, L"Выход");

    POINT cur = { 0, 0 };
    GetCursorPos(&cur);
    SetForegroundWindow(g_mainHwnd);
    UINT cmd = (UINT)TrackPopupMenuEx(root, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, cur.x, cur.y, g_mainHwnd, NULL);
    PostMessageW(g_mainHwnd, WM_NULL, 0, 0);

    DestroyMenu(styleMenu);
    DestroyMenu(root);

    switch (cmd) {
        case IDM_STYLE_DIGITS_BAR:
        case IDM_STYLE_LARGE:
        case IDM_STYLE_GAUGE:
            g_settings.style = (cmd == IDM_STYLE_LARGE) ? STYLE_LARGE_DIGITS
                             : (cmd == IDM_STYLE_GAUGE) ? STYLE_BATTERY_GAUGE
                             : STYLE_DIGITS_BAR;
            TraySettings_Save(&g_settings);
            UpdateTrayAndFlyout();
            break;
        case IDM_LOW_ALERT:
            g_settings.low_alert_enabled = !g_settings.low_alert_enabled;
            TraySettings_Save(&g_settings);
            break;
        case IDM_AUTOSTART:
            Startup_SetEnabled(!Startup_IsEnabled());
            break;
        case IDM_PIN_TASKBAR:
            OpenTaskbarSettings();
            break;
        case IDM_RESCAN:
            RescanMouseNow();
            break;
        case IDM_OPEN_CENTER:
            OpenControlCenter();
            break;
        case IDM_EXIT:
            RequestExit();
            break;
        default:
            break;
    }

    SetProcessWorkingSetSize(GetCurrentProcess(), (SIZE_T)-1, (SIZE_T)-1);
}

static LRESULT CALLBACK MainWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == g_taskbarCreatedMsg && g_taskbarCreatedMsg != 0) {
        Shell_NotifyIconW(NIM_ADD, &g_nid);
        return 0;
    }

    switch (msg) {
        case WM_APP_TRAYICON: {
            UINT ev = LOWORD(lp);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK) {
                if (Flyout_IsVisible()) {
                    Flyout_Hide();
                } else if (GetTickCount64() - Flyout_LastDeactivateTick64() >= 180ULL) {
                    Flyout_ShowNearTray(&g_status);
                }
            } else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) {
                if (Flyout_IsVisible()) Flyout_Hide();
                ShowContextMenu();
            }
            return 0;
        }

        case WM_APP_STATUS_UPDATE: {
            MouseManager_GetStatus(&g_status);
            SyncWatchdogTimer();
            UpdateTrayAndFlyout();
            return 0;
        }

        case WM_APP_HID_DISCONNECTED:
            MouseManager_Stop();
            KillTimer(hwnd, TIMER_SLEEP_WATCHDOG);
            ZeroMemory(&g_status, sizeof(g_status));
            g_status.active_dpi_stage = -1;
            g_status.polling_rate_idx = -1;
            wcscpy(g_status.product_name, L"Disconnected");
            UpdateTrayAndFlyout();
            SetTimer(hwnd, TIMER_RESCAN_DEBOUNCE, 600, NULL);
            return 0;

        case WM_DEVICECHANGE:
            if (wp == DBT_DEVICEARRIVAL || wp == DBT_DEVICEREMOVECOMPLETE || wp == DBT_DEVNODES_CHANGED) {
                SetTimer(hwnd, TIMER_RESCAN_DEBOUNCE, 400, NULL);
            }
            break;

        case WM_SETTINGCHANGE:
        case WM_DPICHANGED:
            UpdateTrayAndFlyout();
            return 0;

        case WM_TIMER:
            if (wp == TIMER_SLEEP_WATCHDOG) {
                KillTimer(hwnd, TIMER_SLEEP_WATCHDOG);
                if (g_status.backend_id == BACKEND_X11_PASSIVE &&
                    g_status.mode == CONN_WIRELESS_24G &&
                    g_status.is_awake) {
                    g_status.is_awake = false;
                    UpdateTrayAndFlyout();
                }
            } else if (wp == TIMER_UI_REFRESH) {
                UpdateTrayAndFlyout();
            } else if (wp == TIMER_RESCAN_DEBOUNCE) {
                KillTimer(hwnd, TIMER_RESCAN_DEBOUNCE);
                MouseManager_OnDeviceChange(hwnd, &g_status);
                SyncWatchdogTimer();
                UpdateTrayAndFlyout();
            }
            return 0;

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            KillTimer(hwnd, TIMER_SLEEP_WATCHDOG);
            KillTimer(hwnd, TIMER_UI_REFRESH);
            KillTimer(hwnd, TIMER_RESCAN_DEBOUNCE);
            if (g_devNotify) {
                UnregisterDeviceNotification(g_devNotify);
                g_devNotify = NULL;
            }
            if (g_bleNotify) {
                UnregisterDeviceNotification(g_bleNotify);
                g_bleNotify = NULL;
            }
            MouseManager_Stop();
            Shell_NotifyIconW(NIM_DELETE, &g_nid);
            if (g_trayIcon) {
                DestroyIcon(g_trayIcon);
                g_trayIcon = NULL;
            }
            Flyout_Destroy();
            PostQuitMessage(0);
            return 0;
    }

    return DefWindowProcW(hwnd, msg, wp, lp);
}

static void EnablePerMonitorDpi(void) {
    HMODULE u32 = GetModuleHandleW(L"user32.dll");
    if (!u32) return;
    typedef BOOL (WINAPI *SetCtxFn)(HANDLE);
    SetCtxFn fn = (SetCtxFn)(void*)GetProcAddress(u32, "SetProcessDpiAwarenessContext");
    if (fn) {
        fn((HANDLE)(INT_PTR)-4); /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */
    }
}

static void AttachConsoleIfNeeded(void) {
    if (GetStdHandle(STD_OUTPUT_HANDLE) == NULL || GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_UNKNOWN) {
        AttachConsole(ATTACH_PARENT_PROCESS);
        FILE* dummy = NULL;
        freopen_s(&dummy, "CONOUT$", "w", stdout);
    }
}

static int RunStatusProbe(void) {
    AttachConsoleIfNeeded();

    DeviceStatus st;
    bool found = MouseManager_ProbeOnce(2200, &st);
    const wchar_t* modeName = !found ? L"Disconnected"
                            : (st.mode == CONN_WIRED) ? L"Wired"
                            : (st.mode == CONN_BLUETOOTH) ? L"Bluetooth"
                            : L"Wireless24G";
    wprintf(L"Mouse Battery Status: mode=%ls backend=\"%ls\" vid=%04X pid=%04X product=\"%ls\"\n",
            modeName, st.backend_label[0] ? st.backend_label : L"-", st.vid, st.pid, st.product_name);
    if (found) {
        if (st.has_reading) {
            if (st.voltage_mv > 0) {
                wprintf(L"  Battery=%d%% charging=%d full=%d voltage=%dmV\n",
                        st.battery_pct, st.is_charging ? 1 : 0, st.is_full ? 1 : 0, st.voltage_mv);
            } else {
                wprintf(L"  Battery=%d%% charging=%d full=%d\n",
                        st.battery_pct, st.is_charging ? 1 : 0, st.is_full ? 1 : 0);
            }
        } else {
            wprintf(L"  Connected, waiting for battery report.\n");
        }
    }
    fflush(stdout);
    return 0;
}

static int RunScanCli(void) {
    AttachConsoleIfNeeded();
    MouseManager_ScanDevices();
    fflush(stdout);
    return 0;
}

static int RunSelfTestCli(void) {
    AttachConsoleIfNeeded();
    int rc = MouseManager_RunSelfTests();
    fflush(stdout);
    return rc;
}

static int RunSmokeTest(HINSTANCE hInst) {
    DeviceStatus samples[8];
    ZeroMemory(samples, sizeof(samples));
    samples[0].mode = CONN_DISCONNECTED;
    samples[1].mode = CONN_WIRELESS_24G; samples[1].is_awake = true;
    samples[2].mode = CONN_WIRELESS_24G; samples[2].is_awake = true;  samples[2].has_reading = true; samples[2].battery_pct = 100;
    samples[3].mode = CONN_WIRELESS_24G; samples[3].is_awake = true;  samples[3].has_reading = true; samples[3].battery_pct = 65;
    samples[4].mode = CONN_WIRELESS_24G; samples[4].is_awake = false; samples[4].has_reading = true; samples[4].battery_pct = 30;
    samples[5].mode = CONN_WIRELESS_24G; samples[5].is_awake = true;  samples[5].has_reading = true; samples[5].battery_pct = 12;
    samples[6].mode = CONN_WIRED;        samples[6].is_awake = true;  samples[6].has_reading = true; samples[6].is_charging = true; samples[6].battery_pct = 55;
    samples[7].mode = CONN_BLUETOOTH;    samples[7].is_awake = true;  samples[7].has_reading = true; samples[7].battery_pct = 80;
    wcscpy(samples[7].product_name, L"BLE Mouse");
    wcscpy(samples[7].backend_label, L"BLE GATT");

    for (int st = 0; st < 3; st++) {
        for (int l = 0; l < 2; l++) {
            for (int i = 0; i < 8; i++) {
                HICON ic = IconRenderer_CreateTrayIcon(&samples[i], (TrayIconStyle)st, l != 0);
                if (!ic) return 1;
                DestroyIcon(ic);
            }
        }
    }

    if (!Flyout_Init(hInst, NULL, NULL, NULL, NULL))
        return 2;
    Flyout_ShowNearTray(&samples[2]);
    Flyout_Update(&samples[7]);
    MSG msg;
    while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    Flyout_Hide();
    Flyout_Destroy();
    return 0;
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPWSTR lpCmdLine, int nCmdShow) {
    (void)hPrevInstance;
    (void)lpCmdLine;
    (void)nCmdShow;

    g_hInst = hInstance;
    EnablePerMonitorDpi();

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    for (int i = 1; i < argc; i++) {
        if (_wcsicmp(argv[i], L"--status") == 0) {
            LocalFree(argv);
            return RunStatusProbe();
        }
        if (_wcsicmp(argv[i], L"--scan") == 0) {
            LocalFree(argv);
            return RunScanCli();
        }
        if (_wcsicmp(argv[i], L"--self-test") == 0) {
            LocalFree(argv);
            return RunSelfTestCli();
        }
        if (_wcsicmp(argv[i], L"--smoke-test") == 0) {
            LocalFree(argv);
            return RunSmokeTest(hInstance);
        }
        if (_wcsicmp(argv[i], L"--preview") == 0 && i + 1 < argc) {
            bool ok = IconRenderer_WritePreviewBmp(argv[i + 1]);
            LocalFree(argv);
            return ok ? 0 : 1;
        }
    }
    if (argv) LocalFree(argv);

    HANDLE hMutex = CreateMutexW(NULL, TRUE, L"Local\\AttackSharkX11BatteryTray_SingleInstance");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    TraySettings_Load(&g_settings);
    ZeroMemory(&g_status, sizeof(g_status));
    g_status.active_dpi_stage = -1;
    g_status.polling_rate_idx = -1;
    wcscpy(g_status.product_name, L"Disconnected");

    WNDCLASSEXW wc;
    ZeroMemory(&wc, sizeof(wc));
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = MainWndProc;
    wc.hInstance     = hInstance;
    wc.lpszClassName = MAIN_WND_CLASS;
    RegisterClassExW(&wc);

    g_taskbarCreatedMsg = RegisterWindowMessageW(L"TaskbarCreated");
    g_mainHwnd = CreateWindowExW(WS_EX_TOOLWINDOW, MAIN_WND_CLASS, L"X11BatteryTray", WS_POPUP, 0, 0, 0, 0, NULL, NULL, hInstance, NULL);
    if (!g_mainHwnd) {
        if (hMutex) CloseHandle(hMutex);
        return 1;
    }

    DEV_BROADCAST_DEVICEINTERFACE_W dbf;
    ZeroMemory(&dbf, sizeof(dbf));
    dbf.dbcc_size       = sizeof(dbf);
    dbf.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE;
    dbf.dbcc_classguid  = X11_GUID_DEVINTERFACE_HID;
    g_devNotify = RegisterDeviceNotificationW(g_mainHwnd, &dbf, DEVICE_NOTIFY_WINDOW_HANDLE);
    dbf.dbcc_classguid  = X11_GUID_DEVINTERFACE_BLE;
    g_bleNotify = RegisterDeviceNotificationW(g_mainHwnd, &dbf, DEVICE_NOTIFY_WINDOW_HANDLE);

    Flyout_Init(hInstance, RescanMouseNow, OpenControlCenter, OpenTaskbarSettings, RequestExit);

    ZeroMemory(&g_nid, sizeof(g_nid));
    g_nid.cbSize           = sizeof(g_nid);
    g_nid.hWnd             = g_mainHwnd;
    g_nid.uID              = 1;
    g_nid.uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    g_nid.uCallbackMessage = WM_APP_TRAYICON;
    g_trayIcon             = IconRenderer_CreateTrayIcon(&g_status, g_settings.style, Theme_IsLightTaskbar());
    g_nid.hIcon            = g_trayIcon;
    FormatTooltip(&g_status, g_nid.szTip, ARRAYSIZE(g_nid.szTip));
    Shell_NotifyIconW(NIM_ADD, &g_nid);

    RescanMouseNow();
    SetTimer(g_mainHwnd, TIMER_UI_REFRESH, UI_REFRESH_MS, NULL);
    SetProcessWorkingSetSize(GetCurrentProcess(), (SIZE_T)-1, (SIZE_T)-1);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (hMutex) CloseHandle(hMutex);
    return 0;
}
