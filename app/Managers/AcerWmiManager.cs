using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;

namespace Nitrous.Managers;

/// <summary>Power-off USB charging: on or off, and the battery level it stops at (null: none).</summary>
public readonly record struct UsbChargingState(bool On, int? Floor);

public static class AcerWmiManager
{
    private static readonly object Gate = new();

    // A cached WMI instance avoids creating a searcher + collection for every sensor read
    // (the old code did that up to six times every two seconds, in two processes).
    private static ManagementObjectSearcher? _gamingSearcher;
    private static ManagementObjectCollection? _gamingCollection;
    private static ManagementObject? _gaming;

    private static ManagementObjectSearcher? _batterySearcher;
    private static ManagementObjectCollection? _batteryCollection;
    private static ManagementObject? _battery;

    private static bool? _hardwareSupported;
    private static bool _telemetryFailureLogged;
    private static bool _batteryFailureLogged;

    public static bool IsHardwareSupported()
    {
        lock (Gate)
        {
            if (_hardwareSupported.HasValue) return _hardwareSupported.Value;

            try
            {
                _hardwareSupported = GetGamingInstanceLocked() != null;
            }
            catch (Exception ex)
            {
                Logger.Warn("WMI: AcerGamingFunction is not available.", ex);
                InvalidateGamingLocked();
                _hardwareSupported = false;
            }

            if (_hardwareSupported == false)
                Logger.Warn("WMI: Acer gaming WMI interface not found (unsupported model, or not elevated).");

            return _hardwareSupported.Value;
        }
    }

    /// <summary>
    /// Whether Turbo can be selected here. Asked of the capability probe, so it agrees with the
    /// channel the mode write actually uses: the WMI mask alone hid Turbo on models whose
    /// controller lists the mode but whose register does not advertise it.
    /// </summary>
    public static bool IsTurboModeSupported()
    {
        try
        {
            var capabilities = AcerCapabilitiesProvider.Current;
            var supported =
                (capabilities.PowerModeChannel is PowerModeChannel.EcHid or PowerModeChannel.Wmi) &&
                capabilities.SupportsTurbo;

            // An empty WMI mask is more often a read that failed once than a model with no modes,
            // so ask again rather than caching "no Turbo" for the session.
            if (!supported &&
                capabilities.PowerModeChannel == PowerModeChannel.Wmi &&
                capabilities.Wmi.SupportedProfiles.Count == 0)
            {
                supported = AcerCapabilitiesProvider.Refresh().SupportsTurbo;
            }

            return supported;
        }
        catch (Exception ex)
        {
            Logger.Warn("Capabilities: could not determine whether Turbo is supported.", ex);
            AcerCapabilitiesProvider.Invalidate();
            return false;
        }
    }

    public static Task SetPowerModeAsync(PowerProfile profile)
    {
        return Task.Run(() =>
        {
            var capabilities = AcerCapabilitiesProvider.Current;

            if (capabilities.PowerModeChannel == PowerModeChannel.EcHid &&
                capabilities.EcHid is { Modes.Count: > 0 } ecHid &&
                TrySetPowerModeViaEcHid(profile, ecHid))
            {
                return;
            }

            lock (Gate)
            {
                if (SetPowerModeViaWmiLocked(profile, capabilities.Profile))
                    return;
            }

            if (capabilities.PowerModeChannel == PowerModeChannel.ActionFunction7)
            {
                Logger.Warn(
                    $"Power mode {profile} was not applied: this model keeps the mode on APGeAction " +
                    "function 7, which Nitrous does not write yet.");
                return;
            }

            Logger.Warn($"WMI: failed to set power mode {profile}.");
        });
    }

    /// <summary>
    /// The operating mode the EC's HID interface reports, or null when this model has none. Reading
    /// never changes anything and never touches the GPU.
    /// </summary>
    public static PowerProfile? GetEcPowerMode()
    {
        if (AcerCapabilitiesProvider.Current.EcHid is not { Modes.Count: > 0 } info) return null;

        try
        {
            using var hid = AcerEcHid.OpenWith(info.Modes);
            return hid?.ReadMode();
        }
        catch (Exception ex)
        {
            Logger.Warn("EC HID: could not read the power mode.", ex);
            AcerCapabilitiesProvider.Invalidate();
            return null;
        }
    }

