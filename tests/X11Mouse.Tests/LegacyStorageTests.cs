using System;
using System.IO;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;
using X11Mouse.Core.Storage;
using Xunit;

namespace X11Mouse.Tests;

public class LegacyStorageTests
{
    [Fact]
    public void SingleProfileAndProData_RoundTripPreservesAllFields()
    {
        var p1 = X11Protocol.CreateDefaultProfile("FPSMode");
        p1.PollingRate = PollingRate.Hz500;
        p1.ActiveDpiStage = 3;
        p1.DpiStages[3].DpiXRaw = 32;
        p1.DpiStages[3].Color = new RgbColor(12, 34, 56);
        p1.LightMode = LightMode.Breathing;
        p1.LightColor = new RgbColor(200, 10, 40);
        p1.KeyDebounceMs = 2;
        p1.LiftOfDistanceMm = 1;
        p1.RippleControl = true;
        p1.AngleSnap = true;
        p1.Buttons[6].ActionId = 0x10;
        p1.Buttons[6].ShortcutModifiers = ShortcutModifiers.Ctrl;
        p1.Buttons[6].ShortcutKeyFwCode = 0x06;
        p1.Buttons[7].AssignMacro(
            new MacroDefinition
            {
                Name = "Burst",
                RepeatCount = 2,
                Actions =
                [
                    new MacroAction { FwCode = KeyCodeTable.MouseLeftFw, State = MacroActionState.Down, DelayMs = 15 },
                    new MacroAction { FwCode = KeyCodeTable.MouseLeftFw, State = MacroActionState.Up, DelayMs = 25 },
                ],
            },
            libraryIndex: 0,
            cycleMode: MacroCycleMode.LoopCount,
            repeatCount: 2);

        byte[] rawProfile = LegacyBinaryFormat.SerializeProfileRecord(p1);
        Assert.Equal(LegacyBinaryFormat.ProfileRecordSize, rawProfile.Length);

        byte[] rawProData = LegacyBinaryFormat.SerializeProData(0, [p1]);
        Assert.Equal(LegacyBinaryFormat.ProDataHeaderSize + LegacyBinaryFormat.ProfileRecordSize, rawProData.Length);

        var bundle = LegacyBinaryFormat.ParseProData(rawProData);
        Assert.Equal(0, bundle.ActiveProfileIndex);
        Assert.Single(bundle.Profiles);

        var parsed = bundle.Profiles[0];
        Assert.Equal("FPSMode", parsed.Name);
        Assert.Equal(PollingRate.Hz500, parsed.PollingRate);
        Assert.Equal(3, parsed.ActiveDpiStage);
        Assert.Equal(32, parsed.DpiStages[3].DpiXRaw);
        Assert.Equal(new RgbColor(12, 34, 56), parsed.DpiStages[3].Color);
        Assert.Equal(LightMode.Breathing, parsed.LightMode);
        Assert.Equal(new RgbColor(200, 10, 40), parsed.LightColor);
        Assert.Equal(2, parsed.KeyDebounceMs);
        Assert.Equal(1, parsed.LiftOfDistanceMm);
        Assert.True(parsed.RippleControl);
        Assert.True(parsed.AngleSnap);
        Assert.Equal((byte)0x10, parsed.Buttons[6].ActionId);
        Assert.Equal(ShortcutModifiers.Ctrl, parsed.Buttons[6].ShortcutModifiers);
        Assert.Equal((byte)0x06, parsed.Buttons[6].ShortcutKeyFwCode);
        Assert.Equal((byte)0x11, parsed.Buttons[7].ActionId);
        Assert.Equal("Burst", parsed.Buttons[7].MacroName);
        Assert.Equal(2, parsed.Buttons[7].MacroActions.Count);
        Assert.Equal(KeyCodeTable.MouseLeftFw, parsed.Buttons[7].MacroActions[0].FwCode);
        Assert.Equal((ushort)25, parsed.Buttons[7].MacroActions[1].DelayMs);
    }

    [Fact]
    public void MacroBinaryFormat_RoundTripPreservesActions()
    {
        var macro = new MacroDefinition
        {
            Name = "JumpThrow",
            RepeatCount = 4,
            Actions =
            [
                new MacroAction { FwCode = 0x2C, State = MacroActionState.Down, DelayMs = 10 },
                new MacroAction { FwCode = 0x2C, State = MacroActionState.Up, DelayMs = 30 },
            ],
        };

        byte[] raw = LegacyBinaryFormat.SerializeMacros([macro]);
        Assert.Equal(LegacyBinaryFormat.MacroRecordSize, raw.Length);

        var list = LegacyBinaryFormat.ParseMacroStream(raw);
        Assert.Single(list);
        Assert.Equal("JumpThrow", list[0].Name);
        Assert.Equal(4u, list[0].RepeatCount);
        Assert.Equal(2, list[0].Actions.Count);
        Assert.Equal((byte)0x2C, list[0].Actions[0].FwCode);
        Assert.Equal(MacroActionState.Up, list[0].Actions[1].State);
        Assert.Equal((ushort)30, list[0].Actions[1].DelayMs);
    }

    [Fact]
    public void ReadOnlyOfficialAppDataFiles_IfPresent_ParseWithoutError()
    {
        string officialDir = ProfileRepository.OfficialAppDataDirectory;
        string proPath = Path.Combine(officialDir, "pro.data");
        string macroPath = Path.Combine(officialDir, "macro.data");

        if (File.Exists(proPath))
        {
            var bundle = LegacyBinaryFormat.ReadProData(proPath);
            Assert.NotEmpty(bundle.Profiles);
            Assert.Contains(bundle.Profiles, p => p.Name == "Profile1");
            Assert.All(bundle.Profiles, p => Assert.Equal(MouseProfile.ButtonSlotCount, p.Buttons.Length));
        }

        if (File.Exists(macroPath))
        {
            var macros = LegacyBinaryFormat.ReadMacroFile(macroPath);
            Assert.NotEmpty(macros);
        }
    }

    [Fact]
    public void ProfileRepository_SavesAndLoadsIsolatedJsonStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "x11clean_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new ProfileRepository(tempDir);
            var store = new AppConfigStore
            {
                ActiveProfileIndex = 0,
                AutoApplyOnChange = true,
                Profiles = [X11Protocol.CreateDefaultProfile("CleanProfile")],
                Macros = [new MacroDefinition { Name = "CleanMacro", RepeatCount = 1 }],
            };

            repo.Save(store);
            var reloaded = repo.LoadOrInitialize();

            Assert.True(reloaded.AutoApplyOnChange);
            Assert.Equal("CleanProfile", reloaded.Profiles[0].Name);
            Assert.Equal("CleanMacro", reloaded.Macros[0].Name);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
