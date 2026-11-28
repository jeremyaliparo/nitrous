using System;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Helpers;

/// <summary>
/// Cross-process wake-up used by the dashboard to tell the tray that a tray-visible appearance
/// choice changed. The tray also polls every ten seconds, so this is a latency optimization:
/// a missing signal only makes the change take up to one poll interval.
/// </summary>
public static class AppearanceChangeSignal
{
    private const string EventName = @"Local\Nitrous.AppearanceChanged";

    public static EventWaitHandle CreateReceiver()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Logger.Warn("Appearance: could not create the cross-process change event.", ex);
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
            Logger.Warn("Appearance: could not signal the tray about the change.", ex);
        }
    }
}
