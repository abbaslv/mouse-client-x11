#include "tray_common.h"
#include <stdio.h>
#include <string.h>

#define FLYOUT_CLASS L"AttackSharkX11FlyoutWnd"

#define IDC_FLY_BTN_RESCAN   101
#define IDC_FLY_BTN_CENTER   102
#define IDC_FLY_BTN_EXIT     103
#define IDC_FLY_LINK_PIN     104

typedef struct FlyBtnRect {
    int id;
    RECT rc;
    const wchar_t* label;
    bool danger;
} FlyBtnRect;

static HWND         g_flyWnd = NULL;
static DeviceStatus g_status;
static ULONGLONG    g_lastDeactivateTick = 0;
static int          g_hoverId = 0;
static bool         g_trackingMouse = false;

static FlyoutActionFn g_onRescan = NULL;
static FlyoutActionFn g_onOpenCenter = NULL;
static FlyoutActionFn g_onPinTaskbar = NULL;
static FlyoutActionFn g_onExit = NULL;

static FlyBtnRect g_buttons[4];

static int ScaleDpi(int px, UINT dpi) {
    return MulDiv(px, (int)dpi, 96);
}

static UINT GetFlyoutDpi(HWND hwnd) {
    HMODULE u32 = GetModuleHandleW(L"user32.dll");
    if (u32 && hwnd) {
        typedef UINT (WINAPI *GetDpiForWindowFn)(HWND);
        GetDpiForWindowFn p = (GetDpiForWindowFn)(void*)GetProcAddress(u32, "GetDpiForWindow");
        if (p) {
            UINT d = p(hwnd);
            if (d >= 96) return d;
        }
    }
    HDC dc = GetDC(NULL);
    UINT dpi = (UINT)GetDeviceCaps(dc, LOGPIXELSX);
    ReleaseDC(NULL, dc);
    return dpi >= 96 ? dpi : 96;
}

static void ComputeLayout(UINT dpi) {
    bool showCenterBtn = (g_status.backend_id == BACKEND_X11_PASSIVE || g_status.mode == CONN_DISCONNECTED);

    g_buttons[0].id = IDC_FLY_LINK_PIN;
    g_buttons[0].label = L"Закрепить на панели задач Windows ->";
    g_buttons[0].danger = false;
    SetRect(&g_buttons[0].rc, ScaleDpi(14, dpi), ScaleDpi(134, dpi), ScaleDpi(272, dpi), ScaleDpi(154, dpi));

    g_buttons[1].id = IDC_FLY_BTN_RESCAN;
    g_buttons[1].label = L"Обновить";
    g_buttons[1].danger = false;

    g_buttons[2].id = IDC_FLY_BTN_CENTER;
    g_buttons[2].label = L"Панель X11";
    g_buttons[2].danger = false;

    g_buttons[3].id = IDC_FLY_BTN_EXIT;
    g_buttons[3].label = L"Выход";
    g_buttons[3].danger = true;

    if (showCenterBtn) {
        SetRect(&g_buttons[1].rc, ScaleDpi(14, dpi),  ScaleDpi(162, dpi), ScaleDpi(92, dpi),  ScaleDpi(190, dpi));
        SetRect(&g_buttons[2].rc, ScaleDpi(98, dpi),  ScaleDpi(162, dpi), ScaleDpi(200, dpi), ScaleDpi(190, dpi));
        SetRect(&g_buttons[3].rc, ScaleDpi(206, dpi), ScaleDpi(162, dpi), ScaleDpi(272, dpi), ScaleDpi(190, dpi));
    } else {
        SetRect(&g_buttons[1].rc, ScaleDpi(14, dpi),  ScaleDpi(162, dpi), ScaleDpi(166, dpi), ScaleDpi(190, dpi));
        SetRect(&g_buttons[2].rc, 0, 0, 0, 0);
        SetRect(&g_buttons[3].rc, ScaleDpi(172, dpi), ScaleDpi(162, dpi), ScaleDpi(272, dpi), ScaleDpi(190, dpi));
    }
}

static int HitTestButtons(POINT pt) {
    for (int i = 0; i < 4; i++) {
        if (!IsRectEmpty(&g_buttons[i].rc) && PtInRect(&g_buttons[i].rc, pt))
            return g_buttons[i].id;
    }
    return 0;
}

