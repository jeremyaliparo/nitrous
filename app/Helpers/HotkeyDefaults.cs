using System;
using System.Linq;
using Nitrous.Managers;

namespace Nitrous.Helpers;

/// <summary>
/// Seeds the bindings Nitrous ships with, exactly once.
///
/// The dashboard hotkey is the only one seeded: cycling power silently is a surprising thing to do
/// to a machine, so that one stays unbound until the user asks for it.
///
/// Seeding is guarded by a revision rather than run every start, because "no hotkey" is a valid
/// choice - the KB page clears a binding when the user presses Escape. Without the guard, a user
/// who deliberately cleared the hotkey would get it back on the next launch.
/// </summary>
public static class HotkeyDefaults
{
    private const string RevisionKey = "HotkeyDefaultsRevision";
    private const int CurrentRevision = 2;

    private const string DashboardKey = "Hotkey_Dashboard";
    private const string NvidiaKey = "Hotkey_NvidiaApp";

    /// <summary>VK_HOME.</summary>
    private const uint VkHome = 0x24;

    /// <summary>VK_N.</summary>
    private const uint VkN = 0x4E;

    /// <summary>The shipped dashboard binding: Ctrl + Home.</summary>
    public static string DashboardHotkey => HotkeyBinding.Create(HotkeyBinding.ModControl, VkHome);

    /// <summary>Opening the vendor GPU app: Ctrl + Alt + N.</summary>
    public static string NvidiaHotkey =>
        HotkeyBinding.Create(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, VkN);

    public static void EnsureSeeded()
    {
        if (SettingsManager.Get(RevisionKey, 0) >= CurrentRevision)
            return;

        // Presence, not emptiness. A value that exists but is empty was cleared on purpose, so it
        // is left alone; only a machine that has never stored one gets the default.
        var set = SettingsManager.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!set.Contains(DashboardKey))
            SettingsManager.Save(DashboardKey, DashboardHotkey);

        // Revision 2 added the NVIDIA shortcut. Unlike cycling the power mode, opening a settings
        // window is harmless, so it can ship bound and still be cleared with Escape.
        if (!set.Contains(NvidiaKey))
            SettingsManager.Save(NvidiaKey, NvidiaHotkey);

        SettingsManager.Save(RevisionKey, CurrentRevision);
    }
}
