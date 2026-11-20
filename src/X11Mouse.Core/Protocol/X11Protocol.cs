using System;
using System.Collections.Generic;
using X11Mouse.Core.Models;

namespace X11Mouse.Core.Protocol;

public readonly record struct ActionWireSpec(byte ActionId, byte WireB0, byte WireB1, byte WireB2, string Name, string Category);

public enum MonitorEventType
{
    Unknown = 0,
    DpiChanged = 0x10,
    PollingRateChanged = 0x20,
    BatteryOrHeartbeat = 0x40,
    WirelessAck = 0x50,
    LegacyBatteryBar = 0x60,
    LightModeChanged = 0x70,
    ProfileChanged = 0x80,
}

public readonly record struct MonitorEvent(
    MonitorEventType Type,
    byte RawCommand,
    byte ArgLow,
    byte ArgHigh,
    ushort Arg16);

public sealed class ProfileDiff
{
    public bool DpiOrSensorDirty { get; set; }
    public bool LightOrPowerDirty { get; set; }
    public bool PollingRateDirty { get; set; }
    public bool ButtonsDirty { get; set; }
    public bool[] MacroSlotDirty { get; } = new bool[MouseProfile.ButtonSlotCount];

    public bool AnyDirty
    {
        get
        {
            if (DpiOrSensorDirty || LightOrPowerDirty || PollingRateDirty || ButtonsDirty)
                return true;
            foreach (bool dirty in MacroSlotDirty)
            {
                if (dirty) return true;
            }
            return false;
        }
    }
}

/// <summary>
/// Clean-room implementation of Attack Shark X11 HID protocol encoders, tables, checksums,
/// Monitor Input Report `0x03` parser, and profile diff detector.
/// </summary>
public static class X11Protocol
{
    public const ushort WiredVendorId = 0x1D57;
    public const ushort WiredProductId = 0xFA55;
    public const ushort Wireless24GVendorId = 0x1D57;
    public const ushort Wireless24GProductId = 0xFA60;
    public const ushort BleVendorId = 0x045E;
    public const ushort BleProductId = 0x0055;

    public const ushort MonitorUsagePage = 0x000A;
    public const ushort MonitorUsage = 0x0000;
    public const int MonitorReportLength = 5;

    public const ushort FeatureUsagePage = 0x000B;
    public const ushort FeatureUsage = 0x0000;
    public const int FeatureReportLength = 134;

    /// <summary>
    /// Verified 220-entry DPI sensor register lookup table from `Mouse.exe` `.rdata` (`0x41c4a0`).
    /// </summary>
    public static readonly byte[] DpiRegisterTable =
    [
          1,   2,   3,   4,   5,   6,   8,   9,  10,  11,  12,  14,  15,  16,  17,  18,  19,  21,  22,  23,  24,  25,
         27,  28,  29,  30,  31,  32,  34,  35,  36,  37,  38,  39,  41,  42,  43,  44,  45,  47,  48,  49,  50,  51,
         52,  54,  55,  56,  57,  58,  59,  61,  62,  63,  64,  65,  67,  68,  69,  70,  71,  72,  74,  75,  76,  77,
         78,  79,  81,  82,  83,  84,  85,  87,  88,  89,  90,  91,  92,  94,  95,  96,  97,  98,  99, 101, 102, 103,
        104, 105, 107, 108, 109, 110, 111, 112, 114, 115, 116, 117, 118, 119, 121, 122, 123, 124, 125, 127, 128, 129,
        130, 131, 132, 134, 135, 136, 137, 138, 139, 141, 142, 143, 144, 145, 147, 148, 149, 150, 151, 152, 154, 155,
        156, 157, 158, 159, 161, 162, 163, 164, 165, 167, 168, 169, 170, 171, 172, 174, 175, 176, 177, 178, 179, 181,
        182, 183, 184, 185, 187, 188, 189, 190, 191, 192, 194, 195, 196, 197, 198, 199, 201, 202, 203, 204, 205, 207,
        208, 209, 210, 211, 212, 214, 215, 216, 217, 218, 219, 221, 222, 223, 224, 225, 227, 228, 229, 230, 231, 232,
        234, 235, 118, 119, 121, 122, 123, 124, 125, 127, 128, 129, 130, 131, 132, 134, 135, 136, 137, 138, 139, 141
    ];

