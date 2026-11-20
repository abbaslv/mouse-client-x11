using System;
using System.Collections.Generic;

namespace X11Mouse.Core.Models;

/// <summary>
/// Active transport mode for the Attack Shark X11 mouse.
/// </summary>
public enum ConnectionMode
{
    Disconnected = 0,
    Wired = 1,
    Wireless24G = 2,
    Bluetooth = 3,
}

/// <summary>
/// Supported USB / 2.4 GHz polling rates (`profile+0x938`).
/// </summary>
public enum PollingRate
{
    Hz125 = 0,
    Hz250 = 1,
    Hz500 = 2,
    Hz1000 = 3,
}

/// <summary>
/// Lighting modes (`profile+0x94C`, `ms_lightmode1..7`).
/// </summary>
public enum LightMode
{
    LedOff = 0,
    Static = 1,
    Breathing = 2,
    Neon = 3,
    ColorBreathing = 4,
    StaticDpi = 5,
    BreathingDpi = 6,
}

/// <summary>
/// Macro playback cycle behavior (`btn+0x0C`).
/// </summary>
public enum MacroCycleMode
{
    LoopCount = 0,
    StopOnAnyKey = 1,
    HoldWhilePressed = 2,
}

/// <summary>
/// Delay timing mode when recording macros in the editor.
/// </summary>
public enum MacroDelayMode
{
    RecordDelay = 0,
    DefaultDelay = 1,
    NoDelay = 2,
}

/// <summary>
/// Action state inside a macro sequence.
/// </summary>
public enum MacroActionState : byte
{
    Down = 1,
    Up = 2,
}

/// <summary>
/// Win32 modifier bitmask encoded into `btn+0x07` for custom shortcuts (`ActionId = 0x10`).
/// </summary>
[Flags]
public enum ShortcutModifiers : byte
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Win = 1 << 3,
}

