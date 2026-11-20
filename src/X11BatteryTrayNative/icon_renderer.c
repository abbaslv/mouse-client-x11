#include "tray_common.h"
#include <stdio.h>
#include <string.h>

static uint32_t PremulArgb(uint8_t a, uint8_t r, uint8_t g, uint8_t b) {
    uint32_t pr = ((uint32_t)r * a + 127u) / 255u;
    uint32_t pg = ((uint32_t)g * a + 127u) / 255u;
    uint32_t pb = ((uint32_t)b * a + 127u) / 255u;
    return ((uint32_t)a << 24) | (pr << 16) | (pg << 8) | pb;
}

static void BlendPixel(uint32_t* px, int stride, int h, int x, int y, uint8_t a, uint8_t r, uint8_t g, uint8_t b) {
    if (x < 0 || x >= stride || y < 0 || y >= h || a == 0)
        return;
    if (a == 255) {
        px[y * stride + x] = PremulArgb(255, r, g, b);
        return;
    }
    uint32_t dst = px[y * stride + x];
    uint8_t da = (uint8_t)(dst >> 24);
    uint8_t dr = (uint8_t)(dst >> 16);
    uint8_t dg = (uint8_t)(dst >> 8);
    uint8_t db = (uint8_t)(dst);
    uint32_t inv = 255u - a;
    uint32_t pr = ((uint32_t)r * a + 127u) / 255u + (dr * inv + 127u) / 255u;
    uint32_t pg = ((uint32_t)g * a + 127u) / 255u + (dg * inv + 127u) / 255u;
    uint32_t pb = ((uint32_t)b * a + 127u) / 255u + (db * inv + 127u) / 255u;
    uint32_t oa = a + (da * inv + 127u) / 255u;
    if (oa > 255) oa = 255;
    if (pr > oa) pr = oa;
    if (pg > oa) pg = oa;
    if (pb > oa) pb = oa;
    px[y * stride + x] = (oa << 24) | (pr << 16) | (pg << 8) | pb;
}

static void FillRectRgba(uint32_t* px, int w, int h, int x0, int y0, int rw, int rh, uint8_t a, uint8_t r, uint8_t g, uint8_t b) {
    for (int y = y0; y < y0 + rh; y++) {
        for (int x = x0; x < x0 + rw; x++) {
            BlendPixel(px, w, h, x, y, a, r, g, b);
        }
    }
}

static void FillPillBar(uint32_t* px, int w, int h, int x0, int y0, int bw, int bh, uint8_t a, uint8_t r, uint8_t g, uint8_t b) {
    if (bw <= 0 || bh <= 0)
        return;
    for (int y = y0; y < y0 + bh; y++) {
        for (int x = x0; x < x0 + bw; x++) {
            bool corner = (bw >= 3 && bh >= 3) &&
                          ((x == x0 || x == x0 + bw - 1) && (y == y0 || y == y0 + bh - 1));
            BlendPixel(px, w, h, x, y, corner ? (uint8_t)((a * 110u) / 255u) : a, r, g, b);
        }
    }
}

/*
 * Bold 6x10 glyph patterns (MSB = left pixel of 6px width) for 20x20 icons.
 * Designed specifically for razor-sharp pixel-aligned readability at small taskbar sizes.
 */
