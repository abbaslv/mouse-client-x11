using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using X11Mouse.App.Services;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Protocol;

namespace X11Mouse.App;

public partial class App : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private Mutex? _singleInstanceMutex;
    private TrayIconService? _trayService;
    private MainWindow? _mainWindow;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            RunProbeAndExit();
            return;
        }

        if (e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSmokeTest();
            return;
        }

        bool createdNew;
        _singleInstanceMutex = new Mutex(true, @"Local\AttackSharkX11Clean_SingleInstance", out createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _trayService = new TrayIconService();
        _mainWindow = new MainWindow(smokeTestMode: false);

        _mainWindow.ProfilesListUpdated += (names, active) => _trayService?.UpdateProfiles(names, active);
        _mainWindow.TooltipUpdated += tip => _trayService?.UpdateTooltip(tip);
        _trayService.ShowRequested += () =>
        {
            if (_mainWindow is null) return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        };
        _trayService.ProfileSwitchRequested += idx => _mainWindow?.SelectProfileIndex(idx);
        _trayService.ExitRequested += ExitAppCleanly;

        _mainWindow.Show();
    }

    private void RunProbeAndExit()
    {
        if (!Console.IsOutputRedirected)
            AttachConsole(ATTACH_PARENT_PROCESS);

        using var transport = new Win32HidTransport();
        var report = transport.ProbeReadOnly(TimeSpan.FromMilliseconds(2200));

        Console.WriteLine(report.Summary.TrimEnd());
        foreach (var raw in report.CapturedMonitorReports)
        {
            var parsed = X11Protocol.ParseMonitorReport(raw);
            if (parsed is not null)
            {
                Console.WriteLine($"  Parsed Event: {parsed.Value.Type} cmd=0x{parsed.Value.RawCommand:X2} arg16=0x{parsed.Value.Arg16:X4} (lo={parsed.Value.ArgLow}, hi={parsed.Value.ArgHigh})");
            }
        }
        Console.Out.Flush();
        Environment.Exit(0);
    }

    private void RunSmokeTest()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
        {
            Console.Error.WriteLine(exArgs.ExceptionObject?.ToString());
            Console.Error.Flush();
        };

        DispatcherUnhandledException += (_, exArgs) =>
        {
            Console.Error.WriteLine(exArgs.Exception.ToString());
            Console.Error.Flush();
            exArgs.Handled = true;
            Shutdown(1);
        };

        var win = new MainWindow(smokeTestMode: true);
        win.ForceExit = true;

        bool rendered = false;
        win.ContentRendered += (_, _) => rendered = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            win.Close();
            Shutdown(rendered ? 0 : 1);
        };
        timer.Start();
        win.Show();
    }

    private void ExitAppCleanly()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.ForceExit = true;
            _mainWindow.Close();
        }
        _trayService?.Dispose();
        _singleInstanceMutex?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
