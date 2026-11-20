using System;
using System.Linq;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;
using Xunit;

namespace X11Mouse.Tests;

public class ProtocolTests
{
    private static ushort ReadBigEndianU16(byte[] buf, int offset) =>
        (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static ushort SumRange(byte[] buf, int start, int endExclusive)
    {
        uint sum = 0;
        for (int i = start; i < endExclusive; i++)
            sum += buf[i];
        return (ushort)(sum & 0xFFFF);
    }

    [Fact]
    public void ResetPacket_MatchesExactHardwareHeader()
    {
        byte[] pkt = X11Protocol.BuildResetPacket();
        Assert.Equal(new byte[] { 0x0C, 0x0A, 0x01, 0xFE, 0x01, 0xFE, 0x00, 0x00, 0x00, 0x00 }, pkt);
    }

    [Fact]
    public void DpiPacket_EncodesAllFourDpiRangesAndChecksum()
    {
        var profile = X11Protocol.CreateDefaultProfile();
        profile.AngleSnap = true;
        profile.RippleControl = true;
        profile.ActiveDpiStage = 2; // 0-based -> 3 on wire

        // Stage 0: <= 100 (800 DPI -> raw 8 -> idx 15 -> 18, flag 0, highBit 0)
        profile.DpiStages[0].DpiXRaw = 8;
        // Stage 1: 101..120 (11000 DPI -> raw 110 -> idx 209 -> 129, flag 1, highBit 0)
        profile.DpiStages[1].DpiXRaw = 110;
        // Stage 2: 121..200 (15000 DPI -> raw 150 -> idx 149 -> 176, flag 1, highBit 1)
        profile.DpiStages[2].DpiXRaw = 150;
        // Stage 3: 201..220 (22000 DPI -> raw 220 -> idx 209 -> 129, flag 1, highBit 1)
        profile.DpiStages[3].DpiXRaw = 220;

        byte[] pkt = X11Protocol.BuildDpiPacket(profile);

        Assert.Equal(56, pkt.Length);
        Assert.Equal(new byte[] { 0x04, 0x38, 0x01, 0x01, 0x01 }, pkt[..5]);
        Assert.Equal(0b0011_1111, pkt[5]); // default 6 stages enabled
        Assert.Equal(0b0010_1100, pkt[6]); // stages 2,3 and default stage 5 (raw 220) have raw > 120
        Assert.Equal(pkt[6], pkt[7]);

        Assert.Equal(X11Protocol.DpiRegisterTable[15], pkt[8]);
        Assert.Equal(0, pkt[16]);

        Assert.Equal(X11Protocol.DpiRegisterTable[209], pkt[9]);
        Assert.Equal(1, pkt[17]);

        Assert.Equal(X11Protocol.DpiRegisterTable[149], pkt[10]);
        Assert.Equal(1, pkt[18]);

        Assert.Equal(X11Protocol.DpiRegisterTable[209], pkt[11]);
        Assert.Equal(1, pkt[19]);

        Assert.Equal(3, pkt[24]); // ActiveDpiStage + 1
        Assert.Equal(255, pkt[25]); // stage 0 R=255
        Assert.Equal(0, pkt[26]);
        Assert.Equal(0, pkt[27]);
        Assert.Equal(0x02, pkt[49]);
        Assert.Equal(SumRange(pkt, 3, 50), ReadBigEndianU16(pkt, 50));
    }

    [Fact]
    public void LightAndPowerPacket_EncodesSplitNibblesAndModeBrightness()
    {
        var profile = X11Protocol.CreateDefaultProfile();
        profile.LightMode = LightMode.Static; // mode 1 uses actual brightness
        profile.LightBrightness = 5;
        profile.LightBreathSpeed = 7; // 9 - 7 = 2
        profile.SleepTimerMin = 0x2A; // 42 -> high nibble 0x20, low nibble 0x0A
        profile.LightOffTimerMin = 15;
        profile.KeyDebounceMs = 8;
        profile.LightColor = new RgbColor(0x12, 0x34, 0x56);

        byte[] staticPkt = X11Protocol.BuildLightAndPowerPacket(profile);
        Assert.Equal(15, staticPkt.Length);
        Assert.Equal(new byte[] { 0x05, 0x0F, 0x01 }, staticPkt[..3]);
        Assert.Equal(0x10, staticPkt[3]);
        Assert.Equal(0x22, staticPkt[4]); // (0x20) | (9-7)
        Assert.Equal(0xA5, staticPkt[5]); // (0x0A << 4) | 5
        Assert.Equal(0x12, staticPkt[6]);
        Assert.Equal(0x34, staticPkt[7]);
        Assert.Equal(0x56, staticPkt[8]);
        Assert.Equal(15, staticPkt[9]);
        Assert.Equal(8, staticPkt[10]);
        Assert.Equal(SumRange(staticPkt, 3, 11), ReadBigEndianU16(staticPkt, 11));

        // Neon mode (3) uses 0x08 in low nibble of byte 5
        profile.LightMode = LightMode.Neon;
        byte[] neonPkt = X11Protocol.BuildLightAndPowerPacket(profile);
        Assert.Equal(0xA8, neonPkt[5]);
        Assert.Equal(SumRange(neonPkt, 3, 11), ReadBigEndianU16(neonPkt, 11));
    }

    [Theory]
    [InlineData(PollingRate.Hz125, 0x08, 0xF7)]
    [InlineData(PollingRate.Hz250, 0x04, 0xFB)]
    [InlineData(PollingRate.Hz500, 0x02, 0xFD)]
    [InlineData(PollingRate.Hz1000, 0x01, 0xFE)]
    public void PollingRatePacket_EncodesRateAndComplement(PollingRate rate, byte expectedCode, byte expectedNot)
    {
        byte[] pkt = X11Protocol.BuildPollingRatePacket(rate);
        Assert.Equal(new byte[] { 0x06, 0x09, 0x01, expectedCode, expectedNot, 0x00, 0x00, 0x00, 0x00 }, pkt);
    }

    [Fact]
    public void ButtonsPacket_EncodesStandardShortcutAndMacroSlots()
    {
        var profile = X11Protocol.CreateDefaultProfile();
        profile.Buttons[6].ActionId = 0x10; // Forward slot -> Shortcut Ctrl+Shift+C (0x06)
        profile.Buttons[6].ShortcutModifiers = ShortcutModifiers.Ctrl | ShortcutModifiers.Shift;
        profile.Buttons[6].ShortcutKeyFwCode = 0x06;

        profile.Buttons[7].ActionId = 0x11; // Backward slot -> Macro on slot 8 (1-based)

        byte[] pkt = X11Protocol.BuildButtonsPacket(profile);
        Assert.Equal(59, pkt.Length);
        Assert.Equal(new byte[] { 0x08, 0x3B, 0x01 }, pkt[..3]);

        // Slot 0 = Left Click -> (0x02, 0x00, 0x00)
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, pkt[3..6]);
        // Slot 6 = offset 3 + 18 = 21
        Assert.Equal(new byte[] { 0x11, 0x03, 0x06 }, pkt[21..24]);
        // Slot 7 = offset 3 + 21 = 24
        Assert.Equal(new byte[] { 0x12, 0x00, 0x08 }, pkt[24..27]);

        Assert.Equal(SumRange(pkt, 3, 57), ReadBigEndianU16(pkt, 57));
    }