static const uint8_t GLYPH_6X10[12][10] = {
    /* '0' */ { 0x1E, 0x3F, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x3F, 0x1E },
    /* '1' */ { 0x0C, 0x1C, 0x3C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x3F, 0x3F },
    /* '2' */ { 0x1E, 0x3F, 0x33, 0x03, 0x06, 0x0C, 0x18, 0x30, 0x3F, 0x3F },
    /* '3' */ { 0x1E, 0x3F, 0x33, 0x03, 0x0E, 0x0E, 0x03, 0x33, 0x3F, 0x1E },
    /* '4' */ { 0x06, 0x0E, 0x1E, 0x36, 0x36, 0x3F, 0x3F, 0x06, 0x06, 0x06 },
    /* '5' */ { 0x3F, 0x3F, 0x30, 0x3E, 0x3F, 0x03, 0x03, 0x33, 0x3F, 0x1E },
    /* '6' */ { 0x1E, 0x3F, 0x30, 0x3E, 0x3F, 0x33, 0x33, 0x33, 0x3F, 0x1E },
    /* '7' */ { 0x3F, 0x3F, 0x03, 0x06, 0x06, 0x0C, 0x0C, 0x18, 0x18, 0x18 },
    /* '8' */ { 0x1E, 0x3F, 0x33, 0x33, 0x1E, 0x1E, 0x33, 0x33, 0x3F, 0x1E },
    /* '9' */ { 0x1E, 0x3F, 0x33, 0x33, 0x3F, 0x1F, 0x03, 0x03, 0x3F, 0x1E },
    /* '?' */ { 0x1E, 0x3F, 0x33, 0x06, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00 },
    /* 'x' */ { 0x33, 0x3F, 0x1E, 0x0C, 0x0C, 0x0C, 0x1E, 0x3F, 0x33, 0x00 },
};

/* Compact 5x10 '1' for "100" at 20px (4px wide '1' + 1px + 6px '0' + 1px + 6px '0' = 18px) */
static const uint8_t GLYPH_1_4X10[10] = {
    0x06, 0x0E, 0x0E, 0x06, 0x06, 0x06, 0x06, 0x06, 0x0F, 0x0F
};

/* Crisp 5x7 glyphs for 16x16 icons (or gauge inner glyphs) */
static const uint8_t GLYPH_5X8[12][8] = {
    /* '0' */ { 0x0E, 0x1F, 0x1B, 0x1B, 0x1B, 0x1B, 0x1F, 0x0E },
    /* '1' */ { 0x04, 0x0C, 0x1C, 0x04, 0x04, 0x04, 0x1F, 0x1F },
    /* '2' */ { 0x0E, 0x1F, 0x03, 0x06, 0x0C, 0x18, 0x1F, 0x1F },
    /* '3' */ { 0x1E, 0x1F, 0x03, 0x0E, 0x0E, 0x03, 0x1F, 0x1E },
    /* '4' */ { 0x06, 0x0E, 0x16, 0x16, 0x1F, 0x1F, 0x06, 0x06 },
    /* '5' */ { 0x1F, 0x1F, 0x18, 0x1E, 0x03, 0x03, 0x1F, 0x1E },
    /* '6' */ { 0x0E, 0x1F, 0x18, 0x1E, 0x1B, 0x1B, 0x1F, 0x0E },
    /* '7' */ { 0x1F, 0x1F, 0x03, 0x06, 0x06, 0x0C, 0x0C, 0x0C },
    /* '8' */ { 0x0E, 0x1F, 0x1B, 0x0E, 0x0E, 0x1B, 0x1F, 0x0E },
    /* '9' */ { 0x0E, 0x1F, 0x1B, 0x1F, 0x03, 0x03, 0x1F, 0x0E },
    /* '?' */ { 0x0E, 0x1B, 0x03, 0x06, 0x0C, 0x00, 0x0C, 0x0C },
    /* 'x' */ { 0x1B, 0x1B, 0x0E, 0x04, 0x0E, 0x1B, 0x1B, 0x00 },
};

static int GlyphIndex(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch == '?') return 10;
    return 11;
}

static void DrawGlyphGrid(
    uint32_t* px, int w, int h,
    const uint8_t* rows, int gw, int gh,
    int dstX, int dstY, int dstW, int dstH,
    uint8_t r, uint8_t g, uint8_t b)
{
    for (int py = 0; py < dstH; py++) {
        int sy = (py * gh) / dstH;
        uint8_t maskRow = rows[sy];
        for (int pxx = 0; pxx < dstW; pxx++) {
            int sx = (pxx * gw) / dstW;
            if (maskRow & (1u << (gw - 1 - sx))) {
                BlendPixel(px, w, h, dstX + pxx, dstY + py, 255, r, g, b);
            }
        }
    }
}

static void DrawBoltPixels(uint32_t* px, int w, int h, int bx, int by, int bw, int bh, uint8_t r, uint8_t g, uint8_t b) {
    static const uint8_t BOLT_8X11[11] = {
        0x18, 0x1C, 0x38, 0x70, 0xFE, 0xFF, 0x7E, 0x1C, 0x38, 0x30, 0x20
    };
    DrawGlyphGrid(px, w, h, BOLT_8X11, 8, 11, bx, by, bw, bh, r, g, b);
}