    public static readonly byte[] DefaultButtonActionIds =
    [
        1, 2, 3, 0, 0, 6, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    public static readonly int[] DefaultDpiRawValues = [8, 16, 24, 40, 50, 220, 0, 0];
    public static readonly bool[] DefaultDpiEnabled = [true, true, true, true, true, true, false, false];
    public static readonly RgbColor[] DefaultDpiColors =
    [
        new(255, 0, 0),
        new(0, 255, 0),
        new(0, 0, 255),
        new(255, 255, 0),
        new(0, 255, 255),
        new(255, 0, 255),
        new(255, 64, 0),
        new(255, 255, 255),
    ];

    /// <summary>
    /// Complete 63-entry button action mapping table (`0x41c818..0x41cd08`).
    /// Note entry `0x00` has two interpretations in firmware defaults: `[0]=(0,1,0,0)` = Left Click slot0 action-0 vs `[63]=(0,0,0,0)` disabled.
    /// When `ActionId == 0x00` explicitly disabled in `Mouse.exe`, the linear search at `0x414720` matches `[0]` (`0x01`) ONLY unless `ActionId==0` is disabled; WAIT — verify: in `0x414720`, entry 0 at `0x41c818` IS `id=0 -> (0x01, 0, 0)`! So Disabled button off writes `(0x01, 0x00, 0x00)`? Let's keep the exact linear order matching `0x41c818..0x41cd08` (63 entries `[0..62]`), because `[0]` (`0x00 -> 0x01`) is what `fcn.00414670` resolves when iterating `0x41c818` -> `0x41cd08`! Wait: check what `0x41cd08` was! `0x41cd08` was AFTER the loop bound `cmp eax, 0x41cd08; jl 0x414720`, so `[0..62]` is the EXACT loop!
    /// </summary>
    public static readonly ActionWireSpec[] ActionTable =
    [
        new(0x00, 0x01, 0x00, 0x00, "Button Off", "Basic"),
        new(0x01, 0x02, 0x00, 0x00, "Left Click", "Mouse"),
        new(0x02, 0x03, 0x00, 0x00, "Right Click", "Mouse"),
        new(0x03, 0x04, 0x00, 0x00, "Middle Click", "Mouse"),
        new(0x04, 0x06, 0x00, 0x00, "Forward", "Mouse"),
        new(0x05, 0x05, 0x00, 0x00, "Backward", "Mouse"),
        new(0x09, 0x07, 0x00, 0x00, "Double Click", "Mouse"),
        new(0x0A, 0x08, 0x00, 0x00, "Fire Key", "Mouse"),
        new(0x0B, 0x09, 0x00, 0x00, "Scroll Up", "Mouse"),
        new(0x0C, 0x0A, 0x00, 0x00, "Scroll Down", "Mouse"),
        new(0x0D, 0x0B, 0x00, 0x00, "Tilt Left", "Mouse"),
        new(0x0E, 0x0C, 0x00, 0x00, "Tilt Right", "Mouse"),
        new(0x06, 0x0D, 0x00, 0x00, "DPI Cycle", "DPI"),
        new(0x07, 0x0E, 0x00, 0x00, "DPI +", "DPI"),
        new(0x08, 0x0F, 0x00, 0x00, "DPI -", "DPI"),
        new(0x10, 0x11, 0x00, 0x00, "Shortcut", "Shortcut"),
        new(0x11, 0x12, 0x00, 0x00, "Macro", "Macro"),
        new(0x13, 0x13, 0x00, 0x00, "Profile Cycle", "Mouse"),
        new(0x73, 0x14, 0x00, 0x00, "Report Rate Cycle", "Mouse"),
        new(0x15, 0x2B, 0x00, 0x00, "Special 0x15", "Advanced"),
        new(0x16, 0x10, 0x00, 0x03, "Sniper Key (Easy Aim)", "Mouse"),
        new(0x17, 0x29, 0x00, 0x03, "LED Loop", "Lighting"),
        new(0x36, 0x34, 0x00, 0x00, "Mouse Lock", "Advanced"),
        new(0x37, 0x35, 0x00, 0x00, "Triple Click", "Advanced"),
        new(0x38, 0x36, 0x00, 0x00, "Scroll Toggle", "Advanced"),
        new(0x34, 0x3C, 0x00, 0x00, "Disable / No Op", "Advanced"),
        new(0x40, 0x15, 0x00, 0x00, "Media Player", "Media"),
        new(0x43, 0x16, 0x00, 0x00, "Previous Track", "Media"),
        new(0x44, 0x17, 0x00, 0x00, "Next Track", "Media"),
        new(0x41, 0x18, 0x00, 0x00, "Play / Pause", "Media"),
        new(0x42, 0x19, 0x00, 0x00, "Stop Media", "Media"),
        new(0x47, 0x1A, 0x00, 0x00, "Mute", "Media"),
        new(0x45, 0x1B, 0x00, 0x00, "Volume +", "Media"),
        new(0x46, 0x1C, 0x00, 0x00, "Volume -", "Media"),
        new(0x58, 0x1D, 0x00, 0x00, "Calculator", "System"),
        new(0x57, 0x1E, 0x00, 0x00, "Email", "System"),
        new(0x51, 0x11, 0x03, 0x12, "Browser Favorites", "Browser"),
        new(0x52, 0x20, 0x00, 0x00, "Browser Forward", "Browser"),
        new(0x53, 0x21, 0x00, 0x00, "Browser Backward", "Browser"),
        new(0x54, 0x22, 0x00, 0x00, "Browser Stop", "Browser"),
        new(0x59, 0x23, 0x00, 0x00, "My Computer", "System"),
        new(0x55, 0x24, 0x00, 0x00, "Browser Refresh", "Browser"),
        new(0x50, 0x25, 0x00, 0x00, "Browser Home", "Browser"),
        new(0x56, 0x26, 0x00, 0x00, "Browser Search", "Browser"),
        new(0x20, 0x11, 0x01, 0x1B, "Cut (Ctrl+X)", "Editing"),
        new(0x21, 0x11, 0x01, 0x06, "Copy (Ctrl+C)", "Editing"),
        new(0x22, 0x11, 0x01, 0x19, "Paste (Ctrl+V)", "Editing"),
        new(0x23, 0x11, 0x01, 0x12, "Open (Ctrl+O)", "Editing"),
        new(0x2F, 0x11, 0x01, 0x11, "New (Ctrl+N)", "Editing"),
        new(0x24, 0x11, 0x01, 0x16, "Save (Ctrl+S)", "Editing"),
        new(0x29, 0x11, 0x01, 0x13, "Print (Ctrl+P)", "Editing"),
        new(0x25, 0x11, 0x01, 0x09, "Find (Ctrl+F)", "Editing"),
        new(0x26, 0x11, 0x01, 0x1C, "Redo (Ctrl+Y)", "Editing"),
        new(0x27, 0x11, 0x01, 0x1D, "Undo (Ctrl+Z)", "Editing"),
        new(0x30, 0x11, 0x01, 0x2E, "Zoom In (Ctrl+=)", "Editing"),
        new(0x31, 0x11, 0x01, 0x2D, "Zoom Out (Ctrl+-)", "Editing"),
        new(0x28, 0x11, 0x01, 0x04, "Select All (Ctrl+A)", "Editing"),
        new(0x2B, 0x11, 0x04, 0x2B, "Switch Window (Alt+Tab)", "System"),
        new(0x2C, 0x11, 0x08, 0x07, "Show Desktop (Win+D)", "System"),
        new(0x2A, 0x11, 0x04, 0x3D, "Close Window (Alt+F4)", "System"),
        new(0x2D, 0x11, 0x08, 0x15, "Run (Win+R)", "System"),
        new(0x2E, 0x11, 0x08, 0x0F, "Lock PC (Win+L)", "System"),
        new(0x32, 0x11, 0x0A, 0x16, "Screen Capture (Win+Shift+S)", "System"),
    ];

    private static readonly Dictionary<byte, ActionWireSpec> ActionById;

    static X11Protocol()
    {
        ActionById = new Dictionary<byte, ActionWireSpec>();
        foreach (var item in ActionTable)
        {
            ActionById[item.ActionId] = item;
        }
    }

    public static MouseProfile CreateDefaultProfile(string name = "Profile1")
    {
        var profile = new MouseProfile
        {
            Name = name,
            PollingRate = PollingRate.Hz1000,
            ActiveDpiStage = 1,
            LightBrightness = 8,
            LightBreathSpeed = 6,
            SleepTimerMin = 10,
            LightOffTimerMin = 1,
            KeyDebounceMs = 4,
            LightMode = LightMode.LedOff,
            BleLightMode = 3,
            LightColor = new RgbColor(0, 0, 255),
            MoveModeFlag = true,
        };

        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            profile.DpiStages[i] = new DpiStageConfig
            {
                StageIndex = i,
                DpiXRaw = DefaultDpiRawValues[i],
                DpiYRaw = DefaultDpiRawValues[i],
                Enabled = DefaultDpiEnabled[i],
                Color = DefaultDpiColors[i],
            };
            profile.PaletteColors[i] = DefaultDpiColors[i];
        }

        for (int i = 0; i < MouseProfile.ButtonSlotCount; i++)
        {
            profile.Buttons[i] = new ButtonConfig
            {
                SlotIndex = (byte)i,
                ActionId = DefaultButtonActionIds[i],
                Marker562 = 562,
                MacroLibraryIndex = -1,
            };
        }

        return profile;
    }