    [Fact]
    public void MacroPacket_EncodesShortAndLongDelaysAndSplitsChunksForWiredVs24G()
    {
        var btn = new ButtonConfig
        {
            SlotIndex = 6,
            ActionId = 0x11,
            MacroCycleMode = MacroCycleMode.HoldWhilePressed,
            MacroRepeatCount = 3,
            MacroActions =
            [
                new MacroAction { FwCode = 0x04, State = MacroActionState.Down, DelayMs = 30 },   // 3 | 0x01 = 0x03
                new MacroAction { FwCode = 0x04, State = MacroActionState.Up, DelayMs = 2550 },   // 12*200 + 150 -> (15|0x81=0x8F, 0x04) + (12, 0x03)
            ]
        };

        byte[] logical = X11Protocol.BuildMacroLogicalPacket(7, btn);
        Assert.Equal(131, logical.Length);
        Assert.Equal(new byte[] { 0x09, 0x83, 0x07, 0x02 }, logical[..4]);
        Assert.Equal(3, logical[7]);
        Assert.Equal(3, logical[28]); // 2 original + 1 extra 200ms chunk pair

        // Pair 0: 30ms Down A
        Assert.Equal(0x03, logical[29]);
        Assert.Equal(0x04, logical[30]);
        // Pair 1: 150ms rem Up A
        Assert.Equal(0x8F, logical[31]);
        Assert.Equal(0x04, logical[32]);
        // Pair 2: 12 * 200ms extension marker
        Assert.Equal(12, logical[33]);
        Assert.Equal(0x03, logical[34]);

        Assert.Equal(SumRange(logical, 3, 129), ReadBigEndianU16(logical, 129));

        byte[][] wiredChunks = X11Protocol.SplitMacroChunks(logical, isWireless24G: false);
        byte[][] wirelessChunks = X11Protocol.SplitMacroChunks(logical, isWireless24G: true);

        Assert.Equal(3, wiredChunks.Length);
        Assert.All(wiredChunks, c => Assert.Equal(64, c.Length));
        Assert.Equal(0x40, wiredChunks[2][1]);
        Assert.Equal(0x0C, wirelessChunks[2][1]);
        Assert.Equal((byte)2, wirelessChunks[2][3]);
        Assert.Equal(logical[129], wirelessChunks[2][10]); // 4 + (129 - 123) = 10
        Assert.Equal(logical[130], wirelessChunks[2][11]);
    }