static void DrawStringCrisp(uint32_t* px, int size, const char* s, int areaY, int areaH, bool bigHeight, uint8_t r, uint8_t g, uint8_t b) {
    int len = (int)strlen(s);
    if (size <= 17) {
        /* 16x16 native */
        int charH = bigHeight ? 12 : 9;
        int dstY = areaY + (areaH - charH) / 2;
        if (strcmp(s, "100") == 0) {
            static const uint8_t ONE_3X8[8] = { 0x2, 0x6, 0x2, 0x2, 0x2, 0x2, 0x7, 0x7 };
            DrawGlyphGrid(px, size, size, ONE_3X8, 3, 8, 1, dstY, 3, charH, r, g, b);
            DrawGlyphGrid(px, size, size, GLYPH_5X8[0], 5, 8, 5, dstY, 5, charH, r, g, b);
            DrawGlyphGrid(px, size, size, GLYPH_5X8[0], 5, 8, 11, dstY, 5, charH, r, g, b);
        } else {
            int cw = 5;
            int gap = len == 2 ? 2 : 0;
            int tot = len * cw + (len - 1) * gap;
            int sx = (size - tot) / 2;
            for (int i = 0; i < len; i++) {
                DrawGlyphGrid(px, size, size, GLYPH_5X8[GlyphIndex(s[i])], 5, 8, sx + i * (cw + gap), dstY, cw, charH, r, g, b);
            }
        }
        return;
    }

    /* >= 20px (e.g. 20x20 at 125% DPI, 24x24 at 150%, 32x32 at 200%) */
    float scale = size / 20.0f;
    int charH = (int)((bigHeight ? 14.0f : 11.0f) * scale + 0.5f);
    if (charH > areaH) charH = areaH;
    int dstY = areaY + (areaH - charH) / 2;

    if (strcmp(s, "100") == 0) {
        int w1 = (int)(4.0f * scale + 0.5f);
        int w0 = (int)(6.0f * scale + 0.5f);
        int gap = (int)(1.0f * scale + 0.5f);
        if (gap < 1) gap = 1;
        int tot = w1 + gap + w0 + gap + w0;
        int sx = (size - tot) / 2;
        DrawGlyphGrid(px, size, size, GLYPH_1_4X10, 4, 10, sx, dstY, w1, charH, r, g, b);
        DrawGlyphGrid(px, size, size, GLYPH_6X10[0], 6, 10, sx + w1 + gap, dstY, w0, charH, r, g, b);
        DrawGlyphGrid(px, size, size, GLYPH_6X10[0], 6, 10, sx + w1 + gap + w0 + gap, dstY, w0, charH, r, g, b);
    } else {
        int cw = (int)((len == 1 ? 8.0f : 7.0f) * scale + 0.5f);
        int gap = len == 2 ? (int)(2.0f * scale + 0.5f) : 0;
        int tot = len * cw + (len - 1) * gap;
        if (tot > size - 2) {
            cw = (size - 2 - (len - 1)) / len;
            gap = 1;
            tot = len * cw + (len - 1);
        }
        int sx = (size - tot) / 2;
        for (int i = 0; i < len; i++) {
            DrawGlyphGrid(px, size, size, GLYPH_6X10[GlyphIndex(s[i])], 6, 10, sx + i * (cw + gap), dstY, cw, charH, r, g, b);
        }
    }
}

static void BatteryAccentRgb(int pct, bool charging, bool full, uint8_t* r, uint8_t* g, uint8_t* b) {
    if (charging && !full) {
        *r = 56; *g = 189; *b = 248;
    } else if (pct <= 20) {
        *r = 255; *g = 69; *b = 58;
    } else if (pct <= 40) {
        *r = 255; *g = 196; *b = 20;
    } else {
        *r = 52; *g = 215; *b = 95;
    }
}