    /// <summary>
    /// The keyboard backlight's brightness and auto-off (seconds, 0 = never), or null when this
    /// model has no EC HID interface.
    /// </summary>
    public static (int Brightness, int TimeoutSeconds)? GetKeyboardBacklightTimeout()
    {
        try
        {
            using var hid = AcerEcHid.Open();
            return hid?.ReadBacklightTimeout();
        }
        catch (Exception ex)
        {
            Logger.Warn("EC HID: could not read the keyboard backlight timeout.", ex);
            return null;
        }
    }

    /// <summary>
    /// Sets the keyboard backlight auto-off, in seconds (0 = never). The write carries brightness
    /// too, so the controller's current level is read and preserved unless the caller supplies one.
    /// </summary>
    public static bool SetKeyboardBacklightTimeout(int timeoutSeconds, int? brightness = null)
    {
        try
        {
            using var hid = AcerEcHid.Open();
            if (hid == null) return false;

            return brightness is { } level
                ? hid.WriteBacklightTimeout(level, timeoutSeconds)
                : hid.SetBacklightTimeout(timeoutSeconds);
        }
        catch (Exception ex)
        {
            Logger.Warn("EC HID: could not set the keyboard backlight timeout.", ex);
            AcerCapabilitiesProvider.Invalidate();
            return false;
        }
    }

