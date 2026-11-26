using System;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Keeps Windows from sleeping or turning the screen off while it is on.
///
/// This is a thread execution-state flag, not a timer, thread or service: one call when the preference
/// flips and nothing in between, so an idle Nitrous with it on costs no CPU at all - the kernel holds the
/// request. The request belongs to the calling thread and ends with it, which is why the tray applies it
/// on its UI thread (it outlives every dashboard) and the dashboard applies it on its own only while its
/// window is open, for an immediate response to the click.
/// </summary>
internal static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    private static bool _on;

    /// <summary>
    /// Sets the state for the calling thread. A call with the value it already holds does nothing, so
    /// this can be called whenever the saved preference is noticed without costing a system call.
    /// </summary>
    public static void Apply(bool on)
    {
        if (on == _on) return;

        // ES_CONTINUOUS alone releases a previous request.
        var flags = on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous;
        if (SetThreadExecutionState(flags) == 0)
        {
            Logger.Warn($"Keep awake: Windows refused to {(on ? "hold" : "release")} the wake request.");
            return;
        }

        _on = on;
        Logger.Info($"Keep awake: {(on ? "on - the machine and screen will stay awake" : "off")}.");
    }
}