    [Fact]
    public void ParseMonitorReport_ParsesLiveBatteryDpiAndAck()
    {
        // Exact packet observed during live read-only probe of VID_1D57&PID_FA60
        var bat = X11Protocol.ParseMonitorReport(new byte[] { 0x03, 0x55, 0x40, 0x01, 0x64 });
        Assert.NotNull(bat);
        Assert.Equal(MonitorEventType.BatteryOrHeartbeat, bat!.Value.Type);
        Assert.Equal(1, bat.Value.ArgLow);
        Assert.Equal(100, bat.Value.ArgHigh);

        var dpi = X11Protocol.ParseMonitorReport(new byte[] { 0x03, 0x55, 0x10, 0x04, 0x00 });
        Assert.NotNull(dpi);
        Assert.Equal(MonitorEventType.DpiChanged, dpi!.Value.Type);
        Assert.Equal(4, dpi.Value.Arg16);

        var ack = X11Protocol.ParseMonitorReport(new byte[] { 0x03, 0x55, 0x50, 0x00, 0x00 });
        Assert.NotNull(ack);
        Assert.Equal(MonitorEventType.WirelessAck, ack!.Value.Type);
        Assert.Equal(0, ack.Value.ArgLow);

        Assert.Null(X11Protocol.ParseMonitorReport(new byte[] { 0x03, 0xAA, 0x40, 0x01, 0x64 }));
    }

    [Fact]
    public void KeyCodeTable_HasAll151EntriesAndRoundTrips()
    {
        Assert.Equal(151, KeyCodeTable.Entries.Count);
        Assert.Equal((byte)0x04, KeyCodeTable.VkToFwCode(65)); // 'A'
        Assert.Equal((byte)65, KeyCodeTable.FwCodeToVk(0x04));
        Assert.Equal((byte)76, KeyCodeTable.VkToFwCode(46, isExtended: true)); // Delete
        Assert.Equal("Left Click", KeyCodeTable.GetDisplayName(KeyCodeTable.MouseLeftFw));
        Assert.Equal("Ctrl + Shift + C", KeyCodeTable.FormatShortcut(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, 0x06));
    }
}
