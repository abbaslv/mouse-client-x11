using System;
using System.Threading;
using System.Threading.Tasks;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.Core.Services;

/// <summary>
/// Coordinates Attack Shark X11 HID communication, asynchronous Report `0x03` state updates,
/// 2.4 GHz sleep watchdog timer (6000 ms), `0x50` ACK retry loop, and differential profile uploads.
/// </summary>
public sealed class MouseDeviceService : IDisposable
{
    private readonly IHidTransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Timer _sleepWatchdogTimer;
    private readonly object _ackSync = new();
    private TaskCompletionSource<bool>? _ackWaiter;

    private MouseProfile? _lastCommittedProfile;
    private bool _disposed;

    public DeviceStatus Status { get; } = new();

    public TimeSpan SleepWatchdogTimeout { get; set; } = TimeSpan.FromMilliseconds(6000);
    public int WirelessRetryCount { get; set; } = 10;
    public int WirelessRetryIntervalMs { get; set; } = 500;
    public int WirelessMacroIntervalMs { get; set; } = 1000;
    public int WiredInterPacketDelayMs { get; set; } = 40;

    public event Action<DeviceStatus>? StatusChanged;
    public event Action<int>? DpiStageChangedByMouse;
    public event Action<PollingRate>? PollingRateChangedByMouse;
    public event Action<LightMode>? LightModeChangedByMouse;
    public event Action<int>? ProfileChangedByMouse;
    public event Action<bool, string?>? ApplyStateChanged;