static HFONT MakeUiFont(UINT dpi, int ptSize, int weight, bool underline) {
    int height = -MulDiv(ptSize, (int)dpi, 72);
    return CreateFontW(height, 0, 0, 0, weight, FALSE, underline ? TRUE : FALSE, FALSE,
                       DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                       CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
}

static void DrawFlyout(HWND hwnd, HDC targetDc, int clientW, int clientH) {
    UINT dpi = GetFlyoutDpi(hwnd);
    ComputeLayout(dpi);

    HDC memDc = CreateCompatibleDC(targetDc);
    HBITMAP memBmp = CreateCompatibleBitmap(targetDc, clientW, clientH);
    HBITMAP oldBmp = (HBITMAP)SelectObject(memDc, memBmp);

    /* Background + top crimson stripe */
    HBRUSH bgBrush = CreateSolidBrush(RGB(22, 22, 22));
    RECT fullRc = { 0, 0, clientW, clientH };
    FillRect(memDc, &fullRc, bgBrush);
    DeleteObject(bgBrush);

    HBRUSH accentBrush = CreateSolidBrush(RGB(226, 33, 28));
    RECT accentRc = { 1, 1, clientW - 1, ScaleDpi(4, dpi) };
    FillRect(memDc, &accentRc, accentBrush);
    DeleteObject(accentBrush);

    HPEN borderPen = CreatePen(PS_SOLID, 1, RGB(58, 58, 58));
    HPEN oldPen = (HPEN)SelectObject(memDc, borderPen);
    HBRUSH nullBrush = (HBRUSH)GetStockObject(NULL_BRUSH);
    HBRUSH oldBrush = (HBRUSH)SelectObject(memDc, nullBrush);
    Rectangle(memDc, 0, 0, clientW, clientH);
    SelectObject(memDc, oldBrush);
    SelectObject(memDc, oldPen);
    DeleteObject(borderPen);

    SetBkMode(memDc, TRANSPARENT);

    HFONT fTitle = MakeUiFont(dpi, 10, FW_BOLD, false);
    HFONT fBadge = MakeUiFont(dpi, 8,  FW_BOLD, false);
    HFONT fPct   = MakeUiFont(dpi, 26, FW_BOLD, false);
    HFONT fBody  = MakeUiFont(dpi, 9,  FW_NORMAL, false);
    HFONT fSmall = MakeUiFont(dpi, 8,  FW_NORMAL, false);
    HFONT fLink  = MakeUiFont(dpi, 8,  FW_NORMAL, g_hoverId == IDC_FLY_LINK_PIN);

    /* Title */
    SelectObject(memDc, fTitle);
    SetTextColor(memDc, RGB(255, 255, 255));
    RECT titleRc = { ScaleDpi(14, dpi), ScaleDpi(14, dpi), ScaleDpi(178, dpi), ScaleDpi(34, dpi) };
    wchar_t titleBuf[64] = L"MOUSE BATTERY";
    if (g_status.mode != CONN_DISCONNECTED && g_status.product_name[0]) {
        wcsncpy(titleBuf, g_status.product_name, 63);
        titleBuf[63] = L'\0';
        CharUpperW(titleBuf);
    }
    DrawTextW(memDc, titleBuf, -1, &titleRc, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

    /* Mode pill */
    RECT badgeRc = { ScaleDpi(182, dpi), ScaleDpi(13, dpi), ScaleDpi(272, dpi), ScaleDpi(35, dpi) };
    HBRUSH badgeBg = CreateSolidBrush(RGB(36, 36, 36));
    HPEN badgePen = CreatePen(PS_SOLID, 1, RGB(56, 56, 56));
    SelectObject(memDc, badgeBg);
    SelectObject(memDc, badgePen);
    RoundRect(memDc, badgeRc.left, badgeRc.top, badgeRc.right, badgeRc.bottom, ScaleDpi(6, dpi), ScaleDpi(6, dpi));
    DeleteObject(badgeBg);
    DeleteObject(badgePen);

    const wchar_t* modeStr = L"Отключена";
    COLORREF modeClr = RGB(165, 165, 165);
    if (g_status.mode == CONN_WIRED) {
        modeStr = L"USB Wired";
        modeClr = RGB(82, 224, 120);
    } else if (g_status.mode == CONN_WIRELESS_24G) {
        modeStr = L"2.4 GHz";
        modeClr = g_status.is_awake ? RGB(82, 224, 120) : RGB(255, 198, 70);
    } else if (g_status.mode == CONN_BLUETOOTH) {
        modeStr = L"Bluetooth";
        modeClr = RGB(86, 189, 248);
    }
    SelectObject(memDc, fBadge);
    SetTextColor(memDc, modeClr);
    DrawTextW(memDc, modeStr, -1, &badgeRc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

    /* Large percent + status text */
    wchar_t pctBuf[16] = L"--%";
    wchar_t statBuf[96] = L"Мышь не обнаружена";
    wchar_t teleBuf[128] = L"Подключите мышь по USB, 2.4G или Bluetooth";

    if (g_status.mode != CONN_DISCONNECTED) {
        const wchar_t* bLabel = g_status.backend_label[0] ? g_status.backend_label : L"HID";
        if (!g_status.has_reading) {
            wcscpy(pctBuf, L"?%");
            wcscpy(statBuf, L"Ожидание сигнала...");
            swprintf(teleBuf, 128, L"%ls · пошевелите мышью", bLabel);
        } else {
            swprintf(pctBuf, 16, L"%d%%", g_status.battery_pct);
            if (g_status.is_charging && !g_status.is_full) {
                wcscpy(statBuf, L"Идёт зарядка");
            } else if (g_status.is_full) {
                wcscpy(pctBuf, L"100%");
                wcscpy(statBuf, L"Полностью заряжена");
            } else {
                ULONGLONG ageSec = (g_status.last_update_tick64 > 0)
                    ? (GetTickCount64() - g_status.last_update_tick64) / 1000ULL : 0ULL;
                const wchar_t* stateHead = (g_status.mode == CONN_WIRELESS_24G && !g_status.is_awake)
                    ? L"Режим сна" : L"Активна";
                if (ageSec < 60ULL) {
                    swprintf(statBuf, 96, L"%ls\nтолько что", stateHead);
                } else {
                    swprintf(statBuf, 96, L"%ls\n%llu мин назад", stateHead, (unsigned long long)(ageSec / 60ULL));
                }
            }

            static const int hzMap[4] = { 125, 250, 500, 1000 };
            wchar_t voltPart[32] = L"";
            wchar_t dpiPart[32] = L"";
            wchar_t ratePart[32] = L"";
            if (g_status.voltage_mv > 0)
                swprintf(voltPart, 32, L" · %d.%02dV", g_status.voltage_mv / 1000, (g_status.voltage_mv % 1000) / 10);
            if (g_status.active_dpi_stage >= 0)
                swprintf(dpiPart, 32, L" · DPI S%d", g_status.active_dpi_stage + 1);
            if (g_status.polling_rate_idx >= 0 && g_status.polling_rate_idx <= 3)
                swprintf(ratePart, 32, L" · %d Hz", hzMap[g_status.polling_rate_idx]);
            swprintf(teleBuf, 128, L"%ls%ls%ls%ls", bLabel, voltPart, dpiPart, ratePart);
        }
    }

    SelectObject(memDc, fPct);
    SetTextColor(memDc, RGB(255, 255, 255));
    RECT pctRc = { ScaleDpi(14, dpi), ScaleDpi(44, dpi), ScaleDpi(136, dpi), ScaleDpi(92, dpi) };
    DrawTextW(memDc, pctBuf, -1, &pctRc, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    SelectObject(memDc, fBody);
    SetTextColor(memDc, RGB(200, 200, 200));
    RECT statRc = { ScaleDpi(138, dpi), ScaleDpi(50, dpi), ScaleDpi(272, dpi), ScaleDpi(90, dpi) };
    DrawTextW(memDc, statBuf, -1, &statRc, DT_LEFT | DT_WORDBREAK);

    /* Progress Bar */
    RECT barRc = { ScaleDpi(14, dpi), ScaleDpi(96, dpi), ScaleDpi(272, dpi), ScaleDpi(106, dpi) };
    HBRUSH trkBrush = CreateSolidBrush(RGB(44, 44, 44));
    HPEN nullPen = (HPEN)GetStockObject(NULL_PEN);
    SelectObject(memDc, trkBrush);
    SelectObject(memDc, nullPen);
    int rad = ScaleDpi(5, dpi);
    RoundRect(memDc, barRc.left, barRc.top, barRc.right, barRc.bottom, rad, rad);
    DeleteObject(trkBrush);

    if (g_status.mode != CONN_DISCONNECTED && g_status.has_reading) {
        int pct = g_status.battery_pct < 0 ? 0 : (g_status.battery_pct > 100 ? 100 : g_status.battery_pct);
        int totW = barRc.right - barRc.left;
        int fillW = (totW * pct) / 100;
        if (fillW < ScaleDpi(10, dpi) && pct > 0) fillW = ScaleDpi(10, dpi);
        COLORREF fillClr = RGB(52, 215, 95);
        if (g_status.is_charging && !g_status.is_full) fillClr = RGB(56, 189, 248);
        else if (pct <= 20) fillClr = RGB(255, 69, 58);
        else if (pct <= 40) fillClr = RGB(255, 196, 20);

        HBRUSH fBrush = CreateSolidBrush(fillClr);
        SelectObject(memDc, fBrush);
        RoundRect(memDc, barRc.left, barRc.top, barRc.left + fillW, barRc.bottom, rad, rad);
        DeleteObject(fBrush);
    }

    /* Telemetry subtitle */
    SelectObject(memDc, fSmall);
    SetTextColor(memDc, RGB(150, 150, 150));
    RECT teleRc = { ScaleDpi(14, dpi), ScaleDpi(112, dpi), ScaleDpi(272, dpi), ScaleDpi(130, dpi) };
    DrawTextW(memDc, teleBuf, -1, &teleRc, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS);

    /* Pin Link */
    SelectObject(memDc, fLink);
    SetTextColor(memDc, g_hoverId == IDC_FLY_LINK_PIN ? RGB(255, 255, 255) : RGB(235, 95, 90));
    DrawTextW(memDc, g_buttons[0].label, -1, &g_buttons[0].rc, DT_LEFT | DT_VCENTER | DT_SINGLELINE);

    /* Bottom Buttons */
    SelectObject(memDc, fSmall);
    for (int i = 1; i < 4; i++) {
        if (IsRectEmpty(&g_buttons[i].rc))
            continue;
        bool hov = (g_hoverId == g_buttons[i].id);
        COLORREF fillC = g_buttons[i].danger
            ? (hov ? RGB(180, 33, 28) : RGB(62, 24, 24))
            : (hov ? RGB(54, 54, 54) : RGB(36, 36, 36));
        COLORREF bordC = g_buttons[i].danger ? RGB(165, 38, 34) : RGB(62, 62, 62);
        HBRUSH bFill = CreateSolidBrush(fillC);
        HPEN   bPen  = CreatePen(PS_SOLID, 1, bordC);
        SelectObject(memDc, bFill);
        SelectObject(memDc, bPen);
        RoundRect(memDc, g_buttons[i].rc.left, g_buttons[i].rc.top, g_buttons[i].rc.right, g_buttons[i].rc.bottom, ScaleDpi(4, dpi), ScaleDpi(4, dpi));
        DeleteObject(bFill);
        DeleteObject(bPen);

        SetTextColor(memDc, RGB(255, 255, 255));
        DrawTextW(memDc, g_buttons[i].label, -1, &g_buttons[i].rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    }

    BitBlt(targetDc, 0, 0, clientW, clientH, memDc, 0, 0, SRCCOPY);

    SelectObject(memDc, oldBmp);
    DeleteObject(memBmp);
    DeleteDC(memDc);
    DeleteObject(fTitle);
    DeleteObject(fBadge);
    DeleteObject(fPct);
    DeleteObject(fBody);
    DeleteObject(fSmall);
    DeleteObject(fLink);
}

static LRESULT CALLBACK FlyoutWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_ERASEBKGND:
            return 1;

        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            RECT cr;
            GetClientRect(hwnd, &cr);
            DrawFlyout(hwnd, dc, cr.right, cr.bottom);
            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_MOUSEMOVE: {
            POINT pt = { (SHORT)LOWORD(lp), (SHORT)HIWORD(lp) };
            int nextHover = HitTestButtons(pt);
            if (!g_trackingMouse) {
                TRACKMOUSEEVENT tme = { sizeof(tme), TME_LEAVE, hwnd, 0 };
                TrackMouseEvent(&tme);
                g_trackingMouse = true;
            }
            if (nextHover != g_hoverId) {
                g_hoverId = nextHover;
                InvalidateRect(hwnd, NULL, FALSE);
            }
            SetCursor(LoadCursorW(NULL, nextHover != 0 ? IDC_HAND : IDC_ARROW));
            return 0;
        }

        case WM_MOUSELEAVE:
            g_trackingMouse = false;
            if (g_hoverId != 0) {
                g_hoverId = 0;
                InvalidateRect(hwnd, NULL, FALSE);
            }
            return 0;

        case WM_LBUTTONUP: {
            POINT pt = { (SHORT)LOWORD(lp), (SHORT)HIWORD(lp) };
            int hit = HitTestButtons(pt);
            if (hit == IDC_FLY_BTN_RESCAN && g_onRescan) g_onRescan();
            else if (hit == IDC_FLY_BTN_CENTER && g_onOpenCenter) { Flyout_Hide(); g_onOpenCenter(); }
            else if (hit == IDC_FLY_LINK_PIN && g_onPinTaskbar) { Flyout_Hide(); g_onPinTaskbar(); }
            else if (hit == IDC_FLY_BTN_EXIT && g_onExit) g_onExit();
            return 0;
        }

        case WM_KEYDOWN:
            if (wp == VK_ESCAPE) {
                Flyout_Hide();
                return 0;
            }
            break;

        case WM_ACTIVATE:
            if (LOWORD(wp) == WA_INACTIVE) {
                g_lastDeactivateTick = GetTickCount64();
                Flyout_Hide();
                return 0;
            }
            break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

bool Flyout_Init(HINSTANCE hInst, FlyoutActionFn onRescan, FlyoutActionFn onOpenCenter, FlyoutActionFn onPinTaskbar, FlyoutActionFn onExit) {
    g_onRescan     = onRescan;
    g_onOpenCenter = onOpenCenter;
    g_onPinTaskbar = onPinTaskbar;
    g_onExit       = onExit;
    ZeroMemory(&g_status, sizeof(g_status));

    WNDCLASSEXW wc;
    ZeroMemory(&wc, sizeof(wc));
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = FlyoutWndProc;
    wc.hInstance     = hInst;
    wc.lpszClassName = FLYOUT_CLASS;
    wc.hCursor       = LoadCursorW(NULL, IDC_ARROW);
    RegisterClassExW(&wc);

    UINT dpi = GetFlyoutDpi(NULL);
    int w = ScaleDpi(286, dpi);
    int h = ScaleDpi(204, dpi);
    ComputeLayout(dpi);

    g_flyWnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
        FLYOUT_CLASS,
        L"Attack Shark X11",
        WS_POPUP,
        0, 0, w, h,
        NULL, NULL, hInst, NULL);
    return g_flyWnd != NULL;
}

void Flyout_Destroy(void) {
    if (g_flyWnd) {
        DestroyWindow(g_flyWnd);
        g_flyWnd = NULL;
    }
}

void Flyout_ShowNearTray(const DeviceStatus* st) {
    if (!g_flyWnd)
        return;

    g_status = *st;
    POINT cur = { 0, 0 };
    GetCursorPos(&cur);
    HMONITOR hMon = MonitorFromPoint(cur, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi;
    ZeroMemory(&mi, sizeof(mi));
    mi.cbSize = sizeof(mi);
    GetMonitorInfoW(hMon, &mi);

    UINT dpi = GetFlyoutDpi(g_flyWnd);
    int w = ScaleDpi(286, dpi);
    int h = ScaleDpi(204, dpi);
    int pad = ScaleDpi(12, dpi);

    int x = mi.rcWork.right - w - pad;
    int y = mi.rcWork.bottom - h - pad;
    if (x < mi.rcWork.left + 4) x = mi.rcWork.left + 4;
    if (y < mi.rcWork.top + 4)  y = mi.rcWork.top + 4;

    SetWindowPos(g_flyWnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW);
    SetForegroundWindow(g_flyWnd);
    InvalidateRect(g_flyWnd, NULL, FALSE);
    UpdateWindow(g_flyWnd);
}

void Flyout_Hide(void) {
    if (g_flyWnd && IsWindowVisible(g_flyWnd)) {
        ShowWindow(g_flyWnd, SW_HIDE);
        SetProcessWorkingSetSize(GetCurrentProcess(), (SIZE_T)-1, (SIZE_T)-1);
    }
}

void Flyout_Update(const DeviceStatus* st) {
    g_status = *st;
    if (g_flyWnd && IsWindowVisible(g_flyWnd)) {
        InvalidateRect(g_flyWnd, NULL, FALSE);
    }
}

bool Flyout_IsVisible(void) {
    return g_flyWnd && IsWindowVisible(g_flyWnd);
}

ULONGLONG Flyout_LastDeactivateTick64(void) {
    return g_lastDeactivateTick;
}
