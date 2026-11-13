using System;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;
using Nitrous.Diagnostics;
using Nitrous.Managers;
using Nitrous.Ui;

namespace Nitrous;

static class Program
{
    private static Mutex? mutex;
    private static Mutex? dashboardMutex;

    private const string MutexName = "Nitrous_SingleInstance_Mutex_Lock";
    private const string DashboardMutexName = "Nitrous_Dashboard_SingleInstance_Mutex_Lock";

    [STAThread]
    static void Main(string[] args)
    {
        // The tray OSD and context menu are WinForms, so opt into per-monitor DPI before
        // any window exists. The manifest declares the same mode; this is a safety net for
        // hosts that ignore the manifest setting.
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
        catch (InvalidOperationException) { }

        var isUi = args.Any(arg => string.Equals(arg, "--ui", StringComparison.OrdinalIgnoreCase));
        Logger.Initialize(isUi ? "dashboard" : "tray");

        // Offscreen capture runs before the elevation gate on purpose: rendering the visual tree
        // reads no hardware, so a screenshot should not need an administrator prompt.
        if (TryParseCapture(args) is { } capture)
        {
            Environment.ExitCode = Nitrous.Ui.UiCapture.Run(
                capture.Page, capture.Output, capture.Scale, capture.SettleMs);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("Unhandled exception.", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
            Logger.Error("WinForms thread exception.", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        if (!IsAdministrator())
        {
            Logger.Warn("Elevation: administrator=false; exiting.");
            MessageBox.Show(
                "Nitrous needs administrator privileges to access the Acer EC (fans, power modes, " +
                "battery limit) and Windows power plans.\n\nPlease run Nitrous as administrator.",
                "Nitrous", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Logger.Info("Elevation: administrator=true.");

        // Runs before either role reads a binding, so the tray registers the seeded hotkey and the
        // dashboard displays it without a race.
        Nitrous.Helpers.HotkeyDefaults.EnsureSeeded();

        // TRANSIENT UI MODE: run the dashboard in its own process.
        if (isUi)
        {
            RunDashboard();
            return;
        }

        // BACKGROUND ENGINE MODE: runs purely in the system tray.
        var isUpdateRestart = args.Any(arg => string.Equals(arg, "--updated", StringComparison.OrdinalIgnoreCase));
        if (!AcquireSingleInstance(isUpdateRestart))
            return;

        // Do this only after the previous instance has exited: the old .exe is still mapped
        // while the updater's predecessor is shutting down and cannot be deleted before then.
        UpdateManager.CleanupOldFiles();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!AcerWmiManager.IsHardwareSupported())
        {
            MessageBox.Show(
                "This Acer model does not expose the AcerGamingFunction WMI interface, so Nitrous " +
                "cannot control its fans or power modes.",
                "Nitrous", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new TrayApplication());
        GC.KeepAlive(mutex);
    }

    /// <summary>
    /// Takes the single-instance mutex. A normal launch gives up immediately; an update launch
    /// (<c>--updated</c>) waits for the instance it is replacing, and takes over if that instance
    /// never finishes exiting.
    /// </summary>
    private static bool AcquireSingleInstance(bool waitForPrevious)
    {
        mutex = new Mutex(false, MutexName);

        var attempts = waitForPrevious ? 30 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (TryTakeMutex(waitForPrevious ? 1000 : 0))
                return true;
        }

        // Only reachable for an update launch: the instance being replaced still holds the mutex
        // after half a minute, so it is stuck in its own shutdown rather than busy. It has already
        // done its hardware teardown by then - 0.13.0 and earlier hung later than that, disposing
        // the GPU manager after the updater had replaced the bundle - so ending it is better than
        // leaving the user with no Nitrous at all.
        if (waitForPrevious)
        {
            Logger.Warn($"The previous instance still holds the mutex after {attempts} s; taking over.");
            TrayApplication.KillOtherInstances();

            if (TryTakeMutex(5000))
            {
                Logger.Info("Took over from the stalled previous instance.");
                return true;
            }
        }

        Logger.Info("Another Nitrous instance is already running; exiting.");
        mutex.Dispose();
        mutex = null;
        return false;
    }

    private static bool TryTakeMutex(int millisecondsTimeout)
    {
        try
        {
            return mutex!.WaitOne(millisecondsTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance died or was terminated; this process now owns the mutex.
            return true;
        }
    }

    /// <summary>
    /// Parses the offscreen capture switches: <c>--screenshot &lt;page&gt; [--out &lt;path&gt;]
    /// [--scale &lt;n&gt;] [--settle &lt;ms&gt;]</c>. Returns null when <c>--screenshot</c> is absent, so
    /// every other launch path behaves exactly as before.
    /// </summary>
    private static (string Page, string Output, double Scale, int SettleMs)? TryParseCapture(string[] args)
    {
        string? page = null, output = null;
        var scale = 1.0;
        var settle = 0;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--screenshot":
                    page = args[++i];
                    break;
                case "--out":
                    output = args[++i];
                    break;
                case "--scale":
                    if (double.TryParse(args[++i], out var parsedScale)) scale = parsedScale;
                    break;
                case "--settle":
                    if (int.TryParse(args[++i], out var parsedSettle)) settle = parsedSettle;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(page)) return null;

        output ??= System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nitrous-{page.ToLowerInvariant()}.png");

        return (page, output, scale, settle);
    }

    private static void RunDashboard()
    {
        // Hard backstop for every launch path (hotkey, tray menu, a manual --ui start): only one
        // dashboard window may exist. The tray also debounces launches before they get here.
        if (!AcquireDashboardSingleInstance())
            return;

        try
        {
            // Keep WPF out of the background tray process. The dashboard is the only mode that
            // uses WPF, and forcing software rendering here avoids a GPU composition device in
            // a process that is only showing 2D UI.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            var app = new System.Windows.Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                Logger.Error("Dashboard thread exception.", e.Exception);
                e.Handled = true;
            };

            // Appearance is installed before the window is parsed so no control ever renders
            // with an unresolved brush.
            Nitrous.Ui.Theme.ThemeManager.Initialise(app);

            app.Run(new NitrousDashboard());
        }
        finally
        {
            try { dashboardMutex?.ReleaseMutex(); } catch { }
            dashboardMutex?.Dispose();
            dashboardMutex = null;
        }
    }

    private static bool AcquireDashboardSingleInstance()
    {
        dashboardMutex = new Mutex(false, DashboardMutexName);

        try
        {
            // A dashboard that was just closed may still be tearing down. Wait briefly so a
            // quick reopen does not lose the race and exit without showing a window.
            if (dashboardMutex.WaitOne(TimeSpan.FromSeconds(2)))
                return true;
        }
        catch (AbandonedMutexException)
        {
            // A previous dashboard crashed; this process now owns the mutex.
            return true;
        }

        Logger.Info("Another Nitrous dashboard is already running; exiting.");
        dashboardMutex.Dispose();
        dashboardMutex = null;
        return false;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not determine whether the process is elevated.", ex);
            return false;
        }
    }
}
