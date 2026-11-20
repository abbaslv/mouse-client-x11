using System;
using System.Collections.Generic;
using System.Text;
using X11Mouse.Core.Models;

namespace X11Mouse.Core.Protocol;

/// <summary>
/// Mapping entry between Attack Shark firmware HID usage code (`FwCode`), Win32 Virtual-Key, and PS/2 scan code,
/// extracted from `Hook.dll` (`0x1001a180..0x1001aaf0`).
/// </summary>
public readonly record struct KeyCodeEntry(byte FwCode, byte VkCode, byte ScanCode, bool IsExtended, string DisplayName);

/// <summary>
/// Complete 151-entry keyboard code table from `Hook.dll` plus Attack Shark macro mouse-button codes (`0xF1..0xF5`).
/// </summary>
public static class KeyCodeTable
{
    public const byte MouseLeftFw = 0xF1;
    public const byte MouseRightFw = 0xF2;
    public const byte MouseMiddleFw = 0xF3;
    public const byte MouseX2ForwardFw = 0xF4;
    public const byte MouseX1BackwardFw = 0xF5;

    /// <summary>
    /// Raw 151 tuples `[fwCode, vkCode, scanCode, extFlag (0 or 224)]` matching `Hook.dll`.
    /// </summary>
    private static readonly (byte Fw, byte Vk, byte Scan, byte Ext, string Name)[] RawEntries =
    [
        (4, 65, 30, 0, "A"),
        (5, 66, 48, 0, "B"),
        (6, 67, 46, 0, "C"),
        (7, 68, 32, 0, "D"),
        (8, 69, 18, 0, "E"),
        (9, 70, 33, 0, "F"),
        (10, 71, 34, 0, "G"),
        (11, 72, 35, 0, "H"),
        (12, 73, 23, 0, "I"),
        (13, 74, 36, 0, "J"),
        (14, 75, 37, 0, "K"),
        (15, 76, 38, 0, "L"),
        (16, 77, 50, 0, "M"),
        (17, 78, 49, 0, "N"),
        (18, 79, 24, 0, "O"),
        (19, 80, 25, 0, "P"),
        (20, 81, 16, 0, "Q"),
        (21, 82, 19, 0, "R"),
        (22, 83, 31, 0, "S"),
        (23, 84, 20, 0, "T"),
        (24, 85, 22, 0, "U"),
        (25, 86, 47, 0, "V"),
        (26, 87, 17, 0, "W"),
        (27, 88, 45, 0, "X"),
        (28, 89, 21, 0, "Y"),
        (29, 90, 44, 0, "Z"),
        (30, 49, 2, 0, "1"),
        (31, 50, 3, 0, "2"),
        (32, 51, 4, 0, "3"),
        (33, 52, 5, 0, "4"),
        (34, 53, 6, 0, "5"),
        (35, 54, 7, 0, "6"),
        (36, 55, 8, 0, "7"),
        (37, 56, 9, 0, "8"),
        (38, 57, 10, 0, "9"),
        (39, 48, 11, 0, "0"),
        (40, 13, 28, 0, "Enter"),
        (41, 27, 1, 0, "Esc"),
        (42, 8, 14, 0, "Backspace"),
        (43, 9, 15, 0, "Tab"),
        (44, 32, 57, 0, "Space"),
        (45, 189, 12, 0, "-"),
        (46, 187, 13, 0, "="),
        (47, 219, 26, 0, "["),
        (48, 221, 27, 0, "]"),
        (49, 220, 43, 0, "\\"),
        (50, 220, 43, 0, "\\#"),
        (51, 186, 39, 0, ";"),
        (52, 222, 40, 0, "'"),
        (53, 192, 41, 0, "`"),
        (54, 188, 51, 0, ","),
        (55, 190, 52, 0, "."),
        (56, 191, 53, 0, "/"),
        (57, 20, 58, 0, "Caps Lock"),
        (58, 112, 59, 0, "F1"),
        (59, 113, 60, 0, "F2"),
        (60, 114, 61, 0, "F3"),
        (61, 115, 62, 0, "F4"),
        (62, 116, 63, 0, "F5"),
        (63, 117, 64, 0, "F6"),
        (64, 118, 65, 0, "F7"),
        (65, 119, 66, 0, "F8"),
        (66, 120, 67, 0, "F9"),
        (67, 121, 68, 0, "F10"),
        (68, 122, 87, 0, "F11"),
        (69, 123, 88, 0, "F12"),
        (70, 44, 84, 0, "Print Screen"),
        (71, 145, 70, 0, "Scroll Lock"),
        (85, 106, 55, 0, "Num *"),
        (86, 109, 74, 0, "Num -"),
        (87, 107, 78, 0, "Num +"),
        (89, 97, 79, 0, "Num 1"),
        (90, 98, 80, 0, "Num 2"),
        (91, 99, 81, 0, "Num 3"),
        (92, 100, 75, 0, "Num 4"),
        (93, 101, 76, 0, "Num 5"),
        (94, 102, 77, 0, "Num 6"),
        (95, 103, 71, 0, "Num 7"),
        (96, 104, 72, 0, "Num 8"),
        (97, 105, 73, 0, "Num 9"),
        (98, 96, 82, 0, "Num 0"),
        (99, 110, 83, 0, "Num ."),
        (100, 0, 0, 0, "Non-US \\"),
        (103, 0, 0, 0, "Num ="),
        (104, 124, 100, 0, "F13"),
        (105, 125, 101, 0, "F14"),
        (106, 126, 102, 0, "F15"),
        (107, 127, 103, 0, "F16"),
        (108, 128, 104, 0, "F17"),
        (109, 129, 105, 0, "F18"),
        (110, 130, 106, 0, "F19"),
        (111, 131, 107, 0, "F20"),
        (112, 132, 108, 0, "F21"),
        (113, 133, 109, 0, "F22"),
        (114, 134, 110, 0, "F23"),
        (115, 135, 118, 0, "F24"),
        (116, 0, 0, 0, "Execute"),
        (117, 0, 0, 0, "Help"),
        (118, 0, 0, 0, "Menu"),
        (119, 0, 0, 0, "Select"),
        (120, 0, 0, 0, "Stop"),
        (121, 0, 0, 0, "Again"),
        (122, 0, 0, 0, "Undo"),
        (123, 0, 0, 0, "Cut"),
        (124, 0, 0, 0, "Copy"),
        (125, 0, 0, 0, "Paste"),
        (126, 0, 0, 0, "Find"),
        (127, 0, 0, 0, "Mute"),
        (128, 0, 0, 0, "Volume Up"),
        (129, 0, 0, 0, "Volume Down"),
        (130, 0, 0, 0, "Locking Caps"),
        (131, 0, 0, 0, "Locking Num"),
        (132, 0, 0, 0, "Locking Scroll"),
        (133, 0, 0, 0, "Num ,"),
        (134, 0, 0, 0, "Num = AS/400"),
        (140, 0, 0, 0, "Intl 6"),
        (141, 0, 0, 0, "Intl 7"),
        (142, 0, 0, 0, "Intl 8"),
        (143, 0, 0, 0, "Intl 9"),
        (224, 162, 29, 0, "L-Ctrl"),
        (225, 160, 42, 0, "L-Shift"),
        (226, 164, 56, 0, "L-Alt"),
        (229, 161, 54, 0, "R-Shift"),
        (156, 12, 76, 0, "Clear"),
        (88, 108, 117, 0, "Num Enter"),
        (228, 163, 29, 224, "R-Ctrl"),
        (84, 111, 53, 224, "Num /"),
        (230, 165, 56, 224, "R-Alt"),
        (74, 36, 71, 224, "Home"),
        (82, 38, 72, 224, "Up"),
        (75, 33, 73, 224, "Page Up"),
        (80, 37, 75, 224, "Left"),
        (79, 39, 77, 224, "Right"),
        (77, 35, 79, 224, "End"),
        (81, 40, 80, 224, "Down"),
        (78, 34, 81, 224, "Page Down"),
        (73, 45, 82, 224, "Insert"),
        (76, 46, 83, 224, "Delete"),
        (227, 91, 91, 224, "L-Win"),
        (231, 92, 92, 224, "R-Win"),
        (101, 93, 93, 224, "Apps"),
        (102, 0, 94, 224, "Power"),
        (83, 144, 69, 0, "Num Lock"),
        (72, 19, 69, 0, "Pause"),
        (135, 153, 0, 0, "Intl 1"),
        (136, 136, 0, 0, "Intl 2"),
        (137, 60, 0, 0, "Intl 3"),
        (138, 151, 0, 0, "Intl 4"),
        (139, 152, 0, 0, "Intl 5"),
        (53, 243, 65, 0, "Half/Full"),
        (57, 240, 58, 0, "Alphanumeric"),
    ];