    public static string GetActionDisplayName(ButtonConfig button)
    {
        if (button.ActionId == 0x10)
            return $"Shortcut: {KeyCodeTable.FormatShortcut(button.ShortcutModifiers, button.ShortcutKeyFwCode)}";
        if (button.ActionId == 0x11)
            return string.IsNullOrWhiteSpace(button.MacroName) ? "Macro" : $"Macro: {button.MacroName}";
        if (ActionById.TryGetValue(button.ActionId, out var spec))
            return spec.Name;
        return $"Action 0x{button.ActionId:X2}";
    }

    public static (byte RegisterByte, byte FlagByte, bool HighRangeBit) EncodeDpiStage(int dpiRaw)
    {
        if (dpiRaw <= 0)
            return (0, 0, false);

        int idx;
        byte flag;
        bool highBit;

        if (dpiRaw <= 100)
        {
            idx = dpiRaw * 2 - 1;
            flag = 0;
            highBit = false;
        }
        else if (dpiRaw <= 120)
        {
            idx = dpiRaw + 99;
            flag = 1;
            highBit = false;
        }
        else if (dpiRaw <= 200)
        {
            idx = dpiRaw - 1;
            flag = 1;
            highBit = true;
        }
        else
        {
            int clamped = Math.Min(dpiRaw, 220);
            idx = (clamped - 200) / 2 + 199;
            flag = 1;
            highBit = true;
        }

        idx = Math.Clamp(idx, 0, DpiRegisterTable.Length - 1);
        return (DpiRegisterTable[idx], flag, highBit);
    }

