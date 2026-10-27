using System;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Helpers;

/// <summary>
/// Cross-process wake-up used by the dashboard to tell the tray that a hotkey changed.
/// The tray also polls the registry every two seconds, so this is a latency optimization:
/// a missing signal only makes the change take up to one poll interval.
/// </summary>
public static class HotkeyChangeSignal
{
    private const string EventName = @"Local\Nitrous.HotkeyChanged";

    public static EventWaitHandle CreateReceiver()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            Logger.Warn("Hotkey: could not create the cross-process change event.", ex);
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
            Logger.Warn("Hotkey: could not signal the tray about the change.", ex);
        }
    }
}
