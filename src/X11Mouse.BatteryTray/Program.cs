using System.Runtime.InteropServices;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.BatteryTray;

internal static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
            return RunStatusProbe();

        if (args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
            return RunSmokeTest();

        using var singleInstance = new Mutex(true, @"Local\AttackSharkX11BatteryTray_SingleInstance", out bool createdNew);
        if (!createdNew)
            return 0;

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var context = new BatteryTrayAppContext(smokeTestMode: false);
        Application.Run(context);
        return 0;
    }

    private static int RunStatusProbe()
    {
        if (!Console.IsOutputRedirected)
            AttachConsole(ATTACH_PARENT_PROCESS);

        using var transport = new Win32HidTransport();
        var probe = transport.ProbeReadOnly(TimeSpan.FromMilliseconds(2200));

        Console.WriteLine($"X11 Battery Status: mode={(probe.ActiveFeatureEndpoint is not null ? (probe.ActiveFeatureEndpoint.ProductId == X11Protocol.WiredProductId ? "Wired" : "Wireless24G") : "Disconnected")}");

        MonitorEvent? lastBat = null;
        foreach (var raw in probe.CapturedMonitorReports)
        {
            var ev = X11Protocol.ParseMonitorReport(raw);
            if (ev?.Type is MonitorEventType.BatteryOrHeartbeat or MonitorEventType.LegacyBatteryBar)
                lastBat = ev;
        }

        if (lastBat is { Type: MonitorEventType.BatteryOrHeartbeat } bat)
        {
            Console.WriteLine($"  Battery={bat.ArgHigh}% state=0x{bat.ArgLow:X2}");
        }
        else if (lastBat is { Type: MonitorEventType.LegacyBatteryBar } bar)
        {
            Console.WriteLine($"  LegacyBattery={bar.Arg16 * 10}%");
        }
        else
        {
            Console.WriteLine(probe.ActiveMonitorEndpoint is not null
                ? "  Connected, waiting for Report 0x03 battery/heartbeat packet."
                : "  Device not connected.");
        }

        Console.Out.Flush();
        return 0;
    }

    private static int RunSmokeTest()
    {
        var states = new[]
        {
            new DeviceStatus { Mode = ConnectionMode.Disconnected },
            new DeviceStatus { Mode = ConnectionMode.Wireless24G, IsAwake = true, HasBatteryReading = false },
            new DeviceStatus { Mode = ConnectionMode.Wireless24G, IsAwake = true, HasBatteryReading = true, BatteryPercent = 100 },
            new DeviceStatus { Mode = ConnectionMode.Wireless24G, IsAwake = true, HasBatteryReading = true, BatteryPercent = 65 },
            new DeviceStatus { Mode = ConnectionMode.Wireless24G, IsAwake = false, HasBatteryReading = true, BatteryPercent = 30 },
            new DeviceStatus { Mode = ConnectionMode.Wireless24G, IsAwake = true, HasBatteryReading = true, BatteryPercent = 12 },
            new DeviceStatus { Mode = ConnectionMode.Wired, IsAwake = true, HasBatteryReading = true, IsCharging = true, BatteryPercent = 55 },
        };

        foreach (TrayIconStyle style in Enum.GetValues<TrayIconStyle>())
        {
            foreach (bool light in new[] { false, true })
            {
                foreach (var st in states)
                {
                    using var icon = TrayIconRenderer.RenderIcon(st, style, light);
                    if (icon.Width <= 0 || icon.Height <= 0)
                        return 1;
                }
            }
        }

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var ctx = new BatteryTrayAppContext(smokeTestMode: true);
        ctx.RunFlyoutSmokeCheck();
        return 0;
    }
}
