using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;
using X11Mouse.Core.Services;
using Xunit;

namespace X11Mouse.Tests;

internal sealed class FakeHidTransport : IHidTransport
{
    public ConnectionMode CurrentMode { get; private set; }
    public HidEndpointInfo? ActiveMonitorEndpoint { get; private set; }
    public HidEndpointInfo? ActiveFeatureEndpoint { get; private set; }

    public List<byte[]> SentReports { get; } = new();
    public bool AutoSendAckOnWireless { get; set; }

    public event Action<ConnectionMode, HidEndpointInfo?>? ConnectionChanged;
    public event Action<byte[]>? MonitorReportReceived;

    public IReadOnlyList<HidEndpointInfo> EnumerateX11Endpoints() => Array.Empty<HidEndpointInfo>();
    public bool ConnectBestAvailable() => CurrentMode != ConnectionMode.Disconnected;
    public void StartHotPlugMonitor(TimeSpan? pollInterval = null) { }
    public void StopHotPlugMonitor() { }
    public void Disconnect() => SimulateConnect(ConnectionMode.Disconnected);

    public void SimulateConnect(ConnectionMode mode)
    {
        CurrentMode = mode;
        if (mode == ConnectionMode.Disconnected)
        {
            ActiveMonitorEndpoint = null;
            ActiveFeatureEndpoint = null;
            ConnectionChanged?.Invoke(mode, null);
            return;
        }

        ushort pid = mode == ConnectionMode.Wired ? X11Protocol.WiredProductId : X11Protocol.Wireless24GProductId;
        ActiveMonitorEndpoint = new HidEndpointInfo("mon", X11Protocol.WiredVendorId, pid, 0x1108, 0x0A, 0, 5, 0, 0, "X11", true);
        ActiveFeatureEndpoint = new HidEndpointInfo("feat", X11Protocol.WiredVendorId, pid, 0x1108, 0x0B, 0, 0, 0, 134, "X11", true);
        ConnectionChanged?.Invoke(mode, ActiveFeatureEndpoint);
    }

    public void InjectMonitorReport(byte[] report) => MonitorReportReceived?.Invoke(report);

    public bool SendFeatureReport(byte[] payload)
    {
        SentReports.Add(payload);
        if (AutoSendAckOnWireless && CurrentMode == ConnectionMode.Wireless24G)
        {
            // Simulate hardware ACK 0x50 for either single packet or final macro chunk (byte[1]==0x0C)
            if (payload[0] != 0x09 || payload[1] == 0x0C)
            {
                _ = Task.Run(() => MonitorReportReceived?.Invoke(new byte[] { 0x03, 0x55, 0x50, 0x00, 0x00 }));
            }
        }
        return true;
    }

    public void Dispose() { }
}

public class MouseDeviceServiceTests
{
    [Fact]
    public async Task ApplyProfile_DifferentialOnlySendsChangedPackets()
    {
        using var transport = new FakeHidTransport();
        using var service = new MouseDeviceService(transport)
        {
            WiredInterPacketDelayMs = 0,
        };
        transport.SimulateConnect(ConnectionMode.Wired);

        var profile = X11Protocol.CreateDefaultProfile();
        service.SeedCommittedProfile(profile);

        // Change ONLY polling rate -> should emit only Report 0x06
        profile.PollingRate = PollingRate.Hz250;
        bool ok = await service.ApplyProfileAsync(profile);
        Assert.True(ok);
        Assert.Single(transport.SentReports);
        Assert.Equal(0x06, transport.SentReports[0][0]);

        transport.SentReports.Clear();

        // Change ONLY lighting mode -> should emit only Report 0x05
        profile.LightMode = LightMode.Static;
        ok = await service.ApplyProfileAsync(profile);
        Assert.True(ok);
        Assert.Single(transport.SentReports);
        Assert.Equal(0x05, transport.SentReports[0][0]);
    }

    [Fact]
    public async Task RestoreResetHeader_SendsResetAndAllStandardReportsInOrder()
    {
        using var transport = new FakeHidTransport();
        using var service = new MouseDeviceService(transport)
        {
            WiredInterPacketDelayMs = 0,
        };
        transport.SimulateConnect(ConnectionMode.Wired);

        var profile = X11Protocol.CreateDefaultProfile();
        bool ok = await service.ApplyProfileAsync(profile, forceAll: true, sendResetHeader: true);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0x0C, 0x04, 0x05, 0x06, 0x08 },
            transport.SentReports.ConvertAll(r => r[0]).ToArray());
    }

    [Fact]
    public async Task Wireless24G_AwaitsAck0x50AndHandlesSleepWatchdog()
    {
        using var transport = new FakeHidTransport { AutoSendAckOnWireless = true };
        using var service = new MouseDeviceService(transport)
        {
            SleepWatchdogTimeout = TimeSpan.FromMilliseconds(120),
            WirelessRetryCount = 2,
            WirelessRetryIntervalMs = 500,
        };

        transport.SimulateConnect(ConnectionMode.Wireless24G);
        Assert.True(service.Status.IsAwake);
        Assert.False(service.Status.HasBatteryReading);
        Assert.Null(service.Status.LastBatteryUpdateUtc);

        var profile = X11Protocol.CreateDefaultProfile();
        service.SeedCommittedProfile(profile);
        profile.KeyDebounceMs = 10;

        bool ok = await service.ApplyProfileAsync(profile);
        Assert.True(ok);
        Assert.Single(transport.SentReports);
        Assert.Equal(0x05, transport.SentReports[0][0]);

        // Wait past watchdog timeout so mouse enters sleep
        await Task.Delay(220);
        Assert.False(service.Status.IsAwake);

        // Heartbeat Report 0x03 wakes mouse & updates battery
        transport.InjectMonitorReport(new byte[] { 0x03, 0x55, 0x40, 0x01, 0x58 });
        Assert.True(service.Status.IsAwake);
        Assert.True(service.Status.HasBatteryReading);
        Assert.NotNull(service.Status.LastBatteryUpdateUtc);
        Assert.Equal(88, service.Status.BatteryPercent);

        // Sleeping preserves last known reading for tray display
        await Task.Delay(220);
        Assert.False(service.Status.IsAwake);
        Assert.True(service.Status.HasBatteryReading);
        Assert.Equal(88, service.Status.BatteryPercent);

        // Disconnecting clears telemetry reading
        transport.Disconnect();
        Assert.False(service.Status.HasBatteryReading);
        Assert.Null(service.Status.LastBatteryUpdateUtc);
    }

    [Fact]
    public void MonitorEvents_PropagateHardwareDpiAndPollingRateChanges()
    {
        using var transport = new FakeHidTransport();
        using var service = new MouseDeviceService(transport);
        transport.SimulateConnect(ConnectionMode.Wireless24G);

        int? reportedStage = null;
        PollingRate? reportedRate = null;
        service.DpiStageChangedByMouse += s => reportedStage = s;
        service.PollingRateChangedByMouse += r => reportedRate = r;

        transport.InjectMonitorReport(new byte[] { 0x03, 0x55, 0x10, 0x05, 0x00 }); // 1-based stage 5 -> idx 4
        transport.InjectMonitorReport(new byte[] { 0x03, 0x55, 0x20, 0x02, 0x00 }); // 500 Hz

        Assert.Equal(4, reportedStage);
        Assert.Equal(PollingRate.Hz500, reportedRate);
    }
}