    public static IReadOnlyList<KeyCodeEntry> Entries { get; }

    private static readonly Dictionary<byte, KeyCodeEntry> ByFw = new();
    private static readonly Dictionary<int, KeyCodeEntry> ByVk = new();

    static KeyCodeTable()
    {
        var list = new List<KeyCodeEntry>(RawEntries.Length);
        foreach (var (fw, vk, scan, ext, name) in RawEntries)
        {
            var entry = new KeyCodeEntry(fw, vk, scan, ext != 0, name);
            list.Add(entry);
            ByFw.TryAdd(fw, entry);
            if (vk != 0)
            {
                // Preserve first occurrence in table order to match Hook.dll linear search.
                ByVk.TryAdd((vk << 8) | (ext != 0 ? 1 : 0), entry);
                ByVk.TryAdd(vk, entry);
            }
        }
        Entries = list;
    }

    public static byte VkToFwCode(int vkCode, bool isExtended = false)
    {
        // Generic Shift/Control/Menu fallback for shortcut capture.
        vkCode = vkCode switch
        {
            0x10 => 0xA0, // VK_SHIFT -> VK_LSHIFT
            0x11 => 0xA2, // VK_CONTROL -> VK_LCONTROL
            0x12 => 0xA4, // VK_MENU -> VK_LMENU
            _ => vkCode
        };

        if (isExtended && ByVk.TryGetValue((vkCode << 8) | 1, out var extMatch))
            return extMatch.FwCode;

        if (ByVk.TryGetValue(vkCode, out var match))
            return match.FwCode;

        return 0;
    }

    public static byte FwCodeToVk(byte fwCode) =>
        ByFw.TryGetValue(fwCode, out var entry) ? entry.VkCode : (byte)0;

    public static string GetDisplayName(byte fwCode)
    {
        return fwCode switch
        {
            MouseLeftFw => "Left Click",
            MouseRightFw => "Right Click",
            MouseMiddleFw => "Middle Click",
            MouseX2ForwardFw => "Forward (X2)",
            MouseX1BackwardFw => "Backward (X1)",
            0 => "None",
            _ => ByFw.TryGetValue(fwCode, out var entry) ? entry.DisplayName : $"Key 0x{fwCode:X2}",
        };
    }

    public static string FormatShortcut(ShortcutModifiers modifiers, byte fwCode)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ShortcutModifiers.Ctrl)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");
        if (fwCode != 0) parts.Add(GetDisplayName(fwCode));
        return parts.Count == 0 ? "None" : string.Join(" + ", parts);
    }
}