static void RenderPixels(uint32_t* px, int size, const DeviceStatus* st, TrayIconStyle style, bool isLightTaskbar) {
    uint8_t fgR = isLightTaskbar ? 20 : 255;
    uint8_t fgG = fgR;
    uint8_t fgB = fgR;
    uint8_t mutR = isLightTaskbar ? 115 : 178;
    uint8_t trkA = 80;
    uint8_t trkV = isLightTaskbar ? 0 : 255;

    if (style == STYLE_BATTERY_GAUGE) {
        int bw = (int)(size * 0.72f + 0.5f);
        int bh = (int)(size * 0.56f + 0.5f);
        int bx = (int)(size * 0.08f + 0.5f);
        int by = (size - bh) / 2;
        int nubW = (size >= 24) ? 3 : 2;
        int nubH = bh / 2;
        int stroke = (size >= 24) ? 2 : 1;

        uint8_t br = (st->mode == CONN_DISCONNECTED) ? mutR : fgR;
        uint8_t bg = (st->mode == CONN_DISCONNECTED) ? mutR : fgG;
        uint8_t bb = (st->mode == CONN_DISCONNECTED) ? mutR : fgB;

        /* Outer frame */
        FillRectRgba(px, size, size, bx, by, bw, stroke, 255, br, bg, bb);
        FillRectRgba(px, size, size, bx, by + bh - stroke, bw, stroke, 255, br, bg, bb);
        FillRectRgba(px, size, size, bx, by, stroke, bh, 255, br, bg, bb);
        FillRectRgba(px, size, size, bx + bw - stroke, by, stroke, bh, 255, br, bg, bb);
        FillRectRgba(px, size, size, bx + bw, by + (bh - nubH) / 2, nubW, nubH, 255, br, bg, bb);

        int ix = bx + stroke + 1;
        int iy = by + stroke + 1;
        int iw = bw - (stroke + 1) * 2;
        int ih = bh - (stroke + 1) * 2;

        if (st->mode == CONN_DISCONNECTED) {
            DrawStringCrisp(px, size, "x", iy, ih, false, mutR, mutR, mutR);
            return;
        }
        if (!st->has_reading) {
            DrawStringCrisp(px, size, "?", iy, ih, false, fgR, fgG, fgB);
            return;
        }

        int pct = st->battery_pct < 0 ? 0 : (st->battery_pct > 100 ? 100 : st->battery_pct);
        int fw = (iw * pct + 50) / 100;
        if (fw < 2 && pct > 0) fw = 2;
        uint8_t ar, ag, ab;
        BatteryAccentRgb(pct, st->is_charging, st->is_full, &ar, &ag, &ab);
        FillRectRgba(px, size, size, ix, iy, fw, ih, 255, ar, ag, ab);

        if (st->is_charging && !st->is_full) {
            DrawBoltPixels(px, size, size, ix + iw / 4, by - 1, iw / 2, bh + 2, 255, 255, 255);
        }
        return;
    }

    if (style == STYLE_LARGE_DIGITS) {
        if (st->mode == CONN_DISCONNECTED) {
            DrawStringCrisp(px, size, "x", 1, size - 2, true, mutR, mutR, mutR);
            return;
        }
        if (!st->has_reading) {
            DrawStringCrisp(px, size, "?", 1, size - 2, true, fgR, fgG, fgB);
            return;
        }
        if (st->is_charging && !st->is_full) {
            int bw = (size * 5) / 10;
            int bh = (size * 8) / 10;
            DrawBoltPixels(px, size, size, (size - bw) / 2, (size - bh) / 2, bw, bh, 56, 189, 248);
            return;
        }
        int pct = st->battery_pct < 0 ? 0 : (st->battery_pct > 100 ? 100 : st->battery_pct);
        char buf[8];
        snprintf(buf, sizeof(buf), "%d", pct);
        uint8_t dr = fgR, dg = fgG, db = fgB;
        if (pct <= 20) { dr = 255; dg = 75; db = 66; }
        else if (pct <= 40) { dr = 255; dg = 198; db = 30; }
        DrawStringCrisp(px, size, buf, 1, size - 2, true, dr, dg, db);
        return;
    }

    /* STYLE_DIGITS_BAR (default): Crisp white digits on top + vivid rounded bar below */
    int barH = (size >= 24) ? 4 : 3;
    int barY = size - barH - 1;
    int textAreaH = barY - 2;

    uint8_t barR = 135, barG = 140, barB = 150;
    int fillRatio = 0;

    if (st->mode == CONN_DISCONNECTED) {
        DrawStringCrisp(px, size, "x", 1, textAreaH, false, mutR, mutR, mutR);
        fillRatio = 0;
    } else if (!st->has_reading) {
        DrawStringCrisp(px, size, "?", 1, textAreaH, false, fgR, fgG, fgB);
        fillRatio = 100;
    } else if (st->is_charging && !st->is_full) {
        int bw = (size * 44) / 100;
        int bh = textAreaH;
        DrawBoltPixels(px, size, size, (size - bw) / 2, 1, bw, bh, 56, 189, 248);
        barR = 56; barG = 189; barB = 248;
        fillRatio = st->battery_pct < 12 ? 12 : (st->battery_pct > 100 ? 100 : st->battery_pct);
    } else {
        int pct = st->battery_pct < 0 ? 0 : (st->battery_pct > 100 ? 100 : st->battery_pct);
        char buf[8];
        snprintf(buf, sizeof(buf), "%d", pct);
        DrawStringCrisp(px, size, buf, 1, textAreaH, false, fgR, fgG, fgB);
        BatteryAccentRgb(pct, false, st->is_full, &barR, &barG, &barB);
        fillRatio = pct < 10 ? 10 : pct;
    }

    int barX = 1;
    int barW = size - 2;
    FillPillBar(px, size, size, barX, barY, barW, barH, trkA, trkV, trkV, trkV);
    if (fillRatio > 0) {
        int fw = (barW * fillRatio + 50) / 100;
        if (fw < barH) fw = barH;
        if (fw > barW) fw = barW;
        FillPillBar(px, size, size, barX, barY, fw, barH, 255, barR, barG, barB);
    }
}

