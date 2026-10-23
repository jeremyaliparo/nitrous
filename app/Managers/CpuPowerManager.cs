using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

/// <summary>
/// Applies the "processor power management" min/max state to the active Windows power scheme.
///
/// The old implementation wrote 5/100 on every AC/DC event and called it a restore. That both
/// destroyed the user's original values (this machine ships with 5/99 on DC) and could leave
/// min &gt; max when switching profiles (e.g. Turbo's min=100 while the previous max was 95).
/// This version backs up the original values, serializes changes across the tray/UI processes,
/// orders the writes so min can never exceed max, and verifies the result.
/// </summary>
public static class CpuPowerManager
{
    private const string ProcessorSubgroup = "SUB_PROCESSOR";
    private const string MinSetting = "PROCTHROTTLEMIN";
    private const string MaxSetting = "PROCTHROTTLEMAX";
    private const string MutexName = @"Local\Nitrous_PowerCfg_Mutex";
    private const string BackupPrefix = "CpuPowerBackup";
    private const int MinPercent = 0;
    private const int MaxPercent = 100;

    private static readonly object RequestGate = new();
    private static readonly Regex SchemeRegex = new(
        @"(?<guid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);
    private static readonly Regex BackupRegex = new(
        @"^" + BackupPrefix + @"_(?<guid>[0-9a-fA-F-]{36})_(?<side>AC|DC)_(?<kind>Min|Max)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Locale-independent processor power-setting identifiers.
    private static readonly Guid ProcessorSubgroupGuid = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MinSettingGuid = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid MaxSettingGuid = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    private static PowerProfile _pendingProfile;
    private static bool _pendingIsOnline;
    private static int _pendingVersion;
    private static long _lastAppliedVersion;

    /// <summary>Apply the CPU state for a profile on the current AC/DC side.</summary>
    public static Task ApplyProfileLimitsAsync(PowerProfile profile, bool isOnline)
    {
        int version;
        lock (RequestGate)
        {
            // Coalesce rapid profile switches: only the newest request is allowed to run.
            _pendingProfile = profile;
            _pendingIsOnline = isOnline;
            version = ++_pendingVersion;
        }

        return Task.Run(() => ApplyProfile(version));
    }

    /// <summary>
    /// Restore every backed-up scheme to the user's original min/max values, then remove the
    /// backups. Safe to call when nothing was ever applied.
    /// </summary>
    public static Task RestoreOriginalAsync() => Task.Run(RestoreOriginal);

    /// <summary>
    /// Synchronous variant used during process shutdown, where a fire-and-forget task would be
    /// killed with the process before it finished restoring the CPU state.
    /// </summary>
    public static void RestoreOriginalSynchronously() => RestoreOriginal();

    private static void ApplyProfile(int version)
    {
        if (!TryAcquireMutex(TimeSpan.FromSeconds(5), out var mutex))
        {
            Logger.Warn("CPU power: could not acquire powercfg mutex; skipping apply.");
            return;
        }

        try
        {
            PowerProfile profile;
            bool isOnline;

            lock (RequestGate)
            {
                if (version != _pendingVersion)
                {
                    Logger.Info($"CPU power: coalesced stale request v{version} (latest v{_pendingVersion}).");
                    return;
                }

                if (version <= _lastAppliedVersion)
                {
                    return;
                }

                profile = _pendingProfile;
                isOnline = _pendingIsOnline;
            }

            // The user may have disabled the feature while this request was queued. Never
            // re-apply after that point: the restore may already have run.
            if (SettingsManager.Get("ManageCpuPower", 0) != 1)
            {
                Logger.Info("CPU power: feature is disabled; skipping queued apply.");
                return;
            }

            var scheme = GetActiveSchemeGuid();
            if (scheme == null)
            {
                Logger.Warn("CPU power: could not determine the active power scheme.");
                return;
            }

            if (!EnsureBackup(scheme))
            {
                Logger.Warn("CPU power: could not back up the current processor state; refusing to modify it.");
                return;
            }

            var target = GetTarget(profile, isOnline);
            var applied = ApplySide(scheme, isOnline, target.Min, target.Max);
            if (applied)
            {
                _lastAppliedVersion = version;
                Logger.Info($"CPU power: {profile} ({(isOnline ? "AC" : "DC")}) -> min {target.Min}%, max {target.Max}%.");
            }
            else
            {
                Logger.Warn($"CPU power: failed to apply {profile} ({(isOnline ? "AC" : "DC")}) " +
                            $"min {target.Min}%, max {target.Max}%.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CPU power: apply failed.", ex);
        }
        finally
        {
            ReleaseMutex(mutex);
        }
    }

    private static void RestoreOriginal()
    {
        if (!TryAcquireMutex(TimeSpan.FromSeconds(3), out var mutex))
        {
            Logger.Warn("CPU power: could not acquire powercfg mutex; skipping restore.");
            return;
        }

        try
        {
            var backups = ReadBackups();
            if (backups.Count == 0) return;

            foreach (var group in backups.GroupBy(b => b.SchemeGuid, StringComparer.OrdinalIgnoreCase))
            {
                var scheme = group.Key;
                var ac = group.Where(b => b.Side == "AC").ToDictionary(b => b.Kind, b => b.Value, StringComparer.OrdinalIgnoreCase);
                var dc = group.Where(b => b.Side == "DC").ToDictionary(b => b.Kind, b => b.Value, StringComparer.OrdinalIgnoreCase);

                var ok = true;
                if (ac.TryGetValue("Min", out var acMin) && ac.TryGetValue("Max", out var acMax))
                    ok &= ApplySide(scheme, isOnline: true, acMin, acMax);

                if (dc.TryGetValue("Min", out var dcMin) && dc.TryGetValue("Max", out var dcMax))
                    ok &= ApplySide(scheme, isOnline: false, dcMin, dcMax);

                if (ok)
                {
                    foreach (var backup in group) SettingsManager.Delete(backup.Key);
                    Logger.Info($"CPU power: restored original processor state for scheme {scheme}.");
                }
                else
                {
                    Logger.Warn($"CPU power: could not fully restore scheme {scheme}; backups retained.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CPU power: restore failed.", ex);
        }
        finally
        {
            ReleaseMutex(mutex);
        }
    }

    private static (int Min, int Max) GetTarget(PowerProfile profile, bool isOnline)
    {
        if (isOnline) // AC
        {
            return profile switch
            {
                PowerProfile.Quiet => (5, 95),
                PowerProfile.Balanced => (5, 100),
                PowerProfile.Performance => (5, 100),
                PowerProfile.Turbo => (100, 100),
                _ => (5, 100)
            };
        }

        // DC (battery)
        return profile switch
        {
            PowerProfile.Quiet => (5, 88),
            PowerProfile.Balanced => (5, 99),
            PowerProfile.Performance => (5, 100),
            PowerProfile.Turbo => (5, 100),
            _ => (5, 100)
        };
    }

    private static bool ApplySide(string scheme, bool isOnline, int desiredMin, int desiredMax)
    {
        desiredMin = Math.Clamp(desiredMin, MinPercent, MaxPercent);
        desiredMax = Math.Clamp(desiredMax, MinPercent, MaxPercent);
        if (desiredMin > desiredMax) desiredMin = desiredMax;

        var currentMin = ReadSetting(scheme, MinSetting, isOnline);
        var currentMax = ReadSetting(scheme, MaxSetting, isOnline);

        if (currentMin == desiredMin && currentMax == desiredMax)
        {
            return true; // idempotent: never rewrite (or "restore") values that already match
        }

        // A write sequence is only valid while min <= max. If the change crosses the current
        // opposite bound, drop min to 0 first, then set max, then set the real min. If the
        // current bounds could not be read, use that same safe sequence.
        var crossing = currentMin == null || currentMax == null ||
                       currentMin > desiredMax || desiredMin > currentMax;
        if (crossing)
        {
            if (!WriteSetting(scheme, MinSetting, isOnline, 0)) return false;
            if (!WriteSetting(scheme, MaxSetting, isOnline, desiredMax)) return false;
            if (!WriteSetting(scheme, MinSetting, isOnline, desiredMin)) return false;
        }
        else
        {
            if (!WriteSetting(scheme, MinSetting, isOnline, desiredMin)) return false;
            if (!WriteSetting(scheme, MaxSetting, isOnline, desiredMax)) return false;
        }

        ActivateScheme(scheme);

        var appliedMin = ReadSetting(scheme, MinSetting, isOnline);
        var appliedMax = ReadSetting(scheme, MaxSetting, isOnline);
        if (appliedMin == desiredMin && appliedMax == desiredMax) return true;

        Logger.Warn($"CPU power: verification failed for {(isOnline ? "AC" : "DC")} " +
                    $"(wanted {desiredMin}/{desiredMax}, read {appliedMin?.ToString() ?? "?"}/{appliedMax?.ToString() ?? "?"}).");

        // One retry using the fully ordered sequence; some firmware/Windows builds reject
        // the direct order even when the bounds are valid.
        if (!crossing)
        {
            if (!WriteSetting(scheme, MinSetting, isOnline, 0)) return false;
            if (!WriteSetting(scheme, MaxSetting, isOnline, desiredMax)) return false;
            if (!WriteSetting(scheme, MinSetting, isOnline, desiredMin)) return false;
            ActivateScheme(scheme);
            appliedMin = ReadSetting(scheme, MinSetting, isOnline);
            appliedMax = ReadSetting(scheme, MaxSetting, isOnline);
        }

        return appliedMin == desiredMin && appliedMax == desiredMax;
    }

    private static bool EnsureBackup(string scheme)
    {
        // If any backup exists for this scheme, it is authoritative: it was captured before
        // we touched the values. Do not overwrite it with possibly already-managed values.
        var existing = SettingsManager.GetValueNames();
        var prefix = $"{BackupPrefix}_{scheme}_";

        var existingForScheme = existing
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingForScheme.Count > 0)
        {
            var required = new[]
            {
                $"{prefix}AC_Min", $"{prefix}AC_Max",
                $"{prefix}DC_Min", $"{prefix}DC_Max"
            };

            if (required.All(existingForScheme.Contains))
                return true;

            // A partial set can only be the result of an interrupted very first backup. Do not
            // capture the remaining (possibly already-managed) values on top of it.
            Logger.Warn($"CPU power: incomplete backup set for scheme {scheme}; refusing to modify it.");
            return false;
        }

        var acMin = ReadSetting(scheme, MinSetting, isOnline: true);
        var acMax = ReadSetting(scheme, MaxSetting, isOnline: true);
        var dcMin = ReadSetting(scheme, MinSetting, isOnline: false);
        var dcMax = ReadSetting(scheme, MaxSetting, isOnline: false);

        if (acMin == null || acMax == null || dcMin == null || dcMax == null)
            return false;

        SettingsManager.Save($"{BackupPrefix}_{scheme}_AC_Min", acMin.Value);
        SettingsManager.Save($"{BackupPrefix}_{scheme}_AC_Max", acMax.Value);
        SettingsManager.Save($"{BackupPrefix}_{scheme}_DC_Min", dcMin.Value);
        SettingsManager.Save($"{BackupPrefix}_{scheme}_DC_Max", dcMax.Value);
        Logger.Info($"CPU power: backed up scheme {scheme} (AC {acMin}/{acMax}, DC {dcMin}/{dcMax}).");
        return true;
    }

    private static List<BackupEntry> ReadBackups()
    {
        var result = new List<BackupEntry>();
        foreach (var name in SettingsManager.GetValueNames())
        {
            var match = BackupRegex.Match(name);
            if (!match.Success) continue;

            var value = SettingsManager.Get(name, int.MinValue);
            if (value == int.MinValue) continue;

            result.Add(new BackupEntry(
                name,
                match.Groups["guid"].Value,
                match.Groups["side"].Value.ToUpperInvariant(),
                match.Groups["kind"].Value,
                value));
        }

        return result;
    }

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        try
        {
            var scheme = GetActiveSchemeGuid();
            result["activeSchemeGuid"] = scheme;
            result["manageCpuPower"] = SettingsManager.Get("ManageCpuPower", 0) == 1;

            if (scheme != null)
            {
                result["ac"] = new Dictionary<string, object?>
                {
                    ["min"] = ReadSetting(scheme, MinSetting, isOnline: true),
                    ["max"] = ReadSetting(scheme, MaxSetting, isOnline: true)
                };
                result["dc"] = new Dictionary<string, object?>
                {
                    ["min"] = ReadSetting(scheme, MinSetting, isOnline: false),
                    ["max"] = ReadSetting(scheme, MaxSetting, isOnline: false)
                };
            }

            result["backups"] = ReadBackups()
                .Select(b => new Dictionary<string, object?>
                {
                    ["key"] = b.Key,
                    ["schemeGuid"] = b.SchemeGuid,
                    ["side"] = b.Side,
                    ["kind"] = b.Kind,
                    ["value"] = b.Value
                })
                .ToList();
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            Logger.Warn("CPU power: diagnostics snapshot failed.", ex);
        }

        result["errors"] = errors;
        return result;
    }

    private static string? GetActiveSchemeGuid()
    {
        // PowerGetActiveScheme is locale-independent; parsing localized powercfg output is not.
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var activeGuidPtr) == 0 &&
                activeGuidPtr != IntPtr.Zero)
            {
                try
                {
                    var guid = Marshal.PtrToStructure<Guid>(activeGuidPtr);
                    return guid.ToString("D");
                }
                finally
                {
                    LocalFree(activeGuidPtr);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("CPU power: PowerGetActiveScheme failed; falling back to powercfg.", ex);
        }

        var result = RunPowerCfg("/getactivescheme");
        if (!result.Success) return null;

        var match = SchemeRegex.Match(result.Output);
        return match.Success ? match.Groups["guid"].Value : null;
    }

    private static int? ReadSetting(string scheme, string setting, bool isOnline)
    {
        // Prefer the documented power API. The powercfg text output is localized, so parsing
        // "Current AC Power Setting Index" silently fails on non-English Windows.
        try
        {
            var schemeGuid = Guid.Parse(scheme);
            var subgroupGuid = ProcessorSubgroupGuid;
            var settingGuid = string.Equals(setting, MinSetting, StringComparison.OrdinalIgnoreCase)
                ? MinSettingGuid
                : MaxSettingGuid;

            uint result;
            uint value;
            if (isOnline)
                result = PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, out value);
            else
                result = PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroupGuid, ref settingGuid, out value);

            if (result == 0)
                return (int)value;
            Logger.Warn($"CPU power: could not read {setting} ({(isOnline ? "AC" : "DC")}) via powrprof (error {result}).");
        }
        catch (Exception ex)
        {
            Logger.Warn($"CPU power: PowerRead value call failed for {setting}.", ex);
        }

        // Fallback for diagnostics only. This parser assumes an English powercfg output.
        var query = RunPowerCfg($"/query {scheme} {ProcessorSubgroup} {setting}");
        if (!query.Success) return null;

        var prefix = isOnline
            ? "Current AC Power Setting Index:"
            : "Current DC Power Setting Index:";

        foreach (var rawLine in query.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[prefix.Length..].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    private static bool WriteSetting(string scheme, string setting, bool isOnline, int value)
    {
        var verb = isOnline ? "/setacvalueindex" : "/setdcvalueindex";
        var result = RunPowerCfg($"{verb} {scheme} {ProcessorSubgroup} {setting} {value}");
        if (result.Success) return true;

        Logger.Warn($"CPU power: powercfg failed for {setting}={value} ({verb}): {result.Output.Trim()}");
        return false;
    }

    private static void ActivateScheme(string scheme)
    {
        // Do not switch the user's active plan merely because we restored a backup for a
        // different plan. Only the currently active scheme needs re-activation.
        var active = GetActiveSchemeGuid();
        if (!string.Equals(active, scheme, StringComparison.OrdinalIgnoreCase)) return;

        var result = RunPowerCfg($"/setactive {scheme}");
        if (!result.Success)
            Logger.Warn($"CPU power: powercfg /setactive {scheme} failed: {result.Output.Trim()}");
    }

    private static (bool Success, string Output) RunPowerCfg(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("powercfg.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();

            // Read both pipes concurrently; reading one to completion while the other fills
            // its buffer can deadlock.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return (false, "powercfg timed out");
            }

            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            Logger.Warn($"CPU power: could not run powercfg {arguments}.", ex);
            return (false, ex.Message);
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid powerSettingGuid,
        out uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid powerSettingGuid,
        out uint valueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static bool TryAcquireMutex(TimeSpan timeout, out Mutex mutex)
    {
        mutex = new Mutex(false, MutexName);
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died while holding it; we now own it.
            return true;
        }
        catch
        {
            mutex.Dispose();
            mutex = null!;
            return false;
        }
    }

    private static void ReleaseMutex(Mutex mutex)
    {
        try { mutex.ReleaseMutex(); } catch { /* not owned */ }
        finally { mutex.Dispose(); }
    }

    private sealed record BackupEntry(string Key, string SchemeGuid, string Side, string Kind, int Value);
}