    public static byte[] BuildResetPacket() =>
    [
        0x0C, 0x0A, 0x01, 0xFE, 0x01, 0xFE, 0x00, 0x00, 0x00, 0x00
    ];

    public static byte[] BuildDpiPacket(MouseProfile profile)
    {
        var buf = new byte[56];
        buf[0] = 0x04;
        buf[1] = 0x38;
        buf[2] = 0x01;
        buf[3] = (byte)(profile.AngleSnap ? 1 : 0);
        buf[4] = (byte)(profile.RippleControl ? 1 : 0);

        byte enableMask = 0;
        byte highRangeMask = 0;

        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            var stage = profile.DpiStages[i];
            if (stage.Enabled)
                enableMask |= (byte)(1 << i);

            var (reg, flag, highBit) = EncodeDpiStage(stage.DpiXRaw);
            if (highBit)
                highRangeMask |= (byte)(1 << i);

            buf[8 + i] = reg;
            buf[16 + i] = flag;
            buf[25 + i * 3] = stage.Color.R;
            buf[26 + i * 3] = stage.Color.G;
            buf[27 + i * 3] = stage.Color.B;
        }

        buf[5] = enableMask;
        buf[6] = highRangeMask;
        buf[7] = highRangeMask;
        buf[24] = (byte)(Math.Clamp(profile.ActiveDpiStage, 0, 7) + 1);
        buf[49] = 0x02;

