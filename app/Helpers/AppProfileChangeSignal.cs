using System;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Helpers;

/// <summary>
/// Cross-process wake-up used by the dashboard to tell the tray that a rule, a power mode or a fan
/// mode changed. The app-profile engine re-evaluates from its live process set; this is not a poll.
/// </summary>
public static class AppProfileChangeSignal
{
    private const string EventName = @"Local\Nitrous.AppProfilesChanged";

    public static EventWaitHandle CreateReceiver()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Logger.Warn("App profiles: could not create the cross-process change event.", ex);
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }
    }

    public static void Notify()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName, out var handle))
                return;

            using (handle)
                handle.Set();
        }
        catch (Exception ex)
        {
            Logger.Warn("App profiles: could not signal the tray about the change.", ex);
        }
    }
}