int IconRenderer_GetNativeSize(void) {
    int sm = GetSystemMetrics(SM_CXSMICON);
    if (sm < 16) sm = 16;
    if (sm > 64) sm = 64;
    return sm;
}

HICON IconRenderer_CreateTrayIcon(const DeviceStatus* st, TrayIconStyle style, bool isLightTaskbar) {
    int size = IconRenderer_GetNativeSize();

    BITMAPINFO bmi;
    ZeroMemory(&bmi, sizeof(bmi));
    bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth       = size;
    bmi.bmiHeader.biHeight      = -size; /* top-down DIB */
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* bits = NULL;
    HDC screenDc = GetDC(NULL);
    HBITMAP hColor = CreateDIBSection(screenDc, &bmi, DIB_RGB_COLORS, &bits, NULL, 0);
    ReleaseDC(NULL, screenDc);
    if (!hColor || !bits) {
        if (hColor) DeleteObject(hColor);
        return LoadIconW(NULL, IDI_APPLICATION);
    }

    ZeroMemory(bits, (size_t)size * (size_t)size * 4u);
    RenderPixels((uint32_t*)bits, size, st, style, isLightTaskbar);

    HBITMAP hMask = CreateBitmap(size, size, 1, 1, NULL);
    ICONINFO ii;
    ZeroMemory(&ii, sizeof(ii));
    ii.fIcon    = TRUE;
    ii.hbmMask  = hMask;
    ii.hbmColor = hColor;

    HICON hIcon = CreateIconIndirect(&ii);
    if (hMask) DeleteObject(hMask);
    DeleteObject(hColor);
    return hIcon ? hIcon : LoadIconW(NULL, IDI_APPLICATION);
}

