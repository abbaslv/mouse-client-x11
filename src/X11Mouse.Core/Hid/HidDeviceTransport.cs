using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.Core.Hid;

public sealed record HidEndpointInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength,
    ushort OutputReportLength,
    ushort FeatureReportLength,
    string ProductName,
    bool ReadWriteAccessible);

public sealed record HidProbeReport(
    IReadOnlyList<HidEndpointInfo> MatchingEndpoints,
    HidEndpointInfo? ActiveMonitorEndpoint,
    HidEndpointInfo? ActiveFeatureEndpoint,
    IReadOnlyList<byte[]> CapturedMonitorReports,
    string Summary);

/// <summary>
/// Abstraction over Attack Shark X11 HID transport to enable full unit testing without live hardware.
/// </summary>
public interface IHidTransport : IDisposable
{
    ConnectionMode CurrentMode { get; }
    HidEndpointInfo? ActiveMonitorEndpoint { get; }
    HidEndpointInfo? ActiveFeatureEndpoint { get; }

    event Action<ConnectionMode, HidEndpointInfo?>? ConnectionChanged;
    event Action<byte[]>? MonitorReportReceived;

    IReadOnlyList<HidEndpointInfo> EnumerateX11Endpoints();
    bool ConnectBestAvailable();
    void Disconnect();
    bool SendFeatureReport(byte[] payload);
    void StartHotPlugMonitor(TimeSpan? pollInterval = null);
    void StopHotPlugMonitor();
}

/// <summary>
/// Native Win32 SetupAPI + HID transport implementing `hiddriver_1.dll` / `hiddriver_2.dll` semantics:
/// shared access, `UsagePage=0x0A` overlapped monitor read loop, `UsagePage=0x0B` `HidD_SetFeature`,
/// and automatic Wired (`0xFA55`) -> 2.4G Wireless (`0xFA60`) detection.
/// </summary>
public sealed class Win32HidTransport : IHidTransport
{
    private readonly object _sync = new();
    private SafeFileHandle? _monitorHandle;
    private SafeFileHandle? _featureHandle;
    private Thread? _monitorThread;
    private CancellationTokenSource? _monitorCts;
    private Timer? _hotPlugTimer;
    private ConnectionMode _currentMode = ConnectionMode.Disconnected;
    private HidEndpointInfo? _activeMonitor;
    private HidEndpointInfo? _activeFeature;
    private bool _disposed;

    public ConnectionMode CurrentMode
    {
        get { lock (_sync) return _currentMode; }
    }

    public HidEndpointInfo? ActiveMonitorEndpoint
    {
        get { lock (_sync) return _activeMonitor; }
    }

    public HidEndpointInfo? ActiveFeatureEndpoint
    {
        get { lock (_sync) return _activeFeature; }
    }

    public event Action<ConnectionMode, HidEndpointInfo?>? ConnectionChanged;
    public event Action<byte[]>? MonitorReportReceived;

    public IReadOnlyList<HidEndpointInfo> EnumerateX11Endpoints()
    {
        var results = new List<HidEndpointInfo>();
        NativeHid.HidD_GetHidGuid(out var hidGuid);

        IntPtr devInfo = NativeHid.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);

        if (devInfo == new IntPtr(-1))
            return results;

        try
        {
            uint index = 0;
            while (true)
            {
                var iface = new NativeHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeHid.SP_DEVICE_INTERFACE_DATA>(),
                };

                if (!NativeHid.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index, ref iface))
                    break;
                index++;

