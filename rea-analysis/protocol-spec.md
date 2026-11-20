# Attack Shark X11 Mouse — Reverse-Engineered Protocol & Binary Format Specification

Clean-room specification derived from read-only static analysis and disassembly of `C:\Program Files (x86)\X11 Mouse\Mouse.exe`, `hiddriver_1.dll`, `hiddriver_2.dll`, `Hook.dll`, and UI resources using `morluto/rea` (`rea-agents@6.9.0`) and Rizin (`v0.9.1`).

Clean-room artifacts in this folder:
- `protocol-spec.md` — full USB/HID, BLE, and binary format specification
- `hook-table-151.json` — 151-entry VK / scan code / HID keycode table

*(Raw local disassembly dumps `*.evidence.json` / `*.txt` generated during static inspection are excluded from version control via `.gitignore`.)*

---

## 1. USB / HID Device & Transport Layer

### 1.1 Hardware Identifiers (`Mouse.exe` `0x40ed43..0x40f068`, `hiddriver_1.dll`)
| Mode | Transport DLL | VID | PID | Monitor Window Messages | Feature / Monitor Matching |
|---|---|---|---|---|---|
| **Wired** | `hiddriver_2.dll` | `0x1D57` | `0xFA55` | `0x0603` (data), `0x0604` (hot-plug) | Monitor `UsagePage=0x000A, Usage=0x0000` (In=`5B`), Feature `UsagePage=0x000B, Usage=0x0000` (Feat=`134B`) |
| **2.4 GHz Wireless** | `hiddriver_1.dll` | `0x1D57` | `0xFA60` | `0x0601` (data), `0x0602` (hot-plug) | Monitor `UsagePage=0x000A, Usage=0x0000` (In=`5B`), Feature `UsagePage=0x000B, Usage=0x0000` (Feat=`134B`) |
| **Bluetooth (BLE)** | `hidapi.dll` + WinRT BLE (`0x4058e0`, `0x40f03d`) | `0x045E` | `0x0055` | GATT characteristic `0xFEE3` | Skips Report `0x06` (`0x4138c8`) |

- **`hiddriver_1.dll` Internals**:
  - `Set_VIDPID(int vid, int pid)` @ `0x10002370` (`stdcall`)
  - `Open_DevMonitor(HWND hWnd, UINT dataMsg, UINT hotplugMsg)` @ `0x10002150` -> `fcn.10001d70` enumerates HID collections matching `(UsagePage == 0x0A && Usage == 0x00 && VID && PID)`, opens with `FILE_SHARE_READ | FILE_SHARE_WRITE` (`0x3`).
  - Monitor thread `fcn.10001a70` (`0x10001b37..0x10001b95`): overlapped `ReadFile`; if `buf[0] == 0x03`, posts `wParam = buf[1] | (buf[2] << 8)`, `lParam = buf[3] | (buf[4] << 8)` to `hWnd`.
  - `Open_FeatureDevice()` @ `0x10002320` -> `fcn.10001d70` matches `(UsagePage == 0x0B && Usage == 0x00 && VID && PID)` with `FILE_SHARE_READ | FILE_SHARE_WRITE`.
  - `SetFeature(void* buf, int len)` @ `0x10002340` -> Win32 `HidD_SetFeature`.
  - `GetFeature(void* buf, int len)` @ `0x10001f20` -> Win32 `HidD_GetFeature` (exported, but never called by `Mouse.exe`; all runtime status arrives asynchronously on Monitor Report `0x03`).

### 1.2 Send Reliability & Retries (`Mouse.exe` `0x413560`)
- Priority order: Wired (`[esi+0x3d0] == 1`) -> 2.4G Wireless (`[esi+0x3cc] == 1 && [esi+0x3e0] != 0` awake) -> BLE (`[esi+0x3d4] == 1`).
- On **2.4G Wireless**:
  - Clears ACK flag `[ebx+0x3ec] = 0`.
  - Retries up to 10 iterations until `[ebx+0x3ec] != 0` (set by Report `0x03` ACK `0x50`), sleeping `500 ms` per normal attempt or `1000 ms` per macro chunk.
  - Note: For `0x09` macro uploads on 2.4G, chunk 2 replaces byte `[1]` (`0x40`) with `0x0C` (`0x413835`).

---

## 2. Asynchronous Monitor Input Reports (`UsagePage 0x0A`, Report ID `0x03`, 5 bytes)

