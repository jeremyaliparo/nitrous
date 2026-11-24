using System;
using System.Management;
using System.Threading.Tasks;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Acer's firmware events, which is how the Mode (power-mode) key announces itself on the models
/// whose firmware leaves switching to software.
///
/// The class is <c>APGeEvent</c> in root\WMI, and each event's <c>EventDetail</c> starts with the
/// kind and its value. Kind 7 with value 4 or 5 is the Mode key - the AN515 family sends
/// <c>07 04 01</c> - and switching the mode is left to software, which is why the key does nothing
/// on such a machine with no Acer app running. Elsewhere the key never reaches here: on the
/// AN16S-61 it is applied inside the embedded controller and only the mode register moves
/// (§23.30.2), so this watcher stays idle rather than reporting a press that did not happen.
///
/// WMI ends the subscription when its service restarts, so it is re-established.
/// </summary>
internal static class FirmwareEvents
{
    private const string Namespace = @"\\.\root\WMI";
    private const string EventClass = "APGeEvent";
    private const byte ModeKeyKind = 7;
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static ManagementEventWatcher? _watcher;
    private static bool _stopping;

    /// <summary>Raised on a worker thread for each Mode-key press.</summary>
    public static event Action? ModeKeyPressed;

    /// <summary>Starts watching; false when this model has no event class. Safe to call twice.</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_watcher != null) return true;

            var watcher = new ManagementEventWatcher(
                new ManagementScope(Namespace),
                new WqlEventQuery($"SELECT * FROM {EventClass}"));

            watcher.EventArrived += OnEventArrived;
            watcher.Stopped += OnStopped;

            try
            {
                watcher.Start();
            }
            catch (Exception ex)
            {
                // A diagnostic watcher must never take the tray down: a model without the class,
                // or a WMI that refuses the subscription, simply leaves the key unread.
                watcher.Dispose();
                Logger.Info($"Firmware events: not watched ({ex.GetType().Name}), so the mode key cannot be read here.");
                return false;
            }

            _watcher = watcher;
            _stopping = false;
            Logger.Info("Firmware events: watching APGeEvent.");
            return true;
        }
    }

    public static void Stop()
    {
        ManagementEventWatcher? watcher;
        lock (Gate)
        {
            _stopping = true;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null) return;
        watcher.EventArrived -= OnEventArrived;
        watcher.Stopped -= OnStopped;
        try
        {
            watcher.Stop();
        }
        catch (ManagementException)
        {
            // Already gone.
        }

        watcher.Dispose();
    }

    private static void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (!IsModeKey(e.NewEvent["EventDetail"] as byte[])) return;

        Logger.Info("Firmware event: mode key pressed.");
        ModeKeyPressed?.Invoke();
    }

    /// <summary>Kind 7 with value 4 or 5 is the Mode key; anything else is another event.</summary>
    internal static bool IsModeKey(byte[]? detail) =>
        detail is { Length: >= 2 } && detail[0] == ModeKeyKind && detail[1] is 4 or 5;

    private static void OnStopped(object sender, StoppedEventArgs e)
    {
        lock (Gate)
        {
            if (_stopping) return;
            _watcher = null;
        }

        _ = Task.Delay(RestartDelay).ContinueWith(_ =>
        {
            if (!_stopping) Start();
        }, TaskScheduler.Default);
    }
}