/// <summary>
/// Simple immutable RGB color value (matching wire order R, G, B and Win32 COLORREF byte layout).
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public uint ToColorRef() => (uint)(R | (G << 8) | (B << 16));

    public static RgbColor FromColorRef(uint colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Single action step inside a recorded or edited macro (4 bytes on disk in legacy format).
/// </summary>
public sealed class MacroAction
{
    public byte FwCode { get; set; }
    public MacroActionState State { get; set; } = MacroActionState.Down;
    public ushort DelayMs { get; set; } = 10;

    public MacroAction Clone() => new()
    {
        FwCode = FwCode,
        State = State,
        DelayMs = DelayMs,
    };
}

/// <summary>
/// Saved macro definition (240 bytes / `0xF0` in `macro.data` and `.macro`).
/// </summary>
public sealed class MacroDefinition
{
    public const int MaxActions = 46;

    public string Name { get; set; } = "Macro1";
    public uint RepeatCount { get; set; } = 1;
    public List<MacroAction> Actions { get; set; } = new();

    public MacroDefinition Clone()
    {
        var copy = new MacroDefinition
        {
            Name = Name,
            RepeatCount = RepeatCount,
        };
        foreach (var action in Actions)
        {
            copy.Actions.Add(action.Clone());
        }
        return copy;
    }
}

/// <summary>
/// One of 18 programmable button slots (`0..17`, stride `0x4F8` inside `Profile`).
/// Physical X11 mouse exposes slots 0 (Left), 1 (Right), 2 (Middle), 6 (Forward), 7 (Backward), plus slot 5 (DPI cycle).
/// </summary>
public sealed class ButtonConfig
{
    public byte SlotIndex { get; set; }
    public byte ActionId { get; set; }
    public byte AuxByte2 { get; set; }
    public byte ShortcutKeyFwCode { get; set; }
    public ShortcutModifiers ShortcutModifiers { get; set; }
    public ushort Marker562 { get; set; } = 562;

    // Macro assignment fields
    public MacroCycleMode MacroCycleMode { get; set; } = MacroCycleMode.LoopCount;
    public string MacroName { get; set; } = string.Empty;
    public uint MacroRepeatCount { get; set; } = 1;
    public int MacroLibraryIndex { get; set; } = -1;
    public byte MacroReservedFlag { get; set; }
    public List<MacroAction> MacroActions { get; set; } = new();

    public ButtonConfig Clone()
    {
        var copy = new ButtonConfig
        {
            SlotIndex = SlotIndex,
            ActionId = ActionId,
            AuxByte2 = AuxByte2,
            ShortcutKeyFwCode = ShortcutKeyFwCode,
            ShortcutModifiers = ShortcutModifiers,
            Marker562 = Marker562,
            MacroCycleMode = MacroCycleMode,
            MacroName = MacroName,
            MacroRepeatCount = MacroRepeatCount,
            MacroLibraryIndex = MacroLibraryIndex,
            MacroReservedFlag = MacroReservedFlag,
        };
        foreach (var action in MacroActions)
        {
            copy.MacroActions.Add(action.Clone());
        }
        return copy;
    }

    public void AssignMacro(MacroDefinition macro, int libraryIndex, MacroCycleMode cycleMode, uint repeatCount)
    {
        ActionId = 0x11;
        MacroLibraryIndex = libraryIndex;
        MacroCycleMode = cycleMode;
        MacroRepeatCount = Math.Clamp(repeatCount, 1u, 255u);
        MacroName = macro.Name.Length > 23 ? macro.Name[..23] : macro.Name;
        MacroActions.Clear();
        for (int i = 0; i < Math.Min(macro.Actions.Count, MacroDefinition.MaxActions); i++)
        {
            MacroActions.Add(macro.Actions[i].Clone());
        }
    }
}

/// <summary>
/// Single DPI stage configuration (`0..7`).
/// Raw unit is `1..220` (`Raw * 100 = 100..22000` DPI).
/// </summary>
public sealed class DpiStageConfig
{
    public int StageIndex { get; set; }
    public int DpiXRaw { get; set; } = 8;
    public int DpiYRaw { get; set; } = 8;
    public bool Enabled { get; set; } = true;
    public RgbColor Color { get; set; } = new(255, 0, 0);

    public int DpiValue => DpiXRaw * 100;

    public DpiStageConfig Clone() => new()
    {
        StageIndex = StageIndex,
        DpiXRaw = DpiXRaw,
        DpiYRaw = DpiYRaw,
        Enabled = Enabled,
        Color = Color,
    };
}

/// <summary>
/// Full Attack Shark X11 profile (`0x80E0` / 32,992 bytes in legacy storage).
/// </summary>
public sealed class MouseProfile
{
    public const int DpiStageCount = 8;
    public const int ButtonSlotCount = 18;

    public string Name { get; set; } = "Profile1";
    public string Description { get; set; } = string.Empty;

    public int LiftOfDistanceMm { get; set; } // 0 => 1mm, 1 => 2mm
    public DpiStageConfig[] DpiStages { get; set; } = new DpiStageConfig[DpiStageCount];
    public int ActiveDpiStage { get; set; } = 1; // 0..7
    public PollingRate PollingRate { get; set; } = PollingRate.Hz1000;
    public bool RippleControl { get; set; }
    public bool AngleSnap { get; set; }
    public int KeyDebounceMs { get; set; } = 4; // 2..25
    public LightMode LightMode { get; set; } = LightMode.LedOff;
    public int BleLightMode { get; set; } = 3;
    public int LightBrightness { get; set; } = 8; // 1..8
    public int LightBreathSpeed { get; set; } = 6; // 4..8
    public RgbColor LightColor { get; set; } = new(0, 0, 255);
    public int LightOffTimerMin { get; set; } = 1; // 1..60
    public int SleepTimerMin { get; set; } = 10; // 1..60
    public bool MoveModeFlag { get; set; } = true;
    public RgbColor[] PaletteColors { get; set; } = new RgbColor[8];
    public ButtonConfig[] Buttons { get; set; } = new ButtonConfig[ButtonSlotCount];

    public MouseProfile Clone()
    {
        var copy = new MouseProfile
        {
            Name = Name,
            Description = Description,
            LiftOfDistanceMm = LiftOfDistanceMm,
            ActiveDpiStage = ActiveDpiStage,
            PollingRate = PollingRate,
            RippleControl = RippleControl,
            AngleSnap = AngleSnap,
            KeyDebounceMs = KeyDebounceMs,
            LightMode = LightMode,
            BleLightMode = BleLightMode,
            LightBrightness = LightBrightness,
            LightBreathSpeed = LightBreathSpeed,
            LightColor = LightColor,
            LightOffTimerMin = LightOffTimerMin,
            SleepTimerMin = SleepTimerMin,
            MoveModeFlag = MoveModeFlag,
        };
        for (int i = 0; i < DpiStageCount; i++)
        {
            copy.DpiStages[i] = DpiStages[i].Clone();
            copy.PaletteColors[i] = PaletteColors[i];
        }
        for (int i = 0; i < ButtonSlotCount; i++)
        {
            copy.Buttons[i] = Buttons[i].Clone();
        }
        return copy;
    }
}

/// <summary>
/// Live mouse hardware telemetry state derived from HID enumeration and Monitor Report `0x03`.
/// </summary>
public sealed class DeviceStatus
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.Disconnected;
    public bool IsAwake { get; set; }
    public bool IsCharging { get; set; }
    public bool IsFullyCharged { get; set; }
    public int BatteryPercent { get; set; } = 100;
    public bool HasBatteryReading { get; set; }
    public DateTime? LastBatteryUpdateUtc { get; set; }
    public int? ActiveDpiStage { get; set; }
    public PollingRate? PollingRate { get; set; }
    public LightMode? LightMode { get; set; }
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public ushort VersionNumber { get; set; }
    public string ProductName { get; set; } = "Disconnected";
    public string? MonitorPath { get; set; }
    public string? FeaturePath { get; set; }

    public DeviceStatus Clone() => (DeviceStatus)MemberwiseClone();
}