    /// <summary>
    /// Power-off USB charging as the firmware has it; null when this model does not answer
    /// (the APGeAction class is only partly implemented on some BIOSes).
    /// </summary>
    public static UsbChargingState? GetUsbCharging()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace, $"SELECT * FROM {AcerWmiProtocol.ActionClass}");

            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("GetFunction");
                    inParams["uiInput"] = AcerWmiProtocol.UsbChargingQuery;
                    using var outParams = instance.InvokeMethod("GetFunction", inParams, null);

                    var raw = outParams?.Properties["uiOutput"]?.Value;
                    if (raw == null) return null;

                    var value = Convert.ToUInt64(raw);
                    var state = (int)((value >> 8) & 0xFF);
                    var floor = (int)((value >> 16) & 0xFF);

                    return new UsbChargingState(
                        state == AcerWmiProtocol.UsbChargingOn,
                        AcerWmiProtocol.UsbChargingFloors.Contains(floor) ? floor : null);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WMI: could not read USB charging.", ex);
        }

        return null;
    }

    /// <summary>Enables or disables power-off USB charging, stopping at the given battery level.</summary>
    public static bool SetUsbCharging(bool on, int floor)
    {
        if (!AcerWmiProtocol.UsbChargingFloors.Contains(floor)) return false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                AcerWmiProtocol.WmiNamespace, $"SELECT * FROM {AcerWmiProtocol.ActionClass}");

            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("SetFunction");
                    inParams["uiInput"] = AcerWmiProtocol.UsbChargingPayload(on, floor);
                    using var outParams = instance.InvokeMethod("SetFunction", inParams, null);
                    return outParams != null;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("WMI: could not set USB charging.", ex);
        }

        return false;
    }

    /// <summary>
    /// LCD overdrive: whether the panel has it, and whether it is on. Null when the firmware does
    /// not answer. Overdrive is a panel property, so 0xFF means this panel does not have it.
    /// </summary>
    public static (bool Supported, bool On)? GetLcdOverdrive()
    {
        lock (Gate)
        {
            if (ReadGamingMethodLocked(AcerWmiProtocol.GetGamingProfile, AcerWmiProtocol.ProfileQuery)
                is not { } raw)
            {
                return null;
            }

            var supported = AcerWmiProtocol.LcdOverdriveSupported(raw);
            return (supported, supported && AcerWmiProtocol.LcdOverdriveOn(raw));
        }
    }

    /// <summary>Turns LCD overdrive on or off.</summary>
    public static bool SetLcdOverdrive(bool on)
    {
        lock (Gate)
        {
            return InvokeGamingLocked(
                AcerWmiProtocol.SetGamingProfile,
                AcerWmiProtocol.LcdOverdrivePayload(on).ToString());
        }
    }

    /// <summary>Invokes a gaming method that answers a value; used by the profile reads.</summary>
    private static ulong? ReadGamingMethodLocked(string methodName, uint input)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var instance = GetGamingInstanceLocked();
                if (instance == null) return null;

                using var inParams = instance.GetMethodParameters(methodName);
                inParams["gmInput"] = input;
                using var outParams = instance.InvokeMethod(methodName, inParams, null);

                var raw = outParams?.Properties["gmOutput"]?.Value;
                return raw == null ? null : Convert.ToUInt64(raw);
            }
            catch (Exception ex)
            {
                InvalidateGamingLocked();
                if (attempt == 1) Logger.Warn($"WMI: {methodName} failed.", ex);
            }
        }

        return null;
    }

    private static bool TrySetPowerModeViaEcHid(PowerProfile profile, EcHidInfo info)
    {
        try
        {
            // The mode list came from the capability probe, so opening costs no extra exchanges.
            using var hid = AcerEcHid.OpenWith(info.Modes);
            if (hid == null)
            {
                AcerCapabilitiesProvider.Invalidate();
                return false;
            }

            if (!hid.WriteMode(profile))
            {
                Logger.Warn($"EC HID: the controller refused power mode {profile}.");
                AcerCapabilitiesProvider.Invalidate();
                return false;
            }

            // Deliberately no WMI mirror write here. AcerQAAgent, which NitroSense drives, sends only
            // the HID Mode report (captured 31 Oct): Mode Set, then Mode Get. The extra
            // SetGamingMiscSetting(0x0B) write makes the controller acknowledge a second time with
            // its own colour, which is why Nitrous flashed one colour for every profile while
            // NitroSense showed the firmware's per-mode colour. The WMI register is informational
            // and reads back stale, exactly as Acer's own software leaves it.

            var readBack = hid.ReadMode();
            Logger.Info($"EC HID: power mode set to {profile} (read back {readBack?.ToString() ?? "unknown"}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("EC HID: power mode write failed.", ex);
            AcerCapabilitiesProvider.Invalidate();
            return false;
        }
    }

    /// <summary>
    /// The WMI fallback for models without the EC HID interface. Some firmware moves the mode
    /// relative to the one in force and only Balanced resets the Intel DTT profile index, so on
    /// those models the target has to go out from Balanced (see <see cref="AcerModelProfile"/>).
    /// </summary>
    private static bool SetPowerModeViaWmiLocked(PowerProfile profile, AcerModelProfile model)
    {
        if (model.RequiresBalancedModeReset && profile != PowerProfile.Balanced &&
            !InvokeGamingLocked(
                AcerWmiProtocol.SetGamingMiscSetting,
                AcerWmiProtocol.PowerModePayload(PowerProfile.Balanced).ToString()))
        {
            return false;
        }

        return InvokeGamingLocked(
            AcerWmiProtocol.SetGamingMiscSetting,
            AcerWmiProtocol.PowerModePayload(profile).ToString());
    }

    public static Task<bool> SetFansAsync(FanProfile profile)
    {
        return Task.Run(() =>
        {
            lock (Gate)
            {
                switch (profile)
                {
                    case FanProfile.Auto:
                        return InvokeGamingLocked(AcerWmiProtocol.SetGamingFanBehavior, AcerWmiProtocol.FanBehaviorAuto.ToString());

                    case FanProfile.Max:
                        return InvokeGamingLocked(AcerWmiProtocol.SetGamingFanBehavior, AcerWmiProtocol.FanBehaviorMax.ToString());

                    default:
                        // Legacy Quiet/Medium values act as a fixed custom percentage.
                        var speed = FanSafety.Clamp((int)profile);
                        var behavior = InvokeGamingLocked(AcerWmiProtocol.SetGamingFanBehavior, AcerWmiProtocol.FanBehaviorCustom.ToString());
                        var cpu = InvokeFanSpeedLocked(AcerWmiProtocol.CpuFanDevice, speed);
                        var gpuA = InvokeFanSpeedLocked(AcerWmiProtocol.GpuFanDeviceA, speed);
                        var gpuB = InvokeFanSpeedLocked(AcerWmiProtocol.GpuFanDeviceB, speed);
                        return behavior && cpu && gpuA && gpuB;
                }
            }
        });
    }

    public static Task<bool> SetCustomFansAsync(int cpuSpeed, int gpuSpeed)
    {
        // Clamp at the hardware boundary as well as in the UI so a stale registry value
        // or curve cannot request 0% or an out-of-range speed.
        cpuSpeed = FanSafety.Clamp(cpuSpeed);
        gpuSpeed = FanSafety.Clamp(gpuSpeed);

        return Task.Run(() =>
        {
            lock (Gate)
            {
                var behavior = InvokeGamingLocked(AcerWmiProtocol.SetGamingFanBehavior, AcerWmiProtocol.FanBehaviorCustom.ToString());
                var cpu = InvokeFanSpeedLocked(AcerWmiProtocol.CpuFanDevice, cpuSpeed);
                var gpuA = InvokeFanSpeedLocked(AcerWmiProtocol.GpuFanDeviceA, gpuSpeed);
                var gpuB = InvokeFanSpeedLocked(AcerWmiProtocol.GpuFanDeviceB, gpuSpeed);
                return behavior && cpu && gpuA && gpuB;
            }
        });
    }

    public static (int CpuTemp, int CpuRpm, int GpuTemp, int GpuRpm) GetSystemTelemetry()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var instance = GetGamingInstanceLocked();
                    if (instance == null) return default;

                    var cpuTemp = ReadAcerSensor(instance, AcerWmiProtocol.CpuTemperature, AcerWmiProtocol.ByteMask);
                    var cpuRpm = ReadAcerSensor(instance, AcerWmiProtocol.CpuFanRpm, AcerWmiProtocol.WordMask);
                    var gpuRpm = ReadAcerSensor(instance, AcerWmiProtocol.GpuFanRpm, AcerWmiProtocol.WordMask);

                    var gpuTemp = ReadAcerSensor(instance, AcerWmiProtocol.GpuTemperaturePrimary, AcerWmiProtocol.ByteMask);
                    if (gpuTemp == 0) gpuTemp = ReadAcerSensor(instance, AcerWmiProtocol.GpuTemperatureFallbackA, AcerWmiProtocol.ByteMask);
                    if (gpuTemp == 0) gpuTemp = ReadAcerSensor(instance, AcerWmiProtocol.GpuTemperatureFallbackB, AcerWmiProtocol.ByteMask);

                    if (cpuTemp == null && cpuRpm == null && gpuTemp == null && gpuRpm == null)
                        throw new InvalidOperationException("All Acer sensor reads failed.");

                    _telemetryFailureLogged = false;
                    return (cpuTemp ?? 0, cpuRpm ?? 0, gpuTemp ?? 0, gpuRpm ?? 0);
                }
                catch (Exception ex)
                {
                    InvalidateGamingLocked();
                    if (attempt == 0) continue;

                    if (!_telemetryFailureLogged)
                    {
                        Logger.Warn("WMI: telemetry read failed; will retry on the next poll.", ex);
                        _telemetryFailureLogged = true;
                    }
                }
            }

            return default;
        }
    }

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        try { result["hardwareSupported"] = IsHardwareSupported(); }
        catch (Exception ex) { errors.Add($"hardwareSupported: {ex.Message}"); }

        try { result["turboSupported"] = IsTurboModeSupported(); }
        catch (Exception ex) { errors.Add($"turboSupported: {ex.Message}"); }

        try
        {
            var telemetry = GetSystemTelemetry();
            result["telemetry"] = new Dictionary<string, object?>
            {
                ["cpuTemperatureC"] = telemetry.CpuTemp,
                ["cpuFanRpm"] = telemetry.CpuRpm,
                ["gpuTemperatureC"] = telemetry.GpuTemp,
                ["gpuFanRpm"] = telemetry.GpuRpm
            };
        }
        catch (Exception ex)
        {
            errors.Add($"telemetry: {ex.Message}");
            Logger.Warn("WMI: diagnostics telemetry read failed.", ex);
        }

        result["gamingClassAvailable"] = false;
        result["batteryClassAvailable"] = false;

        lock (Gate)
        {
            try
            {
                var gaming = GetGamingInstanceLocked();
                result["gamingClassAvailable"] = gaming != null;
                if (gaming != null)
                {
                    result["gamingClassPath"] = gaming.ClassPath?.ToString();
                    result["gamingMethods"] = GetClassMethods(AcerWmiProtocol.GamingClass);
                    result["supportedProfiles"] = ReadSupportedProfilesLocked(gaming);

                    var probes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    AddSensorProbe(probes, "cpuTemperature", gaming,
                        AcerWmiProtocol.CpuTemperature, AcerWmiProtocol.ByteMask);
                    AddSensorProbe(probes, "cpuFanRpm", gaming,
                        AcerWmiProtocol.CpuFanRpm, AcerWmiProtocol.WordMask);
                    AddSensorProbe(probes, "gpuTemperaturePrimary", gaming,
                        AcerWmiProtocol.GpuTemperaturePrimary, AcerWmiProtocol.ByteMask);
                    AddSensorProbe(probes, "gpuTemperatureFallbackA", gaming,
                        AcerWmiProtocol.GpuTemperatureFallbackA, AcerWmiProtocol.ByteMask);
                    AddSensorProbe(probes, "gpuTemperatureFallbackB", gaming,
                        AcerWmiProtocol.GpuTemperatureFallbackB, AcerWmiProtocol.ByteMask);
                    AddSensorProbe(probes, "gpuFanRpm", gaming,
                        AcerWmiProtocol.GpuFanRpm, AcerWmiProtocol.WordMask);
                    result["sensorProbes"] = probes;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"gamingClass: {ex.Message}");
                Logger.Warn("WMI: diagnostics gaming-class probe failed.", ex);
            }

            try
            {
                var battery = GetBatteryInstanceLocked();
                result["batteryClassAvailable"] = battery != null;
                if (battery != null)
                {
                    result["batteryClassPath"] = battery.ClassPath?.ToString();
                    result["batteryMethods"] = GetClassMethods(AcerWmiProtocol.BatteryClass);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"batteryClass: {ex.Message}");
                Logger.Warn("WMI: diagnostics battery-class probe failed.", ex);
            }
        }

        result["errors"] = errors;
        return result;
    }

    private static Dictionary<string, object?>? ReadSupportedProfilesLocked(ManagementObject instance)
    {
        try
        {
            using var inParams = instance.GetMethodParameters(AcerWmiProtocol.GetGamingMiscSetting);
            inParams["gmInput"] = AcerWmiProtocol.SupportedProfilesSetting;

            using var outParams = instance.InvokeMethod(AcerWmiProtocol.GetGamingMiscSetting, inParams, null);
            var rawValue = outParams?.Properties["gmOutput"]?.Value ?? outParams?.Properties["outValue"]?.Value;
            if (rawValue == null) return null;

            var raw = Convert.ToUInt64(rawValue);
            var bitmap = (int)((raw >> 8) & 0xFF);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["raw"] = raw,
                ["bitmap"] = bitmap,
                ["turboBit"] = AcerWmiProtocol.TurboProfileBit,
                ["turboSupported"] = (bitmap & (1 << AcerWmiProtocol.TurboProfileBit)) != 0
            };
        }
        catch (Exception ex)
        {
            Logger.Warn("WMI: diagnostics supported-profile query failed.", ex);
            return null;
        }
    }

    private static void AddSensorProbe(
        Dictionary<string, object?> probes,
        string name,
        ManagementObject instance,
        uint address,
        ulong bitmask)
    {
        probes[$"{name}Value"] = ReadAcerSensor(instance, address, bitmask);
        probes[$"{name}Address"] = $"0x{address:X4}";
    }

    private static string[] GetClassMethods(string className)
    {
        try
        {
            using var managementClass = new ManagementClass(
                AcerWmiProtocol.WmiNamespace, className, null);
            return managementClass.Methods
                .Cast<MethodData>()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"WMI: could not enumerate methods for {className}.", ex);
            return Array.Empty<string>();
        }
    }

    private static int? ReadAcerSensor(ManagementObject instance, uint address, ulong bitmask)
    {
        try
        {
            using var inParams = instance.GetMethodParameters(AcerWmiProtocol.GetGamingSysInfo);
            inParams["gmInput"] = address; // UInt32

            using var outParams = instance.InvokeMethod(AcerWmiProtocol.GetGamingSysInfo, inParams, null);
            var rawValue = outParams?.Properties["gmOutput"]?.Value ?? outParams?.Properties["outValue"]?.Value;
            if (rawValue == null) return null;

            return (int)((Convert.ToUInt64(rawValue) >> 8) & bitmask);
        }
        catch
        {
            // One sensor failing (e.g. a sleeping dGPU) must not discard the others.
            return null;
        }
    }

    public static Task SetChargeLimitAsync(bool enable)
    {
        return Task.Run(() =>
        {
            lock (Gate)
            {
                try
                {
                    var instance = GetBatteryInstanceLocked();
                    if (instance == null)
                    {
                        if (!_batteryFailureLogged)
                        {
                            Logger.Warn("WMI: BatteryControl is not available; charge limit was not applied.");
                            _batteryFailureLogged = true;
                        }
                        return;
                    }

                    using var inParams = instance.GetMethodParameters(AcerWmiProtocol.SetBatteryHealthControl);
                    inParams["uBatteryNo"] = AcerWmiProtocol.BatteryNumber;
                    inParams["uFunctionMask"] = AcerWmiProtocol.BatteryFunctionMask;
                    inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);
                    inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
                    instance.InvokeMethod(AcerWmiProtocol.SetBatteryHealthControl, inParams, null);
                }
                catch (Exception ex)
                {
                    Logger.Warn("WMI: failed to set the battery charge limit.", ex);
                    InvalidateBatteryLocked();
                }
            }
        });
    }

    private static bool InvokeFanSpeedLocked(ulong deviceMask, int percent)
    {
        var payload = AcerWmiProtocol.FanSpeedPayload(deviceMask, percent).ToString();
        if (!InvokeGamingLocked(AcerWmiProtocol.SetGamingFanSpeed, payload))
        {
            Logger.Warn($"WMI: failed to set fan speed for device {deviceMask} to {percent}%.");
            return false;
        }

        return true;
    }

    private static bool InvokeGamingLocked(string methodName, string input)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var instance = GetGamingInstanceLocked();
                if (instance == null) return false;

                using var inParams = instance.GetMethodParameters(methodName);
                inParams["gmInput"] = input;
                using var outParams = instance.InvokeMethod(methodName, inParams, null);
                return outParams != null;
            }
            catch (Exception ex)
            {
                InvalidateGamingLocked();
                if (attempt == 1) Logger.Warn($"WMI: {methodName} failed.", ex);
            }
        }

        return false;
    }

    private static ManagementObject? GetGamingInstanceLocked()
    {
        if (_gaming != null) return _gaming;

        _gamingSearcher = new ManagementObjectSearcher(
            AcerWmiProtocol.WmiNamespace,
            $"SELECT * FROM {AcerWmiProtocol.GamingClass}");
        _gamingCollection = _gamingSearcher.Get();

        foreach (ManagementObject instance in _gamingCollection)
        {
            _gaming = instance;
            break;
        }

        if (_gaming == null)
        {
            // No instance: release the empty searcher/collection so the next retry does not
            // overwrite (and leak) them.
            InvalidateGamingLocked();
        }

        return _gaming;
    }

    private static ManagementObject? GetBatteryInstanceLocked()
    {
        if (_battery != null) return _battery;

        _batterySearcher = new ManagementObjectSearcher(
            AcerWmiProtocol.WmiNamespace,
            $"SELECT * FROM {AcerWmiProtocol.BatteryClass}");
        _batteryCollection = _batterySearcher.Get();

        foreach (ManagementObject instance in _batteryCollection)
        {
            _battery = instance;
            break;
        }

        if (_battery == null)
            InvalidateBatteryLocked();

        return _battery;
    }

    private static void InvalidateGamingLocked()
    {
        try { _gaming?.Dispose(); } catch { }
        try { _gamingCollection?.Dispose(); } catch { }
        try { _gamingSearcher?.Dispose(); } catch { }
        _gaming = null;
        _gamingCollection = null;
        _gamingSearcher = null;
    }

    private static void InvalidateBatteryLocked()
    {
        try { _battery?.Dispose(); } catch { }
        try { _batteryCollection?.Dispose(); } catch { }
        try { _batterySearcher?.Dispose(); } catch { }
        _battery = null;
        _batteryCollection = null;
        _batterySearcher = null;
    }
}
