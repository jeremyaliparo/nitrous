using System;
using System.Globalization;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>
/// Sustained (PL1) and burst (PL2) power caps written to MSR 0x610, held <b>per power profile</b>.
///
/// Lower-only by construction: each field is clamped to the current mode's own request, read from MCHBAR
/// 0x59A0, so nothing here can ask the package for more power than the firmware already asked for. PL1 is
/// also held at or below PL2.
///
/// Per profile because the firmware's own numbers are neither fixed nor shared: on one ANV15-51 the same
/// Balanced mode asked 24, 29, 37, 41, 45 and 55 W over a single day (§23.193). A single global cap applied
/// to every mode therefore looked like it "sometimes applied and sometimes did not" - the clamp was
/// following a moving ceiling. Values are stored per profile, and a profile with no value is left alone.
///
/// The firmware's request is read from MCHBAR, which a write to 0x610 has been reported moving, so once a
/// cap is written the live copy is not trusted alone: the last reading taken while the firmware owned it is
/// kept and the smaller of the two is used.
///
/// Bit 63 (the lock) is never written: that would make the register refuse changes until the next boot.
/// </summary>
internal static class CpuPowerCap
{
    /// <summary>Below this a laptop is not usable for anything. A usability floor, not a vendor limit.</summary>
    public const int FloorWatts = 10;

    private const string LegacyPl1Key = "CpuPowerCapWatts";
    private const string LegacyPl2Key = "CpuPowerCapBurstWatts";
    private const string Pl1CeilingKey = "CpuPowerCapCeiling";
    private const string Pl2CeilingKey = "CpuPowerCapBurstCeiling";

    private const ulong Pl1Mask = 0x7FFF;
    private const ulong Pl2Mask = 0x7FFFUL << 32;
    private const ulong Fields = Pl1Mask | Pl2Mask;
    private const ulong LockBit = 1UL << 63;

    private static readonly object Gate = new();

    private static bool _moved;
    private static Limits? _pristine;
    private static Limits? _lastApplied;
    private static (Limits Now, Limits Ask)? _dipNoted;