    public MouseDeviceService(IHidTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.ConnectionChanged += OnTransportConnectionChanged;
        _transport.MonitorReportReceived += OnMonitorReportReceived;
        _sleepWatchdogTimer = new Timer(_ => OnSleepWatchdogElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        _transport.StartHotPlugMonitor();
    }

    public void RefreshConnection()
    {
        _transport.ConnectBestAvailable();
    }

    public void SeedCommittedProfile(MouseProfile profile)
    {
        _lastCommittedProfile = profile.Clone();
    }

    public async Task<bool> ApplyProfileAsync(
        MouseProfile profile,
        bool forceAll = false,
        bool sendResetHeader = false,
        CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        ApplyStateChanged?.Invoke(true, null);

        try
        {
            var mode = _transport.CurrentMode;
            if (mode == ConnectionMode.Disconnected)
            {
                ApplyStateChanged?.Invoke(false, "Mouse is disconnected.");
                return false;
            }

            if (mode == ConnectionMode.Wireless24G && !Status.IsAwake)
            {
                ApplyStateChanged?.Invoke(false, "2.4G wireless mouse is asleep. Move or click the mouse to wake it.");
                return false;
            }

            var diff = X11Protocol.ComputeDiff(profile, _lastCommittedProfile, forceAll || sendResetHeader);
            if (!sendResetHeader && !diff.AnyDirty)
            {
                ApplyStateChanged?.Invoke(false, null);
                return true;
            }

            bool ok = true;
            if (sendResetHeader)
                ok &= await SendPacketAsync(X11Protocol.BuildResetPacket(), isMacro: false, ct).ConfigureAwait(false);

            if (ok && diff.DpiOrSensorDirty)
                ok &= await SendPacketAsync(X11Protocol.BuildDpiPacket(profile), isMacro: false, ct).ConfigureAwait(false);

            if (ok && diff.LightOrPowerDirty)
                ok &= await SendPacketAsync(X11Protocol.BuildLightAndPowerPacket(profile), isMacro: false, ct).ConfigureAwait(false);

            if (ok && diff.PollingRateDirty)
                ok &= await SendPacketAsync(X11Protocol.BuildPollingRatePacket(profile.PollingRate), isMacro: false, ct).ConfigureAwait(false);

            if (ok && diff.ButtonsDirty)
                ok &= await SendPacketAsync(X11Protocol.BuildButtonsPacket(profile), isMacro: false, ct).ConfigureAwait(false);

            if (ok)
            {
                bool isWireless = mode == ConnectionMode.Wireless24G;
                for (int i = 0; i < MouseProfile.ButtonSlotCount && ok; i++)
                {
                    if (!diff.MacroSlotDirty[i] || profile.Buttons[i].ActionId != 0x11)
                        continue;

                    byte[] logical = X11Protocol.BuildMacroLogicalPacket(i + 1, profile.Buttons[i]);
                    byte[][] chunks = X11Protocol.SplitMacroChunks(logical, isWireless);
                    ok &= await SendMacroChunksAsync(chunks, isWireless, ct).ConfigureAwait(false);
                }
            }

            if (ok)
            {
                _lastCommittedProfile = profile.Clone();
                ApplyStateChanged?.Invoke(false, null);
            }
            else
            {
                ApplyStateChanged?.Invoke(false, "Failed to transfer configuration to device.");
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            ApplyStateChanged?.Invoke(false, "Apply cancelled.");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<bool> SendPacketAsync(byte[] packet, bool isMacro, CancellationToken ct)
    {
        var mode = _transport.CurrentMode;
        if (mode == ConnectionMode.Wired)
        {
            bool sent = _transport.SendFeatureReport(packet);
            if (sent && WiredInterPacketDelayMs > 0)
                await Task.Delay(WiredInterPacketDelayMs, ct).ConfigureAwait(false);
            return sent;
        }

        if (mode != ConnectionMode.Wireless24G)
            return false;

        int intervalMs = isMacro ? WirelessMacroIntervalMs : WirelessRetryIntervalMs;
        for (int attempt = 0; attempt < WirelessRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Status.IsAwake)
                return false;

            var waiter = CreateAckWaiter();
            bool sent = _transport.SendFeatureReport(packet);
            if (!sent && intervalMs > 0)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                continue;
            }

            var delayTask = intervalMs > 0 ? Task.Delay(intervalMs, ct) : Task.CompletedTask;
            var finished = await Task.WhenAny(waiter.Task, delayTask).ConfigureAwait(false);
            if (finished == waiter.Task && await waiter.Task.ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private async Task<bool> SendMacroChunksAsync(byte[][] chunks, bool isWireless, CancellationToken ct)
    {
        if (!isWireless)
        {
            foreach (var chunk in chunks)
            {
                if (!_transport.SendFeatureReport(chunk))
                    return false;
                if (WiredInterPacketDelayMs > 0)
                    await Task.Delay(Math.Max(WiredInterPacketDelayMs, 50), ct).ConfigureAwait(false);
            }
            return true;
        }

        for (int attempt = 0; attempt < WirelessRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Status.IsAwake)
                return false;

            var waiter = CreateAckWaiter();
            for (int k = 0; k < chunks.Length; k++)
            {
                _transport.SendFeatureReport(chunks[k]);
                if (WirelessMacroIntervalMs > 0)
                    await Task.Delay(WirelessMacroIntervalMs, ct).ConfigureAwait(false);
                if (waiter.Task.IsCompletedSuccessfully && waiter.Task.Result)
                    return true;
            }

            if (waiter.Task.IsCompletedSuccessfully && waiter.Task.Result)
                return true;
        }
        return false;
    }

    private TaskCompletionSource<bool> CreateAckWaiter()
    {
        lock (_ackSync)
        {
            _ackWaiter?.TrySetResult(false);
            _ackWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _ackWaiter;
        }
    }

    private void CompleteAckWaiter(bool ok)
    {
        lock (_ackSync)
        {
            _ackWaiter?.TrySetResult(ok);
        }
    }

    private void OnTransportConnectionChanged(ConnectionMode mode, HidEndpointInfo? endpoint)
    {
        Status.Mode = mode;
        if (mode == ConnectionMode.Disconnected)
        {
            StopWatchdog();
            Status.IsAwake = false;
            Status.IsCharging = false;
            Status.IsFullyCharged = false;
            Status.HasBatteryReading = false;
            Status.LastBatteryUpdateUtc = null;
            Status.VendorId = 0;
            Status.ProductId = 0;
            Status.VersionNumber = 0;
            Status.ProductName = "Disconnected";
            Status.MonitorPath = null;
            Status.FeaturePath = null;
            CompleteAckWaiter(false);
        }
        else
        {
            Status.IsAwake = true;
            Status.HasBatteryReading = false;
            Status.LastBatteryUpdateUtc = null;
            if (endpoint is not null)
            {
                Status.VendorId = endpoint.VendorId;
                Status.ProductId = endpoint.ProductId;
                Status.VersionNumber = endpoint.VersionNumber;
                Status.ProductName = string.IsNullOrWhiteSpace(endpoint.ProductName)
                    ? (mode == ConnectionMode.Wired ? "Attack Shark X11 (Wired)" : "Attack Shark X11 (2.4G)")
                    : endpoint.ProductName;
                Status.FeaturePath = endpoint.DevicePath;
            }
            Status.MonitorPath = _transport.ActiveMonitorEndpoint?.DevicePath;

            if (mode == ConnectionMode.Wireless24G)
                ResetWatchdog();
            else
                StopWatchdog();
        }

        StatusChanged?.Invoke(Status.Clone());
    }

    private void OnMonitorReportReceived(byte[] raw)
    {
        var ev = X11Protocol.ParseMonitorReport(raw);
        if (ev is null)
            return;

        switch (ev.Value.Type)
        {
            case MonitorEventType.DpiChanged:
            {
                int stage0 = ev.Value.Arg16 - 1;
                if (stage0 is >= 0 and < MouseProfile.DpiStageCount)
                {
                    Status.ActiveDpiStage = stage0;
                    StatusChanged?.Invoke(Status.Clone());
                    DpiStageChangedByMouse?.Invoke(stage0);
                }
                break;
            }

            case MonitorEventType.PollingRateChanged:
            {
                if (ev.Value.Arg16 <= 3)
                {
                    var pr = (PollingRate)ev.Value.Arg16;
                    Status.PollingRate = pr;
                    StatusChanged?.Invoke(Status.Clone());
                    PollingRateChangedByMouse?.Invoke(pr);
                }
                break;
            }

            case MonitorEventType.BatteryOrHeartbeat:
            {
                byte state = ev.Value.ArgLow;
                byte pct = ev.Value.ArgHigh;

                if (_transport.CurrentMode == ConnectionMode.Wireless24G)
                {
                    ResetWatchdog();
                    Status.IsAwake = true;
                }

                if (_transport.CurrentMode == ConnectionMode.Wired)
                {
                    Status.HasBatteryReading = true;
                    Status.LastBatteryUpdateUtc = DateTime.UtcNow;
                    if (state == 2)
                    {
                        Status.IsCharging = false;
                        Status.IsFullyCharged = true;
                        Status.BatteryPercent = 100;
                    }
                    else
                    {
                        Status.IsCharging = true;
                        Status.IsFullyCharged = false;
                    }
                }
                else
                {
                    if (state == 3)
                    {
                        Status.IsCharging = true;
                        Status.IsFullyCharged = false;
                        Status.HasBatteryReading = true;
                        Status.LastBatteryUpdateUtc = DateTime.UtcNow;
                    }
                    else if (state == 2)
                    {
                        Status.IsCharging = false;
                        Status.IsFullyCharged = true;
                        Status.BatteryPercent = 100;
                        Status.HasBatteryReading = true;
                        Status.LastBatteryUpdateUtc = DateTime.UtcNow;
                    }
                    else if (pct <= 100)
                    {
                        Status.IsCharging = false;
                        Status.IsFullyCharged = false;
                        Status.BatteryPercent = pct;
                        Status.HasBatteryReading = true;
                        Status.LastBatteryUpdateUtc = DateTime.UtcNow;
                    }
                }

                StatusChanged?.Invoke(Status.Clone());
                break;
            }

            case MonitorEventType.WirelessAck:
                CompleteAckWaiter(ev.Value.ArgLow == 0x00);
                break;

            case MonitorEventType.LegacyBatteryBar:
                if (ev.Value.Arg16 is >= 1 and <= 10)
                {
                    Status.BatteryPercent = ev.Value.Arg16 * 10;
                    Status.HasBatteryReading = true;
                    Status.LastBatteryUpdateUtc = DateTime.UtcNow;
                    StatusChanged?.Invoke(Status.Clone());
                }
                break;

            case MonitorEventType.LightModeChanged:
            {
                int lm = (ev.Value.Arg16 >> 4) & 0x0F;
                if (lm is >= 0 and <= 6)
                {
                    var mode = (LightMode)lm;
                    Status.LightMode = mode;
                    StatusChanged?.Invoke(Status.Clone());
                    LightModeChangedByMouse?.Invoke(mode);
                }
                break;
            }

            case MonitorEventType.ProfileChanged:
                if (ev.Value.Arg16 <= 4)
                    ProfileChangedByMouse?.Invoke(ev.Value.Arg16);
                break;
        }
    }

    private void ResetWatchdog()
    {
        if (!_disposed)
            _sleepWatchdogTimer.Change(SleepWatchdogTimeout, Timeout.InfiniteTimeSpan);
    }

    private void StopWatchdog()
    {
        if (!_disposed)
            _sleepWatchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnSleepWatchdogElapsed()
    {
        if (_disposed || _transport.CurrentMode != ConnectionMode.Wireless24G)
            return;

        if (Status.IsAwake)
        {
            Status.IsAwake = false;
            StatusChanged?.Invoke(Status.Clone());
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport.ConnectionChanged -= OnTransportConnectionChanged;
        _transport.MonitorReportReceived -= OnMonitorReportReceived;
        _sleepWatchdogTimer.Dispose();
        _sendLock.Dispose();
    }
}