Parsed in `fcn.00413010` (2.4G) and `fcn.00413360` (Wired):
Layout: `[0x03, b1, b2, b3, b4]`, requiring `b1 == 0x55`.

| Command (`b2`) | Meaning | Payload Decoding (`b3`, `b4`) | Side Effects in `Mouse.exe` |
|---|---|---|---|
| `0x10` | Active DPI Stage Changed | `stageIdx = (b3 | (b4 << 8)) - 1` (`0..7`) | Updates `profile.ActiveDpiStage`, posts DPI OSD (`0x469`) |
| `0x20` | Polling Rate Changed | `rateIdx = (b3 | (b4 << 8))` (`0..3`) | Updates `profile.PollingRate` (`125/250/500/1000 Hz`) |
| `0x40` | Battery / Charge / Heartbeat | `state = b3`, `pct = b4` (`0..100`) | Resets `6000 ms` sleep watchdog (`timer 0x200`); `state==2` = Full (`100%`), `state==3` (or `!=2` on wired) = Charging (`timer 0x203`); otherwise battery `% = pct`, marks 2.4G mouse awake (`0x3e0=1`) |
| `0x50` | Wireless `SetFeature` ACK | `ack = (b3 == 0x00)` | Sets `[edi+0x3ec] = ack ? 1 : 0` to release 2.4G send loop |
| `0x60` | Legacy Battery Bar | `val = (b3 | (b4 << 8))` (`1..10`) | Stores raw bar value (`[edi+0x174]`) |
| `0x70` | Light Mode Changed | `mode = ((b3 | (b4 << 8)) >> 4) & 0x0F` | Updates `profile.LightMode` |
| `0x80` | Active Profile Switched | `profileIdx = (b3 | (b4 << 8))` (`0..4`) | Switches active profile and saves `pro.data` |

---

## 3. Feature Write Reports (`UsagePage 0x0B`, `SetFeature`)