                NativeHid.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize < 6)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeHid.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, detailBuf, reqSize, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrEmpty(path))
                        continue;

                    if (!path.Contains("vid_1d57", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains("vid_045e&pid_0055", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var info = InspectEndpoint(path);
                    if (info is not null &&
                        ((info.VendorId == X11Protocol.WiredVendorId &&
                          (info.ProductId == X11Protocol.WiredProductId || info.ProductId == X11Protocol.Wireless24GProductId)) ||
                         (info.VendorId == X11Protocol.BleVendorId && info.ProductId == X11Protocol.BleProductId)))
                    {
                        results.Add(info);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            NativeHid.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return results;
    }

    private static HidEndpointInfo? InspectEndpoint(string path)
    {
        bool rw = true;
        SafeFileHandle handle = NativeHid.CreateFile(
            path,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeHid.OPEN_EXISTING,
            NativeHid.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            rw = false;
            handle = NativeHid.CreateFile(
                path,
                0,
                NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeHid.OPEN_EXISTING,
                NativeHid.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        using (handle)
        {
            var attrs = new NativeHid.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<NativeHid.HIDD_ATTRIBUTES>() };
            if (!NativeHid.HidD_GetAttributes(handle, ref attrs))
                return null;

            NativeHid.HIDP_CAPS caps = default;
            if (NativeHid.HidD_GetPreparsedData(handle, out IntPtr pp))
            {
                try
                {
                    NativeHid.HidP_GetCaps(pp, out caps);
                }
                finally
                {
                    NativeHid.HidD_FreePreparsedData(pp);
                }
            }

            var prodChars = new char[128];
            string productName = string.Empty;
            if (NativeHid.HidD_GetProductString(handle, prodChars, (uint)(prodChars.Length * 2)))
            {
                int nul = Array.IndexOf(prodChars, '\0');
                productName = nul >= 0 ? new string(prodChars, 0, nul) : new string(prodChars);
            }

            return new HidEndpointInfo(
                path,
                attrs.VendorID,
                attrs.ProductID,
                attrs.VersionNumber,
                caps.UsagePage,
                caps.Usage,
                caps.InputReportByteLength,
                caps.OutputReportByteLength,
                caps.FeatureReportByteLength,
                productName,
                rw);
        }
    }

    public bool ConnectBestAvailable()
    {
        var endpoints = EnumerateX11Endpoints();
        (ConnectionMode mode, HidEndpointInfo? monitor, HidEndpointInfo? feature) picked = PickBestPair(endpoints);

        ConnectionMode prevMode;
        bool changed = false;
        HidEndpointInfo? notifyEndpoint = null;

        lock (_sync)
        {
            if (_disposed)
                return false;

            prevMode = _currentMode;

            bool samePaths = _currentMode == picked.mode &&
                             string.Equals(_activeMonitor?.DevicePath, picked.monitor?.DevicePath, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(_activeFeature?.DevicePath, picked.feature?.DevicePath, StringComparison.OrdinalIgnoreCase);
            if (samePaths && _currentMode != ConnectionMode.Disconnected)
                return true;

            CloseHandlesNoLock();

            if (picked.mode == ConnectionMode.Disconnected || picked.monitor is null || picked.feature is null)
            {
                _currentMode = ConnectionMode.Disconnected;
                _activeMonitor = null;
                _activeFeature = null;
                changed = prevMode != ConnectionMode.Disconnected;
            }
            else
            {
                var monHandle = OpenOverlappedRw(picked.monitor.DevicePath);
                var featHandle = OpenOverlappedRw(picked.feature.DevicePath);

                if (monHandle is null || featHandle is null)
                {
                    monHandle?.Dispose();
                    featHandle?.Dispose();
                    _currentMode = ConnectionMode.Disconnected;
                    _activeMonitor = null;
                    _activeFeature = null;
                    changed = prevMode != ConnectionMode.Disconnected;
                }
                else
                {
                    _monitorHandle = monHandle;
                    _featureHandle = featHandle;
                    _activeMonitor = picked.monitor;
                    _activeFeature = picked.feature;
                    _currentMode = picked.mode;
                    notifyEndpoint = picked.feature;
                    changed = true;
                    StartMonitorThreadNoLock(monHandle, picked.monitor.InputReportLength);
                }
            }
        }

        if (changed)
            ConnectionChanged?.Invoke(CurrentMode, notifyEndpoint);

        return CurrentMode != ConnectionMode.Disconnected;
    }

    private static (ConnectionMode mode, HidEndpointInfo? monitor, HidEndpointInfo? feature) PickBestPair(
        IReadOnlyList<HidEndpointInfo> endpoints)
    {
        // Priority matches Mouse.exe: Wired (FA55) before 2.4G Wireless (FA60).
        foreach (var (mode, vid, pid) in new[]
        {
            (ConnectionMode.Wired, X11Protocol.WiredVendorId, X11Protocol.WiredProductId),
            (ConnectionMode.Wireless24G, X11Protocol.Wireless24GVendorId, X11Protocol.Wireless24GProductId),
        })
        {
            HidEndpointInfo? mon = null;
            HidEndpointInfo? feat = null;
            foreach (var ep in endpoints)
            {
                if (ep.VendorId != vid || ep.ProductId != pid)
                    continue;
                if (ep.UsagePage == X11Protocol.MonitorUsagePage && ep.Usage == X11Protocol.MonitorUsage && ep.InputReportLength == X11Protocol.MonitorReportLength)
                    mon ??= ep;
                else if (ep.UsagePage == X11Protocol.FeatureUsagePage && ep.Usage == X11Protocol.FeatureUsage && ep.FeatureReportLength > 0)
                    feat ??= ep;
            }
            if (mon is not null && feat is not null)
                return (mode, mon, feat);
        }

        return (ConnectionMode.Disconnected, null, null);
    }

    private static SafeFileHandle? OpenOverlappedRw(string path)
    {
        var handle = NativeHid.CreateFile(
            path,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeHid.OPEN_EXISTING,
            NativeHid.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    public bool SendFeatureReport(byte[] payload)
    {
        SafeFileHandle? featureHandle;
        int featureLen;

        lock (_sync)
        {
            if (_disposed || _featureHandle is null || _featureHandle.IsInvalid)
                return false;
            featureHandle = _featureHandle;
            featureLen = _activeFeature?.FeatureReportLength ?? X11Protocol.FeatureReportLength;
        }

        // hiddriver_1.dll forwards (buf, len) directly to HidD_SetFeature.
        if (NativeHid.HidD_SetFeature(featureHandle, payload, (uint)payload.Length))
            return true;

        int err = Marshal.GetLastWin32Error();
        if (err == NativeHid.ERROR_INVALID_USER_BUFFER && featureLen > payload.Length)
        {
            var padded = new byte[featureLen];
            Buffer.BlockCopy(payload, 0, padded, 0, payload.Length);
            return NativeHid.HidD_SetFeature(featureHandle, padded, (uint)padded.Length);
        }

        return false;
    }

    public void StartHotPlugMonitor(TimeSpan? pollInterval = null)
    {
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(2);
        lock (_sync)
        {
            if (_disposed)
                return;
            _hotPlugTimer?.Dispose();
            _hotPlugTimer = new Timer(_ => ConnectBestAvailable(), null, TimeSpan.Zero, interval);
        }
    }

    public void StopHotPlugMonitor()
    {
        lock (_sync)
        {
            _hotPlugTimer?.Dispose();
            _hotPlugTimer = null;
        }
    }

    public void Disconnect()
    {
        bool wasConnected;
        lock (_sync)
        {
            wasConnected = _currentMode != ConnectionMode.Disconnected;
            CloseHandlesNoLock();
            _currentMode = ConnectionMode.Disconnected;
            _activeMonitor = null;
            _activeFeature = null;
        }

        if (wasConnected)
            ConnectionChanged?.Invoke(ConnectionMode.Disconnected, null);
    }

    /// <summary>
    /// Runs a strictly read-only probe against any connected X11 hardware (never writes any feature report).
    /// </summary>
    public HidProbeReport ProbeReadOnly(TimeSpan listenWindow)
    {
        var endpoints = EnumerateX11Endpoints();
        var (mode, mon, feat) = PickBestPair(endpoints);
        var reports = new List<byte[]>();

        if (mon is not null)
        {
            using var h = OpenOverlappedRw(mon.DevicePath);
            if (h is not null)
            {
                using var cts = new CancellationTokenSource(listenWindow);
                ReadReportBatch(h, mon.InputReportLength, reports, cts.Token, maxReports: 3);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"X11 Probe: mode={mode}, matchingEndpoints={endpoints.Count}");
        foreach (var ep in endpoints)
        {
            sb.AppendLine(
                $"  VID=0x{ep.VendorId:X4} PID=0x{ep.ProductId:X4} Ver=0x{ep.VersionNumber:X4} " +
                $"UP=0x{ep.UsagePage:X4} U=0x{ep.Usage:X4} In={ep.InputReportLength} Out={ep.OutputReportLength} Feat={ep.FeatureReportLength} " +
                $"Access={(ep.ReadWriteAccessible ? "RW" : "QUERY")} Product=\"{ep.ProductName}\" Path={ep.DevicePath}");
        }
        sb.AppendLine($"  CapturedMonitorReports={reports.Count}");
        foreach (var r in reports)
        {
            sb.AppendLine("    [" + string.Join(", ", Array.ConvertAll(r, b => $"0x{b:X2}")) + "]");
        }

        return new HidProbeReport(endpoints, mon, feat, reports, sb.ToString());
    }

    private static unsafe void ReadReportBatch(
        SafeFileHandle handle,
        int reportLength,
        List<byte[]> sink,
        CancellationToken ct,
        int maxReports)
    {
        int len = Math.Max(reportLength, X11Protocol.MonitorReportLength);
        var buf = new byte[len];
        using var ev = NativeHid.CreateEvent(IntPtr.Zero, true, false, null);

        while (!ct.IsCancellationRequested && sink.Count < maxReports)
        {
            Array.Clear(buf);
            var ov = new NativeHid.OVERLAPPED_NATIVE { hEvent = ev.DangerousGetHandle() };
            uint bytesRead = 0;

            fixed (byte* pBuf = buf)
            {
                bool ok = NativeHid.ReadFile(handle, pBuf, (uint)len, out bytesRead, ref ov);
                int err = Marshal.GetLastWin32Error();
                if (!ok)
                {
                    if (err != NativeHid.ERROR_IO_PENDING)
                        break;

                    uint wait = NativeHid.WaitForSingleObject(ev, 250);
                    while (wait != NativeHid.WAIT_OBJECT_0)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            NativeHid.CancelIoEx(handle, IntPtr.Zero);
                            return;
                        }
                        wait = NativeHid.WaitForSingleObject(ev, 250);
                    }

                    if (!NativeHid.GetOverlappedResult(handle, ref ov, out bytesRead, false))
                        break;
                }
            }

            if (bytesRead >= X11Protocol.MonitorReportLength)
            {
                var copy = new byte[bytesRead];
                Buffer.BlockCopy(buf, 0, copy, 0, (int)bytesRead);
                sink.Add(copy);
            }
        }
    }

    private unsafe void StartMonitorThreadNoLock(SafeFileHandle monHandle, int reportLength)
    {
        var cts = new CancellationTokenSource();
        _monitorCts = cts;
        int len = Math.Max(reportLength, X11Protocol.MonitorReportLength);

        var thread = new Thread(() =>
        {
            var buf = new byte[len];
            using var ev = NativeHid.CreateEvent(IntPtr.Zero, true, false, null);
            bool broken = false;

            while (!cts.IsCancellationRequested)
            {
                Array.Clear(buf);
                var ov = new NativeHid.OVERLAPPED_NATIVE { hEvent = ev.DangerousGetHandle() };
                uint bytesRead = 0;

                try
                {
                    fixed (byte* pBuf = buf)
                    {
                        bool ok = NativeHid.ReadFile(monHandle, pBuf, (uint)len, out bytesRead, ref ov);
                        int err = Marshal.GetLastWin32Error();

                        if (!ok)
                        {
                            if (err != NativeHid.ERROR_IO_PENDING)
                            {
                                broken = true;
                                break;
                            }

                            while (!cts.IsCancellationRequested)
                            {
                                uint wait = NativeHid.WaitForSingleObject(ev, 250);
                                if (wait == NativeHid.WAIT_OBJECT_0)
                                {
                                    if (!NativeHid.GetOverlappedResult(monHandle, ref ov, out bytesRead, false))
                                        broken = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    broken = true;
                }

                if (broken || cts.IsCancellationRequested)
                    break;

                if (bytesRead >= X11Protocol.MonitorReportLength && buf[0] == 0x03)
                {
                    var packet = new byte[bytesRead];
                    Buffer.BlockCopy(buf, 0, packet, 0, (int)bytesRead);
                    MonitorReportReceived?.Invoke(packet);
                }
            }

            if (broken && !cts.IsCancellationRequested)
            {
                Disconnect();
            }
        })
        {
            IsBackground = true,
            Name = "X11HidMonitor",
        };
        _monitorThread = thread;
        thread.Start();
    }

    private void CloseHandlesNoLock()
    {
        var cts = _monitorCts;
        _monitorCts = null;
        cts?.Cancel();

        if (_monitorHandle is not null && !_monitorHandle.IsInvalid)
        {
            NativeHid.CancelIoEx(_monitorHandle, IntPtr.Zero);
        }

        var t = _monitorThread;
        _monitorThread = null;
        if (t is not null && t != Thread.CurrentThread)
        {
            t.Join(TimeSpan.FromMilliseconds(500));
        }

        cts?.Dispose();
        _monitorHandle?.Dispose();
        _monitorHandle = null;
        _featureHandle?.Dispose();
        _featureHandle = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _hotPlugTimer?.Dispose();
            _hotPlugTimer = null;
            CloseHandlesNoLock();
        }
    }
}