    public readonly record struct Limits(int Pl1, int Pl2);

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>The caps chosen for a profile; both zero when that profile follows the mode's own limits.</summary>
    public static Limits SavedFor(PowerProfile profile)
    {
        var stored = SettingsManager.Get(KeyFor(profile), "");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var parts = stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl1) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl2))
            {
                return new Limits(Math.Max(0, pl1), Math.Max(0, pl2));
            }
        }

        return new Limits(0, 0);
    }

    public static void SaveFor(PowerProfile profile, int pl1, int pl2)
    {
        SettingsManager.Save(KeyFor(profile), $"{Math.Max(0, pl1)},{Math.Max(0, pl2)}");

        if (pl1 > 0 || pl2 > 0)
            SettingsManager.Save("CpuPowerCapConfigured", 1);
    }

    /// <summary>
    /// Whether any profile has ever been capped. Until then, mode changes write nothing at all; afterwards an
    /// uncapped profile means "follow this mode", which has to be enforced like a cap does.
    /// </summary>
    private static bool Configured => SettingsManager.Get("CpuPowerCapConfigured", 0) == 1;

    private static string KeyFor(PowerProfile profile) => $"CpuPowerCap_{profile}";

    /// <summary>
    /// Moves a cap saved by the single-value model (0.14.x) onto the profile that is active now, then clears
    /// it, so an upgrade does not silently drop a limit someone set.
    /// </summary>
    public static void MigrateLegacyCap(PowerProfile profile)
    {
        var pl1 = Math.Max(0, SettingsManager.Get(LegacyPl1Key, 0));
        var pl2 = Math.Max(0, SettingsManager.Get(LegacyPl2Key, 0));
        if (pl1 == 0 && pl2 == 0) return;

        if (SavedFor(profile) == new Limits(0, 0))
        {
            SaveFor(profile, pl1, pl2);
            Logger.Info($"CPU power cap: moved the saved cap {pl1}/{pl2} W onto {profile}.");
        }

        SettingsManager.Save(LegacyPl1Key, 0);
        SettingsManager.Save(LegacyPl2Key, 0);
    }

    /// <summary>
    /// The firmware's own limits for this mode: the highest either cap may be set to.
    ///
    /// A write can move the firmware's copy, so a live value that is exactly what Nitrous last wrote cannot
    /// be told apart from the mode's request: there the reading taken while the firmware owned it is kept.
    /// Any other live value is the firmware's own number - including one that came back up after dipping -
    /// and is adopted, because a dip must not ratchet the ceiling down for the rest of the session.
    /// </summary>
    public static Limits? Ceilings()
    {
        var live = LiveAsk();

        if (!_moved)
        {
            _pristine = live;
            return live;
        }

        if (live is { } seen && _lastApplied is { } wrote && seen == wrote)
            return _pristine ?? seen;

        if (live is not null) _pristine = live;
        return live ?? _pristine;
    }

    /// <summary>The firmware's own request right now, or null when it cannot be read.</summary>
    private static Limits? LiveAsk()
    {
        var firmware = CpuPowerLimits.ReadFirmware();
        return firmware is { Pl1Watts: > 0, Pl2Watts: > 0 } mode
            ? new Limits((int)Math.Round(mode.Pl1Watts), (int)Math.Round(mode.Pl2Watts))
            : null;
    }

    /// <summary>What the register holds right now, or null when it cannot be read.</summary>
    public static Limits? Applied() =>
        CpuPowerLimits.ReadRegister() is { Pl1Watts: > 0 } held
            ? new Limits((int)Math.Round(held.Pl1Watts), (int)Math.Round(held.Pl2Watts))
            : null;

    /// <summary>Whether the register may be written right now.</summary>
    public static bool IsWritable() =>
        CpuPowerLimits.ReadRegisterRaw() is { } raw && (raw & LockBit) == 0;

    /// <summary>
    /// Writes both caps, clamped to <see cref="FloorWatts"/> and to the mode's own request, then reads them
    /// back. A null field means "leave this one at the mode's value".
    /// </summary>
    public static Result Apply(int? pl1Watts, int? pl2Watts)
    {
        lock (Gate)
        {
            if (Ceilings() is not { } mode)
                return Fail("this mode does not report its limits.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            var pl1 = Math.Clamp(pl1Watts ?? mode.Pl1, FloorWatts, mode.Pl1);
            var pl2 = Math.Clamp(pl2Watts ?? mode.Pl2, Math.Max(FloorWatts, pl1), mode.Pl2);

            if (pl1 > pl2)
                return Fail("the sustained limit cannot sit above the burst limit.");

            var value = (raw & ~Fields)
                        | ((ulong)pl1 * 8 & Pl1Mask)
                        | (((ulong)pl2 * 8) << 32 & Pl2Mask);

            if ((value & ~Fields) != (raw & ~Fields))
                return Fail("the new value would have changed another field; nothing written.");

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Fields) != (value & Fields))
            {
                PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, raw);
                return Fail("the register did not keep the value; the old one is back.");
            }

            _moved = true;
            _pristine = mode;
            _lastApplied = new Limits(pl1, pl2);

            SettingsManager.Save(Pl1CeilingKey, mode.Pl1);
            SettingsManager.Save(Pl2CeilingKey, mode.Pl2);

            Logger.Info($"CPU power cap: {pl1} W sustained / {pl2} W burst " +
                        $"(this mode asks {mode.Pl1}/{mode.Pl2}).");

            return new Result(true, pl1 == mode.Pl1 && pl2 == mode.Pl2
                ? "Following this mode's own limits."
                : $"Capped to {pl1} W sustained / {pl2} W burst.");
        }
    }

    /// <summary>
    /// Puts both fields back to the mode's own request. That is the definition of "no cap", and because the
    /// ceilings are the firmware's own numbers it can never raise a limit above what the mode asked for.
    /// </summary>
    public static Result Reset()
    {
        lock (Gate)
        {
            if (Ceilings() is not { } mode)
                return Fail("this mode does not report its limits.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            Logger.Info($"CPU power cap: clearing, this mode asks {mode.Pl1}/{mode.Pl2} W, register holds " +
                        $"{Applied()?.Pl1 ?? 0}/{Applied()?.Pl2 ?? 0} W.");

            var value = (raw & ~Fields)
                        | ((ulong)mode.Pl1 * 8 & Pl1Mask)
                        | (((ulong)mode.Pl2 * 8) << 32 & Pl2Mask);

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Fields) != (value & Fields))
                return Fail("the value did not stick; the register may be locked.");

            _moved = true;
            _pristine = mode;
            _lastApplied = mode;

            Logger.Info($"CPU power cap: cleared, back to the mode's {mode.Pl1}/{mode.Pl2} W.");
            return new Result(true, $"Back to {mode.Pl1} W sustained / {mode.Pl2} W burst.");
        }
    }

    /// <summary>
    /// Applies the profile's own caps on a mode change, at start-up or after a resume. A profile with no
    /// saved cap is left entirely alone, which is what makes the cap optional per power mode.
    /// </summary>
    /// <param name="modeChanged">
    /// True when the power mode itself just changed. A periodic refresh passes false: the firmware's own
    /// request dips on its own, and following a dip down wrote 20 W over a 25 W cap minutes after it was set.
    /// </param>
    public static void Reassert(PowerProfile profile, bool modeChanged = false)
    {
        if (!Configured) return;

        if (Ceilings() is not { } mode) return;
        if (Applied() is not { } now) return;

        var saved = SavedFor(profile);

        // No cap in this profile: write nothing. Clearing here reverted the register on every mode change
        // into an uncapped profile, and modes change on their own - on AC/DC, from the tray, from hotkeys -
        // so a cap the user had just set disappeared without them touching anything. "Follow this mode"
        // is now an explicit action (RESET), not a side effect of where the machine happens to be.
        if (saved.Pl1 == 0 && saved.Pl2 == 0) return;

        var pl1 = Math.Min(saved.Pl1 == 0 ? mode.Pl1 : saved.Pl1, mode.Pl1);
        var pl2 = Math.Min(saved.Pl2 == 0 ? mode.Pl2 : saved.Pl2, mode.Pl2);
        var desired = new Limits(pl1, pl2);

        if (pl1 == now.Pl1 && pl2 == now.Pl2) return;

        var ask = LiveAsk();
        if (!ShouldWrite(now, ask, desired, modeChanged))
        {
            NoteDip(now, ask);
            return;
        }

        Apply(pl1, pl2);
    }

    /// <summary>
    /// Whether a re-assert should touch the register.
    ///
    /// Raising, a mode change, and a register the firmware has taken back to its own numbers all write.
    /// A cap that merely sits above a request that dipped does not: the firmware lowers its own request
    /// on its own (DTT, or the charger sharing power with a charging battery), and writing that dip down
    /// turns the user's cap into a smaller number they never chose. The card still shows both numbers, so
    /// a register above the current request stays visible.
    /// </summary>
    internal static bool ShouldWrite(Limits now, Limits? ask, Limits desired, bool modeChanged)
    {
        if (desired == now) return false;
        if (modeChanged) return true;
        if (desired.Pl1 > now.Pl1 || desired.Pl2 > now.Pl2) return true;

        // Lowering only where the register still holds the firmware's own pair: then no cap is applied
        // yet, which is a fresh start or a firmware rewrite rather than a dip.
        return ask is { } live && now == live;
    }

    /// <summary>Logs a dip once per distinct pair, instead of once per refresh.</summary>
    private static void NoteDip(Limits now, Limits? ask)
    {
        if (ask is not { } live) return;
        if (_dipNoted == (now, live)) return;

        _dipNoted = (now, live);
        Logger.Info($"CPU power cap: leaving {now.Pl1}/{now.Pl2} W in place; this mode now asks " +
                    $"{live.Pl1}/{live.Pl2} W. A dip in the mode's own request is not written over the cap.");
    }

    /// <summary>
    /// The firmware has rewritten MCHBAR - a mode was applied, or the machine resumed - so the copy can be
    /// trusted again.
    /// </summary>
    public static void NoteFirmwareRewrite()
    {
        lock (Gate)
        {
            _moved = false;
            _pristine = null;
            _lastApplied = null;
            _dipNoted = null;
        }
    }

    /// <summary>
    /// Picks up the remembered ceilings in a process that did not perform the write, so a cap applied by
    /// the tray or by an earlier dashboard is still bounded correctly.
    /// </summary>
    internal static void Load()
    {
        lock (Gate)
        {
            _moved = false;
            _pristine = null;
            _lastApplied = null;
            _dipNoted = null;

            var pl1 = SettingsManager.Get(Pl1CeilingKey, 0);
            var pl2 = SettingsManager.Get(Pl2CeilingKey, 0);
            if (pl1 <= 0 || pl2 <= 0) return;

            _moved = true;
            _pristine = new Limits(pl1, pl2);
        }
    }

    private static Result Fail(string message)
    {
        Logger.Warn($"CPU power cap: {message}");
        return new Result(false, message);
    }
}