        WriteBigEndianChecksum(buf, 3, 50);
        return buf;
    }

    public static byte[] BuildLightAndPowerPacket(MouseProfile profile)
    {
        var buf = new byte[15];
        buf[0] = 0x05;
        buf[1] = 0x0F;
        buf[2] = 0x01;

        int mode = Math.Clamp((int)profile.LightMode, 0, 15);
        int sleep = Math.Clamp(profile.SleepTimerMin, 1, 60);
        int speed = Math.Clamp(profile.LightBreathSpeed, 4, 8);
        int bright = Math.Clamp(profile.LightBrightness, 1, 8);
        int lightOff = Math.Clamp(profile.LightOffTimerMin, 1, 60);
        int debounce = Math.Clamp(profile.KeyDebounceMs, 2, 25);

        buf[3] = (byte)((mode & 0x0F) << 4);
        buf[4] = (byte)((sleep & 0xF0) | ((9 - speed) & 0xFF));
        int lowNibble = (mode == 1 || mode == 5) ? (bright & 0x0F) : 0x08;
        buf[5] = (byte)(((sleep & 0x0F) << 4) | lowNibble);
        buf[6] = profile.LightColor.R;
        buf[7] = profile.LightColor.G;
        buf[8] = profile.LightColor.B;
        buf[9] = (byte)lightOff;
        buf[10] = (byte)debounce;

        WriteBigEndianChecksum(buf, 3, 11);
        return buf;
    }

    public static byte[] BuildPollingRatePacket(PollingRate pollingRate)
    {
        byte val = pollingRate switch
        {
            PollingRate.Hz125 => 8,
            PollingRate.Hz250 => 4,
            PollingRate.Hz500 => 2,
            PollingRate.Hz1000 => 1,
            _ => 2,
        };

        return
        [
            0x06, 0x09, 0x01, val, (byte)~val, 0x00, 0x00, 0x00, 0x00
        ];
    }

    public static byte[] BuildButtonsPacket(MouseProfile profile)
    {
        var buf = new byte[59];
        buf[0] = 0x08;
        buf[1] = 0x3B;
        buf[2] = 0x01;

        for (int i = 0; i < MouseProfile.ButtonSlotCount; i++)
        {
            var btn = profile.Buttons[i];
            byte b0 = 0;
            byte b1 = 0;
            byte b2 = 0;

            if (ActionById.TryGetValue(btn.ActionId, out var spec))
            {
                b0 = spec.WireB0;
                b1 = spec.WireB1;
                b2 = spec.WireB2;
            }

            if (btn.ActionId == 0x10)
            {
                b1 = (byte)btn.ShortcutModifiers;
                b2 = btn.ShortcutKeyFwCode;
            }
            else if (btn.ActionId == 0x11)
            {
                b1 = 0x00;
                b2 = (byte)(i + 1);
            }

            buf[3 + i * 3] = b0;
            buf[4 + i * 3] = b1;
            buf[5 + i * 3] = b2;
        }

        WriteBigEndianChecksum(buf, 3, 57);
        return buf;
    }

    public static byte[] BuildMacroLogicalPacket(int buttonIndex1Based, ButtonConfig button)
    {
        if (buttonIndex1Based is < 1 or > MouseProfile.ButtonSlotCount)
            throw new ArgumentOutOfRangeException(nameof(buttonIndex1Based));

        var buf = new byte[131];
        buf[0] = 0x09;
        buf[1] = 0x83;
        buf[2] = (byte)buttonIndex1Based;
        buf[3] = (byte)button.MacroCycleMode;
        buf[4] = button.MacroReservedFlag;
        buf[5] = button.MacroReservedFlag;
        buf[6] = button.MacroReservedFlag;
        buf[7] = (byte)(button.MacroRepeatCount == 0 ? 1 : Math.Min(button.MacroRepeatCount, 255u));

        int actionCount = Math.Min(button.MacroActions.Count, MacroDefinition.MaxActions);
        int extraPairs = 0;
        int outIdx = 0;

        for (int i = 0; i < actionCount && outIdx < 100; i++)
        {
            var act = button.MacroActions[i];
            byte dirFlag = act.State == MacroActionState.Down ? (byte)0x01 : (byte)0x81;
            int delay = act.DelayMs;

            if (delay <= 1270)
            {
                int clamped = Math.Max(1, delay);
                int ticks = (int)((float)clamped / 10.0f + 0.5);
                buf[29 + outIdx] = (byte)(ticks | dirFlag);
                buf[30 + outIdx] = act.FwCode;
                outIdx += 2;
            }
            else
            {
                int chunks200 = delay / 200;
                int rem = Math.Max(1, delay - chunks200 * 200);
                int remTicks = (int)((float)rem / 10.0f + 0.5);

                buf[29 + outIdx] = (byte)(remTicks | dirFlag);
                buf[30 + outIdx] = act.FwCode;
                outIdx += 2;

                if (outIdx >= 100)
                    break;

                if (extraPairs < 10)
                {
                    extraPairs++;
                    buf[29 + outIdx] = (byte)chunks200;
                    buf[30 + outIdx] = 0x03;
                }
                else
                {
                    int fallbackTicks = (int)((float)rem / 10.0f + 0.5);
                    buf[29 + outIdx] = (byte)(fallbackTicks | dirFlag);
                    buf[30 + outIdx] = act.FwCode;
                }
                outIdx += 2;
            }
        }

        buf[28] = (byte)(actionCount + extraPairs);
        WriteBigEndianChecksum(buf, 3, 129);
        return buf;
    }

    public static byte[][] SplitMacroChunks(ReadOnlySpan<byte> logical131, bool isWireless24G)
    {
        if (logical131.Length != 131)
            throw new ArgumentException("Macro logical packet must be 131 bytes.", nameof(logical131));

        var padded = new byte[180];
        logical131.Slice(3, 128).CopyTo(padded);

        byte btnIdx = logical131[2];
        var chunks = new byte[3][];
        for (int k = 0; k < 3; k++)
        {
            var pkt = new byte[64];
            pkt[0] = 0x09;
            pkt[1] = (isWireless24G && k == 2) ? (byte)0x0C : (byte)0x40;
            pkt[2] = btnIdx;
            pkt[3] = (byte)k;
            Array.Copy(padded, k * 60, pkt, 4, 60);
            chunks[k] = pkt;
        }
        return chunks;
    }

    public static MonitorEvent? ParseMonitorReport(ReadOnlySpan<byte> report)
    {
        if (report.Length < 5 || report[0] != 0x03 || report[1] != 0x55)
            return null;

        byte cmd = report[2];
        byte lo = report[3];
        byte hi = report[4];
        ushort arg16 = (ushort)(lo | (hi << 8));

        var type = Enum.IsDefined(typeof(MonitorEventType), (int)cmd)
            ? (MonitorEventType)cmd
            : MonitorEventType.Unknown;

        return new MonitorEvent(type, cmd, lo, hi, arg16);
    }

    public static ProfileDiff ComputeDiff(MouseProfile current, MouseProfile? committed, bool forceAll = false)
    {
        var diff = new ProfileDiff();
        if (forceAll || committed is null)
        {
            diff.DpiOrSensorDirty = true;
            diff.LightOrPowerDirty = true;
            diff.PollingRateDirty = true;
            diff.ButtonsDirty = true;
            for (int i = 0; i < MouseProfile.ButtonSlotCount; i++)
            {
                if (current.Buttons[i].ActionId == 0x11)
                    diff.MacroSlotDirty[i] = true;
            }
            return diff;
        }

        if (current.LiftOfDistanceMm != committed.LiftOfDistanceMm ||
            current.ActiveDpiStage != committed.ActiveDpiStage ||
            current.RippleControl != committed.RippleControl ||
            current.AngleSnap != committed.AngleSnap)
        {
            diff.DpiOrSensorDirty = true;
        }

        for (int i = 0; i < MouseProfile.DpiStageCount && !diff.DpiOrSensorDirty; i++)
        {
            var a = current.DpiStages[i];
            var b = committed.DpiStages[i];
            if (a.DpiXRaw != b.DpiXRaw || a.DpiYRaw != b.DpiYRaw || a.Enabled != b.Enabled || a.Color != b.Color)
                diff.DpiOrSensorDirty = true;
        }

        if (current.PollingRate != committed.PollingRate)
            diff.PollingRateDirty = true;

        if (current.LightMode != committed.LightMode ||
            current.BleLightMode != committed.BleLightMode ||
            current.LightBrightness != committed.LightBrightness ||
            current.LightBreathSpeed != committed.LightBreathSpeed ||
            current.LightColor != committed.LightColor ||
            current.LightOffTimerMin != committed.LightOffTimerMin ||
            current.SleepTimerMin != committed.SleepTimerMin ||
            current.KeyDebounceMs != committed.KeyDebounceMs)
        {
            diff.LightOrPowerDirty = true;
        }

        for (int i = 0; i < MouseProfile.ButtonSlotCount; i++)
        {
            var a = current.Buttons[i];
            var b = committed.Buttons[i];

            if (a.ActionId != b.ActionId ||
                a.ShortcutKeyFwCode != b.ShortcutKeyFwCode ||
                a.ShortcutModifiers != b.ShortcutModifiers)
            {
                diff.ButtonsDirty = true;
                if (a.ActionId == 0x11)
                    diff.MacroSlotDirty[i] = true;
            }
            else if (a.ActionId == 0x11 && !AreButtonMacrosEqual(a, b))
            {
                diff.MacroSlotDirty[i] = true;
            }
        }

        return diff;
    }

    private static bool AreButtonMacrosEqual(ButtonConfig a, ButtonConfig b)
    {
        if (a.MacroCycleMode != b.MacroCycleMode ||
            a.MacroRepeatCount != b.MacroRepeatCount ||
            a.MacroReservedFlag != b.MacroReservedFlag ||
            a.MacroActions.Count != b.MacroActions.Count)
        {
            return false;
        }

        for (int i = 0; i < a.MacroActions.Count; i++)
        {
            if (a.MacroActions[i].FwCode != b.MacroActions[i].FwCode ||
                a.MacroActions[i].State != b.MacroActions[i].State ||
                a.MacroActions[i].DelayMs != b.MacroActions[i].DelayMs)
            {
                return false;
            }
        }
        return true;
    }

    public static void WriteBigEndianChecksum(byte[] buffer, int startIndex, int checksumIndex)
    {
        uint sum = 0;
        for (int i = startIndex; i < checksumIndex; i++)
            sum += buffer[i];
        buffer[checksumIndex] = (byte)((sum >> 8) & 0xFF);
        buffer[checksumIndex + 1] = (byte)(sum & 0xFF);
    }
}
