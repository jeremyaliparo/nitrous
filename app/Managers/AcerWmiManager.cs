using System;
using System.Management;
using System.Threading.Tasks;
using Nitrous.Diagnostics;
using Nitrous.Enums;
using Nitrous.Helpers;

namespace Nitrous.Managers;

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
    private static bool? _turboSupported;
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

    public static bool IsTurboModeSupported()
    {
        lock (Gate)
        {
            if (_turboSupported.HasValue) return _turboSupported.Value;

            try
            {
                var instance = GetGamingInstanceLocked();
                if (instance == null)
                {
                    _turboSupported = false;
                    return false;
                }

                using var inParams = instance.GetMethodParameters(AcerWmiProtocol.GetGamingMiscSetting);
                inParams["gmInput"] = AcerWmiProtocol.SupportedProfilesSetting;

                using var outParams = instance.InvokeMethod(AcerWmiProtocol.GetGamingMiscSetting, inParams, null);
                var rawValue = outParams?.Properties["gmOutput"]?.Value ?? outParams?.Properties["outValue"]?.Value;
                if (rawValue == null)
                {
                    _turboSupported = false;
                    return false;
                }

                var supportedBitmap = (int)((Convert.ToUInt64(rawValue) >> 8) & 0xFF);
                _turboSupported = (supportedBitmap & (1 << AcerWmiProtocol.TurboProfileBit)) != 0;
            }
            catch (Exception ex)
            {
                Logger.Warn("WMI: failed to query supported power profiles.", ex);
                InvalidateGamingLocked();
                _turboSupported = false;
            }

            return _turboSupported.Value;
        }
    }

    public static Task SetPowerModeAsync(PowerProfile profile)
    {
        var payload = AcerWmiProtocol.PowerModePayload(profile).ToString();
        return Task.Run(() =>
        {
            lock (Gate)
            {
                if (!InvokeGamingLocked(AcerWmiProtocol.SetGamingMiscSetting, payload))
                    Logger.Warn($"WMI: failed to set power mode {profile}.");
            }
        });
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
