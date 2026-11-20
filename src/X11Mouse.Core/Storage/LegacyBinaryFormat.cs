using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.Core.Storage;

public sealed record ProDataBundle(int ActiveProfileIndex, List<MouseProfile> Profiles, byte[]? RawHeader = null);

/// <summary>
/// Binary serializer/deserializer for official Attack Shark X11 files:
/// `pro.data` (`0x28464` header + `N * 0x80E0` profiles), `.profile` (`0x80E0`),
/// `macro.data` (`N * 0xF0`), and `.macro` (`0xF0`).
/// </summary>
public static class LegacyBinaryFormat
{
    public const int ProDataHeaderSize = 0x28464;
    public const int ProfileRecordSize = 0x80E0;
    public const int ButtonRecordSize = 0x4F8;
    public const int MacroRecordSize = 0xF0;

    public static ProDataBundle ReadProData(string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);
        return ParseProData(data);
    }

    public static ProDataBundle ParseProData(ReadOnlySpan<byte> data)
    {
        if (data.Length < ProDataHeaderSize)
            throw new InvalidDataException($"pro.data too small ({data.Length} bytes, expected >= {ProDataHeaderSize}).");

        int activeIdx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
        byte[] rawHeader = data.Slice(0, ProDataHeaderSize).ToArray();

        var profiles = new List<MouseProfile>();
        int offset = ProDataHeaderSize;
        while (offset + ProfileRecordSize <= data.Length)
        {
            profiles.Add(ParseProfileRecord(data.Slice(offset, ProfileRecordSize)));
            offset += ProfileRecordSize;
        }

        return new ProDataBundle(activeIdx, profiles, rawHeader);
    }

    public static byte[] SerializeProData(int activeProfileIndex, IReadOnlyList<MouseProfile> profiles, ReadOnlySpan<byte> existingHeader = default)
    {
        int total = ProDataHeaderSize + profiles.Count * ProfileRecordSize;
        var buf = new byte[total];

        if (existingHeader.Length >= ProDataHeaderSize)
            existingHeader.Slice(0, ProDataHeaderSize).CopyTo(buf);

        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), Math.Max(0, activeProfileIndex));

        int offset = ProDataHeaderSize;
        foreach (var profile in profiles)
        {
            SerializeProfileRecord(profile).CopyTo(buf, offset);
            offset += ProfileRecordSize;
        }
        return buf;
    }

    public static MouseProfile ReadSingleProfile(string filePath) =>
        ParseProfileRecord(File.ReadAllBytes(filePath));

    public static MouseProfile ParseProfileRecord(ReadOnlySpan<byte> rec)
    {
        if (rec.Length < ProfileRecordSize)
            throw new InvalidDataException($"Profile record too small ({rec.Length} bytes, expected {ProfileRecordSize}).");

        var profile = X11Protocol.CreateDefaultProfile();
        string name = ReadUtf16String(rec.Slice(0, 200));
        if (!string.IsNullOrWhiteSpace(name))
            profile.Name = name;
        profile.Description = ReadUtf16String(rec.Slice(0xC8, 1000));

        profile.MoveModeFlag = rec[0x954] != 0;
        profile.LiftOfDistanceMm = ReadInt32(rec, 0x8B4);

        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            int rawX = ReadInt32(rec, 0x8C4 + i * 4);
            int rawY = ReadInt32(rec, 0x8E4 + i * 4);
            bool enabled = ReadInt32(rec, 0x904 + i * 4) != 0;
            uint colorRef = ReadUInt32(rec, 0x960 + i * 4);

            profile.DpiStages[i] = new DpiStageConfig
            {
                StageIndex = i,
                DpiXRaw = rawX,
                DpiYRaw = rawY,
                Enabled = enabled,
                Color = RgbColor.FromColorRef(colorRef),
            };
            profile.PaletteColors[i] = RgbColor.FromColorRef(ReadUInt32(rec, 0x980 + i * 4));
        }

        profile.LightBrightness = Math.Clamp(ReadInt32(rec, 0x924), 1, 8);
        profile.LightBreathSpeed = Math.Clamp(ReadInt32(rec, 0x928), 4, 8);
        profile.SleepTimerMin = Math.Clamp(ReadInt32(rec, 0x92C), 1, 60);
        profile.LightOffTimerMin = Math.Clamp(ReadInt32(rec, 0x930), 1, 60);
        profile.ActiveDpiStage = Math.Clamp(ReadInt32(rec, 0x934), 0, 7);
        profile.PollingRate = (PollingRate)Math.Clamp(ReadInt32(rec, 0x938), 0, 3);
        profile.RippleControl = ReadInt32(rec, 0x940) != 0;
        profile.AngleSnap = ReadInt32(rec, 0x944) != 0;
        profile.LightMode = (LightMode)Math.Clamp(ReadInt32(rec, 0x94C), 0, 6);
        profile.BleLightMode = ReadInt32(rec, 0x950);
        profile.KeyDebounceMs = Math.Clamp(ReadInt32(rec, 0x958), 2, 25);
        profile.LightColor = RgbColor.FromColorRef(ReadUInt32(rec, 0x95C));

        for (int b = 0; b < MouseProfile.ButtonSlotCount; b++)
        {
            var slotSpan = rec.Slice(0x9A0 + b * ButtonRecordSize, ButtonRecordSize);
            var btn = new ButtonConfig
            {
                SlotIndex = slotSpan[0],
                ActionId = slotSpan[1],
                AuxByte2 = slotSpan[2],
                ShortcutKeyFwCode = slotSpan[3],
                ShortcutModifiers = (ShortcutModifiers)slotSpan[7],
                Marker562 = BinaryPrimitives.ReadUInt16LittleEndian(slotSpan.Slice(8, 2)),
                MacroCycleMode = (MacroCycleMode)Math.Clamp(ReadInt32(slotSpan, 0x0C), 0, 2),
                MacroName = ReadUtf16String(slotSpan.Slice(0x10, 48)),
                MacroRepeatCount = Math.Max(1u, ReadUInt32(slotSpan, 0x40)),
                MacroLibraryIndex = ReadInt32(slotSpan, 0x430),
                MacroReservedFlag = slotSpan[0x43C],
            };

            int actCount = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(slotSpan.Slice(0x44, 2)), MacroDefinition.MaxActions);
            for (int a = 0; a < actCount; a++)
            {
                var aSpan = slotSpan.Slice(0x440 + a * 4, 4);
                btn.MacroActions.Add(new MacroAction
                {
                    FwCode = aSpan[0],
                    State = aSpan[1] == 2 ? MacroActionState.Up : MacroActionState.Down,
                    DelayMs = BinaryPrimitives.ReadUInt16LittleEndian(aSpan.Slice(2, 2)),
                });
            }

            profile.Buttons[b] = btn;
        }

        return profile;
    }

    public static byte[] SerializeProfileRecord(MouseProfile profile)
    {
        var buf = new byte[ProfileRecordSize];
        WriteUtf16String(buf.AsSpan(0, 200), profile.Name);
        WriteUtf16String(buf.AsSpan(0xC8, 1000), profile.Description);

        WriteInt32(buf, 0x898, 420);
        WriteInt32(buf, 0x89C, 3);
        WriteInt32(buf, 0x8A4, 6);
        WriteInt32(buf, 0x8B4, profile.LiftOfDistanceMm);

        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            var stage = profile.DpiStages[i];
            WriteInt32(buf, 0x8C4 + i * 4, stage.DpiXRaw);
            WriteInt32(buf, 0x8E4 + i * 4, stage.DpiYRaw);
            WriteInt32(buf, 0x904 + i * 4, stage.Enabled ? 1 : 0);
            WriteUInt32(buf, 0x960 + i * 4, stage.Color.ToColorRef());
            WriteUInt32(buf, 0x980 + i * 4, profile.PaletteColors[i].ToColorRef());
        }

        WriteInt32(buf, 0x924, profile.LightBrightness);
        WriteInt32(buf, 0x928, profile.LightBreathSpeed);
        WriteInt32(buf, 0x92C, profile.SleepTimerMin);
        WriteInt32(buf, 0x930, profile.LightOffTimerMin);
        WriteInt32(buf, 0x934, profile.ActiveDpiStage);
        WriteInt32(buf, 0x938, (int)profile.PollingRate);
        WriteInt32(buf, 0x940, profile.RippleControl ? 1 : 0);
        WriteInt32(buf, 0x944, profile.AngleSnap ? 1 : 0);
        WriteInt32(buf, 0x94C, (int)profile.LightMode);
        WriteInt32(buf, 0x950, profile.BleLightMode);
        buf[0x954] = profile.MoveModeFlag ? (byte)1 : (byte)0;
        WriteInt32(buf, 0x958, profile.KeyDebounceMs);
        WriteUInt32(buf, 0x95C, profile.LightColor.ToColorRef());

        for (int b = 0; b < MouseProfile.ButtonSlotCount; b++)
        {
            var btn = profile.Buttons[b];
            var slot = buf.AsSpan(0x9A0 + b * ButtonRecordSize, ButtonRecordSize);
            slot[0] = (byte)b;
            slot[1] = btn.ActionId;
            slot[2] = btn.AuxByte2;
            slot[3] = btn.ShortcutKeyFwCode;
            slot[7] = (byte)btn.ShortcutModifiers;
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8, 2), btn.Marker562);
            WriteInt32(slot, 0x0C, (int)btn.MacroCycleMode);
            WriteUtf16String(slot.Slice(0x10, 48), btn.MacroName);
            WriteUInt32(slot, 0x40, btn.MacroRepeatCount);

            int actCount = Math.Min(btn.MacroActions.Count, MacroDefinition.MaxActions);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(0x44, 2), (ushort)actCount);
            WriteInt32(slot, 0x430, btn.MacroLibraryIndex);
            WriteInt32(slot, 0x434, -1);
            slot[0x43C] = btn.MacroReservedFlag;

            for (int a = 0; a < actCount; a++)
            {
                var act = btn.MacroActions[a];
                var aSpan = slot.Slice(0x440 + a * 4, 4);
                aSpan[0] = act.FwCode;
                aSpan[1] = (byte)act.State;
                BinaryPrimitives.WriteUInt16LittleEndian(aSpan.Slice(2, 2), act.DelayMs);
            }
        }

        return buf;
    }

    public static List<MacroDefinition> ReadMacroFile(string filePath) =>
        ParseMacroStream(File.ReadAllBytes(filePath));

    public static List<MacroDefinition> ParseMacroStream(ReadOnlySpan<byte> data)
    {
        var list = new List<MacroDefinition>();
        int offset = 0;
        while (offset + MacroRecordSize <= data.Length)
        {
            list.Add(ParseMacroRecord(data.Slice(offset, MacroRecordSize)));
            offset += MacroRecordSize;
        }
        return list;
    }

    public static MacroDefinition ParseMacroRecord(ReadOnlySpan<byte> rec)
    {
        if (rec.Length < MacroRecordSize)
            throw new InvalidDataException($"Macro record too small ({rec.Length} bytes, expected {MacroRecordSize}).");

        string name = ReadUtf16String(rec.Slice(0, 48));
        uint repeat = Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x30, 4)));
        uint count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x34, 4)), (uint)MacroDefinition.MaxActions);

        var macro = new MacroDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Macro" : name,
            RepeatCount = repeat,
        };

        for (int i = 0; i < (int)count; i++)
        {
            var aSpan = rec.Slice(0x38 + i * 4, 4);
            macro.Actions.Add(new MacroAction
            {
                FwCode = aSpan[0],
                State = aSpan[1] == 2 ? MacroActionState.Up : MacroActionState.Down,
                DelayMs = BinaryPrimitives.ReadUInt16LittleEndian(aSpan.Slice(2, 2)),
            });
        }
        return macro;
    }

    public static byte[] SerializeMacros(IEnumerable<MacroDefinition> macros)
    {
        var list = new List<MacroDefinition>(macros);
        var buf = new byte[list.Count * MacroRecordSize];
        for (int i = 0; i < list.Count; i++)
        {
            SerializeMacroRecord(list[i]).CopyTo(buf, i * MacroRecordSize);
        }
        return buf;
    }

    public static byte[] SerializeMacroRecord(MacroDefinition macro)
    {
        var buf = new byte[MacroRecordSize];
        WriteUtf16String(buf.AsSpan(0, 48), macro.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x30, 4), Math.Max(1u, macro.RepeatCount));

        int count = Math.Min(macro.Actions.Count, MacroDefinition.MaxActions);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x34, 4), (uint)count);
        for (int i = 0; i < count; i++)
        {
            var act = macro.Actions[i];
            var aSpan = buf.AsSpan(0x38 + i * 4, 4);
            aSpan[0] = act.FwCode;
            aSpan[1] = (byte)act.State;
            BinaryPrimitives.WriteUInt16LittleEndian(aSpan.Slice(2, 2), act.DelayMs);
        }
        return buf;
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

    private static void WriteInt32(Span<byte> span, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), value);

    private static void WriteUInt32(Span<byte> span, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), value);

    private static string ReadUtf16String(ReadOnlySpan<byte> utf16Bytes)
    {
        string decoded = Encoding.Unicode.GetString(utf16Bytes);
        int nul = decoded.IndexOf('\0');
        return nul >= 0 ? decoded[..nul] : decoded;
    }

    private static void WriteUtf16String(Span<byte> target, string value)
    {
        target.Clear();
        if (string.IsNullOrEmpty(value))
            return;

        int maxChars = (target.Length / 2) - 1;
        if (maxChars <= 0)
            return;

        string trimmed = value.Length > maxChars ? value[..maxChars] : value;
        Encoding.Unicode.GetBytes(trimmed, target);
    }
}