bool IconRenderer_WritePreviewBmp(const wchar_t* path) {
    DeviceStatus samples[7];
    ZeroMemory(samples, sizeof(samples));
    samples[0].mode = CONN_WIRELESS_24G; samples[0].has_reading = true;  samples[0].battery_pct = 100; samples[0].is_awake = true;
    samples[1].mode = CONN_WIRELESS_24G; samples[1].has_reading = true;  samples[1].battery_pct = 85;  samples[1].is_awake = false;
    samples[2].mode = CONN_WIRELESS_24G; samples[2].has_reading = true;  samples[2].battery_pct = 35;  samples[2].is_awake = true;
    samples[3].mode = CONN_WIRELESS_24G; samples[3].has_reading = true;  samples[3].battery_pct = 12;  samples[3].is_awake = true;
    samples[4].mode = CONN_WIRED;        samples[4].has_reading = true;  samples[4].battery_pct = 55;  samples[4].is_charging = true; samples[4].is_awake = true;
    samples[5].mode = CONN_WIRELESS_24G; samples[5].has_reading = false; samples[5].is_awake = true;
    samples[6].mode = CONN_DISCONNECTED;

    const int cols = 7;
    const int rows = 6; /* 3 styles at 20px + 3 styles at 16px */
    const int cell = 132;
    const int zoom = 5;
    const int imgW = cols * cell;
    const int imgH = rows * cell;

    uint32_t* canvas = (uint32_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (size_t)imgW * imgH * 4u);
    if (!canvas)
        return false;

    for (int i = 0; i < imgW * imgH; i++)
        canvas[i] = 0xFF301612u; /* dark reddish Win11 taskbar background (#301612) */

    int sizes[2] = { 20, 16 };
    int r = 0;
    for (int si = 0; si < 2; si++) {
        int sz = sizes[si];
        for (int st = 0; st < 3; st++) {
            for (int c = 0; c < cols; c++) {
                uint32_t smallBuf[32 * 32] = {0};
                RenderPixels(smallBuf, sz, &samples[c], (TrayIconStyle)st, false);

                int ox = c * cell + (cell - sz * zoom) / 2;
                int oy = r * cell + (cell - sz * zoom) / 2;
                for (int py = 0; py < sz; py++) {
                    for (int pxx = 0; pxx < sz; pxx++) {
                        uint32_t sp = smallBuf[py * sz + pxx];
                        uint8_t sa = (uint8_t)(sp >> 24);
                        if (sa == 0) continue;
                        uint8_t sr = (uint8_t)(sp >> 16);
                        uint8_t sg = (uint8_t)(sp >> 8);
                        uint8_t sb = (uint8_t)(sp);
                        /* Composite premultiplied pixel over #301612 */
                        uint32_t inv = 255u - sa;
                        uint8_t cr = (uint8_t)(sr + (0x30u * inv + 127u) / 255u);
                        uint8_t cg = (uint8_t)(sg + (0x16u * inv + 127u) / 255u);
                        uint8_t cb = (uint8_t)(sb + (0x12u * inv + 127u) / 255u);
                        uint32_t packed = 0xFF000000u | ((uint32_t)cr << 16) | ((uint32_t)cg << 8) | cb;
                        for (int zy = 0; zy < zoom; zy++) {
                            for (int zx = 0; zx < zoom; zx++) {
                                canvas[(oy + py * zoom + zy) * imgW + (ox + pxx * zoom + zx)] = packed;
                            }
                        }
                    }
                }
            }
            r++;
        }
    }

    FILE* f = _wfopen(path, L"wb");
    if (!f) {
        HeapFree(GetProcessHeap(), 0, canvas);
        return false;
    }

    uint32_t rowBytes = (uint32_t)imgW * 4u;
    BITMAPFILEHEADER bfh;
    ZeroMemory(&bfh, sizeof(bfh));
    bfh.bfType = 0x4D42;
    bfh.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    bfh.bfSize = bfh.bfOffBits + rowBytes * (uint32_t)imgH;

    BITMAPINFOHEADER bih;
    ZeroMemory(&bih, sizeof(bih));
    bih.biSize = sizeof(BITMAPINFOHEADER);
    bih.biWidth = imgW;
    bih.biHeight = -imgH;
    bih.biPlanes = 1;
    bih.biBitCount = 32;
    bih.biCompression = BI_RGB;

    fwrite(&bfh, sizeof(bfh), 1, f);
    fwrite(&bih, sizeof(bih), 1, f);
    fwrite(canvas, rowBytes, (size_t)imgH, f);
    fclose(f);
    HeapFree(GetProcessHeap(), 0, canvas);
    return true;
}