Every variable-length packet with a checksum uses an **unsigned 16-bit byte sum over `buf[3 .. checksumOffset - 1]`**, written **big-endian**:
```
buf[checksumOffset]     = (sum >> 8) & 0xFF;
buf[checksumOffset + 1] = sum & 0xFF;
```
Note: All `SetFeature` buffers on `COL04` can be padded cleanly to 134 bytes (`FeatureReportByteLength`) or passed with their documented command length when the HID transport requires full report length. (`hiddriver_1.dll` forwards `(buf, len)` directly or via `HidD_SetFeature`; using a 134-byte report buffer with unused trailing bytes zeroed satisfies Windows `HidD_SetFeature`'s exact `FeatureReportByteLength = 134` requirement.)

### 3.1 Report `0x0C` — Force Sync / Reset Header (10 bytes, `0x413d48`)
Sent before all configuration reports during Reset / Factory Reset (`[esi+0x50cd0] == 1`):
```
[0x0C, 0x0A, 0x01, 0xFE, 0x01, 0xFE, 0x00, 0x00, 0x00, 0x00]
```

### 3.2 Report `0x04` — DPI, Sensor Parameters & Stage Colors (56 bytes, `0x413fd0`)
| Offset | Size | Field / Encoding | Source (`Profile` offset) |
|---|---|---|---|
| `0..2` | 3 | Fixed header `0x04, 0x38, 0x01` | — |
| `3` | 1 | `AngleSnap` (`0` = Off, `1` = On) | `+0x944` |
| `4` | 1 | `RippleControl` (`0` = Off, `1` = On) | `+0x940` |
| `5` | 1 | DPI Stage Enable Bitmask (`bit c` set iff stage `c` enabled) | `+0x904..0x920` |
| `6` | 1 | High-Range DPI Bitmask 1 (`bit c` set iff `dpiRaw[c] > 120`) | Derived (`0x4140b0..0x414200`) |
| `7` | 1 | High-Range DPI Bitmask 2 (identical to byte `6`) | Derived (`0x41419f..0x414200`) |
| `8..15` | 8 | `DpiRegisterTable[idx]` byte for stage `c = 0..7` | `+0x8c4[c]` (`1..220` = `100..22000 DPI`) |
| `16..23` | 8 | DPI Range Flag byte for stage `c = 0..7` (`0` if `raw <= 100`, `1` if `raw > 100`) | Derived |
| `24` | 1 | `ActiveDpiStage + 1` (`1..8`) | `+0x934` |
| `25..48` | 24 | 8 × `(R, G, B)` colors for DPI stages `1..8` | `+0x960..0x97F` (`COLORREF` `B` at +2, `G` at +1, `R` at +0) |
| `49` | 1 | Constant `0x02` | `0x41430b` |
| `50..51` | 2 | Big-endian 16-bit sum of `buf[3..49]` | `0x414310..0x414392` |
| `52..55` | 4 | Zero padding `0x00, 0x00, 0x00, 0x00` | — |

**DPI Value -> Register Index Mapping (`raw = dpi / 100`, `1..220`, `0x4140b0..0x41411b`)**:
- `raw <= 100`: `idx = raw * 2 - 1`, `flag = 0`, `highBit = 0`
- `101 <= raw <= 120`: `idx = raw + 99`, `flag = 1`, `highBit = 0`
- `121 <= raw <= 200`: `idx = raw - 1`, `flag = 1`, `highBit = 1`
- `201 <= raw <= 220`: `idx = (raw - 200) / 2 + 199`, `flag = 1`, `highBit = 1`

Full 220-entry `DpiRegisterTable` (`0x41c4a0`, stored as `uint32`s, low byte used):
```
[1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 14, 15, 16, 17, 18, 19, 21, 22, 23, 24, 25,
 27, 28, 29, 30, 31, 32, 34, 35, 36, 37, 38, 39, 41, 42, 43, 44, 45, 47, 48, 49, 50, 51,
 52, 54, 55, 56, 57, 58, 59, 61, 62, 63, 64, 65, 67, 68, 69, 70, 71, 72, 74, 75, 76, 77,
 78, 79, 81, 82, 83, 84, 85, 87, 88, 89, 90, 91, 92, 94, 95, 96, 97, 98, 99, 101, 102, 103,
 104, 105, 107, 108, 109, 110, 111, 112, 114, 115, 116, 117, 118, 119, 121, 122, 123, 124,
 125, 127, 128, 129, 130, 131, 132, 134, 135, 136, 137, 138, 139, 141, 142, 143, 144, 145,
 147, 148, 149, 150, 151, 152, 154, 155, 156, 157, 158, 159, 161, 162, 163, 164, 165, 167,
 168, 169, 170, 171, 172, 174, 175, 176, 177, 178, 179, 181, 182, 183, 184, 185, 187, 188,
 189, 190, 191, 192, 194, 195, 196, 197, 198, 199, 201, 202, 203, 204, 205, 207, 208, 209,
 210, 211, 212, 214, 215, 216, 217, 218, 219, 221, 222, 223, 224, 225, 227, 228, 229, 230,
 231, 232, 234, 235, 118, 119, 121, 122, 123, 124, 125, 127, 128, 129, 130, 131, 132, 134,
 135, 136, 137, 138, 139, 141]
```

### 3.3 Report `0x05` — Lighting, Sleep Timers & Debounce (15 bytes, `0x413d92..0x413e7f`)
| Offset | Size | Field / Encoding | Source (`Profile` offset) |
|---|---|---|---|
| `0..2` | 3 | Fixed header `0x05, 0x0F, 0x01` | — |
| `3` | 1 | `(LightMode & 0x0F) << 4` (`0..6`: Off, Static, Breathing, Neon, Color Breathing, Static DPI, Breathing DPI) | `+0x94c` |
| `4` | 1 | `(SleepTimerMin & 0xF0) \| ((9 - BreathSpeed) & 0x0F)` (`BreathSpeed` in `4..8`) | `+0x92c`, `+0x928` |
| `5` | 1 | `((SleepTimerMin & 0x0F) << 4) \| ((LightMode == 1 \|\| LightMode == 5) ? (Brightness & 0x0F) : 0x08)` (`Brightness` in `1..8`) | `+0x924` |
| `6..8` | 3 | Light Color `(R, G, B)` | `+0x95c..0x95e` |
| `9` | 1 | `LightOffTimerMin` (`1..60`) | `+0x930` |
| `10` | 1 | `KeyDebounceMs` (`2..25`) | `+0x958` |
| `11..12` | 2 | Big-endian 16-bit sum of `buf[3..10]` | `0x413e35..0x413e76` |
| `13..14` | 2 | Zero padding `0x00, 0x00` | — |

### 3.4 Report `0x06` — Polling Rate (9 bytes, `0x413e84..0x413eeb`)
| Offset | Size | Value |
|---|---|---|
| `0..2` | 3 | `0x06, 0x09, 0x01` |
| `3` | 1 | Encoded period (`profile+0x938`: `0 -> 8` [125Hz], `1 -> 4` [250Hz], `2 -> 2` [500Hz], `3 -> 1` [1000Hz]) |
| `4` | 1 | Bitwise NOT `0xFF ^ buf[3]` |
| `5..8` | 4 | Zero padding |

### 3.5 Report `0x08` — 18 Button Mappings (59 bytes, `0x414670`)
- Header `[0..2]`: `0x08, 0x3B, 0x01`
- Entries `[3 + i*3 .. 5 + i*3]` for button slots `i = 0..17` (54 bytes):
  - Default action IDs (`0x41cd50`): Slot `0`=1 (Left), `1`=2 (Right), `2`=3 (Middle), `3`=0, `4`=0, `5`=6 (DPI Cycle), `6`=4 (Forward), `7`=5 (Backward), `8..17`=0.
  - Visible X11 UI buttons (`ms_1_page.xml`): `key_1` (Slot 0), `key_2` (Slot 1), `key_3` (Slot 2), `key_7` (Slot 6, Forward), `key_8` (Slot 7, Backward).
  - Lookup `(b0, b1, b2)` from 63-entry Action Wire Table (`0x41c818..0x41cd08`):
    - If `ActionId == 0x10` (Shortcut): `b1 = button.ShortcutModifiers` (`bit0`=Ctrl, `bit1`=Shift, `bit2`=Alt, `bit3`=Win), `b2 = button.ShortcutKeyFwCode`.
    - If `ActionId == 0x11` (Macro): `b1 = 0x00`, `b2 = (byte)(i + 1)` (1-based button slot index).
- Checksum `[57..58]`: Big-endian 16-bit sum of `buf[3..56]`.

### 3.6 Report `0x09` — Macro Upload (131 bytes logical -> 3 × 64-byte packets, `0x4143c0` + `0x413560`)
Logical 131-byte buffer:
- `[0..2]`: `0x09, 0x83, buttonIndex1Based (1..18)`
- `[3]`: `MacroCycleMode` (`0`=Loop N times, `1`=Stop on any key, `2`=Hold to run / release to stop)
- `[4..6]`: `btn+0x43c` repeated 3× (`0x00` in standard UI usage)
- `[7]`: `RepeatCount` (`max(1, min(255, repeatCount))`)
- `[8..27]`: 20 zero bytes
- `[28]`: `totalPairs = actionCount + extraLongDelayPairs` (max 50 pairs in `[29..128]`)
- `[29 .. 29 + totalPairs*2 - 1]`: Encoded pairs `[timingAndDir, keyCode]` from up to 46 actions:
  - Action `keyCode`: HID key usage from `Hook.dll` table, or mouse code (`0xF1`=LMB, `0xF2`=RMB, `0xF3`=MMB, `0xF4`=X2/Forward, `0xF5`=X1/Backward).
  - Action `state` (`0x40bb69..0x40bccb`): `0x01` = Press/Down (wait: in `0x40bb69`, `edi = 0` on Down and `[var_9bh] = 0x01` when `edi == 0`, so `1` = Down -> `dirFlag = 0x01`; `2` = Up -> `dirFlag = 0x81`).
  - If `delayMs <= 1270` (`0x4F6`): clamp `delayMs >= 1`, `ticks = (int)((float)delayMs / 10.0f + 0.5)`, emit `[(byte)(ticks | dirFlag), keyCode]`.
  - If `delayMs > 1270`:
    - `chunks200 = delayMs / 200`, `rem = max(1, delayMs - chunks200 * 200)`.
    - Emit `[(byte)((int)((float)rem / 10.0f + 0.5) | dirFlag), keyCode]`.
    - If `extraPairs < 10`: `extraPairs++`, emit `[(byte)chunks200, 0x03]` (`0x4144ff..0x41454b`).
- `[129..130]`: Big-endian 16-bit sum of `buf[3..128]`.

**64-byte Wire Chunking (`0x413560`)**:
Copies `buf[3..130]` (128 bytes, zero-padded to 180 bytes) and splits into `k = 0, 1, 2`:
- Chunk `k`: `[0x09, lenByte, buttonIndex1Based, k, payload[k*60 .. k*60 + 59]]`
- **Wired**: `lenByte = 0x40` for all 3 chunks.
- **2.4G Wireless**: `lenByte = 0x40` for chunks `0` and `1`, **`lenByte = 0x0C` for chunk `2`** (`0x413835`).

---

## 4. Legacy Binary Storage Layouts

### 4.1 `pro.data` & `.profile` (`0x4156b0`, `0x4159a0`, `0x415c10`)
- **`pro.data`**: `0x28464` (164,964) byte header (`uint32 activeProfileIndex` at offset `0`) followed by `N` × `0x80E0` (32,992) byte `Profile` records.
- **`.profile`**: Single raw `0x80E0` byte `Profile` record.
- Key offsets inside `0x80E0`-byte `Profile` struct:
  - `+0x000`: Profile name (`wchar_t[250]`, UTF-16LE null-terminated)
  - `+0x8B4`: LOD (`uint32`, `0`=1mm, `1`=2mm)
  - `+0x8C4`: `uint32[8]` DPI X raw (`1..220`, meaning `100..22000` DPI)
  - `+0x8E4`: `uint32[8]` DPI Y raw (`1..220`)
  - `+0x904`: `uint32[8]` DPI stage enabled (`0` or `1`)
  - `+0x924`: Light Brightness (`uint32`, `1..8`, default `8`)
  - `+0x928`: Light Breathing Speed (`uint32`, `4..8`, default `6`)
  - `+0x92C`: Deep Sleep Timer minutes (`uint32`, `1..60`, default `10`)
  - `+0x930`: Light Off Timer minutes (`uint32`, `1..60`, default `1`)
  - `+0x934`: Active DPI stage index (`uint32`, `0..7`, default `1`)
  - `+0x938`: Polling Rate (`uint32`, `0..3` -> 125/250/500/1000 Hz, default `3`)
  - `+0x940`: Ripple Control (`uint32`, `0`/`1`)
  - `+0x944`: Angle Snap (`uint32`, `0`/`1`)
  - `+0x94C`: Light Mode (`uint32`, `0..6`)
  - `+0x950`: BLE Light Mode (`uint32`, default `3`)
  - `+0x958`: Key Debounce ms (`uint32`, `2..25`, default `4`)
  - `+0x95C`: Light Color (`COLORREF` `0x00BBGGRR`: byte 0 = R, byte 1 = G, byte 2 = B; default `0x00FF0000` = Blue `RGB(0, 0, 255)`, verified in `0x40f4a4..0x40f4d6`)
  - `+0x960`: `uint32[8]` DPI Colors (byte0=R, byte1=G, byte2=B: `[0xff, 0xff00, 0xff0000, 0xffff, 0xffff00, 0xff00ff, 0x40ff, 0xffffff]`)
  - `+0x9A0`: `Button[18]` array, stride `0x4F8` (1,272 bytes):
    - `+0x00`: slot index (`byte`)
    - `+0x01`: `ActionId` (`byte`)
    - `+0x03`: `ShortcutKeyFwCode` (`byte`)
    - `+0x07`: `ShortcutModifiers` (`byte`)
    - `+0x08`: `uint16` marker (`0x0232` = `562`)
    - `+0x0C`: `MacroCycleMode` (`uint32`)
    - `+0x10`: `MacroName` (`wchar_t[24]`, 48 bytes)
    - `+0x40`: `MacroRepeatCount` (`uint32`)
    - `+0x44`: `MacroActionCount` (`uint16`)
    - `+0x430`: `MacroLibraryIndex` (`int32`, `-1` if unassigned)
    - `+0x43C`: `uint8` macro flag (`0`)
    - `+0x440`: `MacroAction[46]` (`4` bytes each: `fwCode`, `state` [`1`=Down, `2`=Up], `uint16 delayMs`)

### 4.2 `macro.data` & `.macro` (`0x414d04..0x414e5c`, `0x416190`, `0x4163e0`)
- Sequence (or single entry for `.macro`) of `0xF0` (**240-byte**) records:
  - `+0x00` (48 bytes): Name (`wchar_t[24]`, UTF-16LE)
  - `+0x30` (`uint32`): Repeat count (`>= 1`)
  - `+0x34` (`uint32`): Action count (`0..46`)
  - `+0x38` (184 bytes = 46 × 4 bytes): Actions `[byte fwCode, byte state, uint16 delayMs]` (`1`=Down, `2`=Up)
