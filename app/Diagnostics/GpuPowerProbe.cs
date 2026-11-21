using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nitrous.Helpers;
using Nitrous.Managers;

namespace Nitrous.Diagnostics;

/// <summary>
/// Read-only probe of the driver's own GPU power-limit fields.
///
/// Answers one question before anyone writes anything: does this laptop's driver expose a settable
/// power limit, and does that limit move with the Acer power mode or the charger state? A limit that
/// tracks the mode means a per-profile control has something to compose with; a fixed one means the
/// control would be a single setting with no per-mode meaning.
///
/// It never writes, and it refuses to run while Windows reports the dGPU powered down - initialising
/// NVML is what brings that rail back up.
/// </summary>
public static class GpuPowerProbe
{
    private const string NvmlDll = "nvml.dll";
    private const uint TemperatureGpu = 0;

    public static int Run(int seconds, bool allowWake = false)
    {
        if (GpuPowerState.IsAsleep)
        {
            if (!allowWake)
            {
                Logger.Warn("GPU power probe: the dGPU is powered down; refusing to wake it. " +
                            "Run this with the GPU awake, or pass --wake to allow it.");
                return 1;
            }

            Logger.Info("GPU power probe: the dGPU is asleep and --wake was passed; waking it for the run.");
        }

        if (!TryStart(out var device))
            return 2;

        try
        {
            var first = Read(device);
            Logger.Info($"GPU power probe: name={first.Name}, range {first.MinW}-{first.MaxW} W, " +
                        $"default {first.DefaultW} W, " +
                        $"current {(first.CurrentSupported ? $"{first.CurrentW} W" : "unsupported")}, " +
                        $"enforced {(first.EnforcedSupported ? $"{first.EnforcedW} W" : "unsupported")}");

            if (!first.CurrentSupported)
                Logger.Info("GPU power probe: this driver does not implement nvmlDeviceGetPowerManagementLimit; " +
                            "the enforced limit is the readable value, and a write would have to be verified by it.");

            if (first.MinW == first.MaxW)
            {
                Logger.Warn("GPU power probe: the driver reports a single fixed limit; nothing to adjust here.");
                return 0;
            }

            var samples = new List<Sample>();
            var deadline = Environment.TickCount64 + Math.Max(1, seconds) * 1000L;

            while (Environment.TickCount64 < deadline)
            {
                var reading = Read(device);
                var sample = new Sample(reading, CurrentMode(), OnBattery());
                samples.Add(sample);

                Logger.Info($"GPU power probe: mode={sample.Mode} {(sample.OnBattery ? "DC" : "AC")} " +
                            $"limit={(reading.LimitSupported ? $"{reading.LimitW} W" : "n/a")} " +
                            $"enforced={(reading.EnforcedSupported ? $"{reading.EnforcedW} W" : "n/a")} " +
                            $"draw={reading.DrawW:0.0} W temp={reading.TemperatureC} C");

                Thread.Sleep(2000);
            }

            Summarise(samples);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("GPU power probe: failed.", ex);
            return 4;
        }
        finally
        {
            try { NativeNvml.Shutdown(); } catch { }
        }
    }

    /// <summary>
    /// A single, reversible write: lower the limit, watch whether it holds, then put the mode's own
    /// number back. Lower-only on purpose - raising would answer the same question while breaking the
    /// rule the feature is built on.
    /// </summary>
    public static int Change(int watts, int holdSeconds, bool allowWake = false)
    {
        if (GpuPowerState.IsAsleep && !allowWake)
        {
            Logger.Warn("GPU power probe: the dGPU is powered down; refusing to wake it. Pass --wake to allow it.");
            return 1;
        }

        if (!TryStart(out var device))
            return 2;

        try
        {
            var before = Read(device);
            Logger.Info($"GPU power probe: before - mode {CurrentMode()}, enforced {before.EnforcedW} W, " +
                        $"range {before.MinW}-{before.MaxW} W");

            if (before.MinW == before.MaxW)
            {
                Logger.Warn("GPU power probe: the driver reports a single fixed limit; nothing to set.");
                return 0;
            }

            var ceiling = before.EnforcedW;
            if (watts >= ceiling)
            {
                Logger.Warn($"GPU power probe: {watts} W is not below the current {ceiling} W. " +
                            "This probe only lowers, so nothing was written.");
                return 3;
            }

            var target = (uint)Math.Clamp(watts, (int)before.MinW, (int)ceiling);

            try
            {
                if (!Set(device, target))
                    return 4;

                var after = Read(device);
                Logger.Info($"GPU power probe: wrote {target} W, enforced now {after.EnforcedW} W " +
                            $"({(after.EnforcedW == target ? "accepted" : "not taken")})");

                var held = 0;
                var samples = 0;
                var deadline = Environment.TickCount64 + Math.Max(1, holdSeconds) * 1000L;

                while (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2000);
                    var sample = Read(device);
                    samples++;
                    if (sample.EnforcedW == target) held++;

                    Logger.Info($"GPU power probe: holding - mode {CurrentMode()}, enforced {sample.EnforcedW} W " +
                                $"({(sample.EnforcedW == target ? "held" : "moved")})");
                }

                Logger.Info($"GPU power probe: held {held}/{samples} samples at {target} W" +
                            (held == samples
                                ? " - the driver keeps it and nothing put it back."
                                : " - something reset it; the EC or the driver owns this value."));
            }
            finally
            {
                // Whatever happened above, the mode's own number goes back.
                Set(device, ceiling);
                var back = Read(device);
                Logger.Info($"GPU power probe: reverted to {ceiling} W, enforced now {back.EnforcedW} W " +
                            $"({(back.EnforcedW == ceiling ? "ok" : "MISMATCH - set it back by hand or switch mode")})");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("GPU power probe: the write test failed.", ex);
            return 5;
        }
        finally
        {
            try { NativeNvml.Shutdown(); } catch { }
        }
    }

    private static string Describe(int nvmlCode) => nvmlCode switch
    {
        1 => "NVML not initialised",
        2 => "invalid argument - the value is outside the driver range",
        3 => "device not found",
        4 => "not supported by this driver or GPU",
        5 => "permission denied - needs administrator",
        999 => "unknown error",
        _ => "see NVML documentation"
    };

    private static bool Set(IntPtr device, uint watts)
    {
        var result = NativeNvml.DeviceSetPowerManagementLimit(device, watts * 1000);
        if (result != 0)
        {
            Logger.Warn($"GPU power probe: nvmlDeviceSetPowerManagementLimit({watts} W) returned {result} " +
                        $"({Describe(result)}).");
            return false;
        }

        return true;
    }

    private readonly record struct Sample(
        Reading Value, string Mode, bool OnBattery);

    private readonly record struct Reading(
        string Name, uint DefaultW, uint MinW, uint MaxW, bool CurrentSupported, uint CurrentW,
        bool EnforcedSupported, uint EnforcedW, double DrawW, uint TemperatureC)
    {
        /// <summary>The limit to reason about: some laptop drivers do not implement the plain getter.</summary>
        public uint LimitW => CurrentSupported ? CurrentW : EnforcedW;

        public bool LimitSupported => CurrentSupported || EnforcedSupported;
    }

    /// <summary>Groups the run by mode and power source, so a limit that tracks the mode is obvious.</summary>
    private static void Summarise(List<Sample> samples)
    {
        var groups = samples
            .GroupBy(sample => (sample.Mode, sample.OnBattery))
            .Select(group => new
            {
                group.Key.Mode,
                group.Key.OnBattery,
                Limits = group.Where(sample => sample.Value.LimitSupported)
                              .Select(sample => sample.Value.LimitW).Distinct().OrderBy(w => w).ToList(),
                Enforced = group.Select(sample => sample.Value.EnforcedW).Distinct().OrderBy(w => w).ToList()
            });

        Logger.Info("GPU power probe: per mode and source, the driver reported:");
        foreach (var group in groups)
        {
            Logger.Info($"GPU power probe:   {group.Mode} {(group.OnBattery ? "DC" : "AC")} -> " +
                        $"limit [{string.Join(", ", group.Limits)}] W, " +
                        $"enforced [{string.Join(", ", group.Enforced)}] W");
        }

        var distinct = samples.Where(sample => sample.Value.LimitSupported)
                              .Select(sample => sample.Value.LimitW).Distinct().Count();
        Logger.Info(distinct > 1
            ? "GPU power probe: the limit changed during the run - it follows something other than the driver default."
            : "GPU power probe: the limit stayed fixed across modes and power sources.");
    }

    private static Reading Read(IntPtr device)
    {
        uint currentW = 0, defaultW = 0, minW = 0, maxW = 0, enforcedW = 0, milliwatts = 0, temperatureC = 0;

        var currentOk = NativeNvml.DeviceGetPowerManagementLimit(device, out currentW) == 0;
        NativeNvml.DeviceGetPowerManagementDefaultLimit(device, out defaultW);
        NativeNvml.DeviceGetPowerManagementLimitConstraints(device, out minW, out maxW);
        var enforcedOk = NativeNvml.DeviceGetEnforcedPowerLimit(device, out enforcedW) == 0;
        NativeNvml.DeviceGetPowerUsage(device, out milliwatts);
        NativeNvml.DeviceGetTemperature(device, TemperatureGpu, out temperatureC);

        return new Reading(
            Name(device),
            defaultW / 1000,
            minW / 1000,
            maxW / 1000,
            currentOk,
            currentW / 1000,
            enforcedOk,
            enforcedW / 1000,
            milliwatts / 1000.0,
            temperatureC);
    }

    private static string Name(IntPtr device)
    {
        var buffer = new byte[96];
        return NativeNvml.DeviceGetName(device, buffer, (uint)buffer.Length) == 0
            ? System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0')
            : "unknown";
    }

    private static bool TryStart(out IntPtr device)
    {
        device = IntPtr.Zero;

        if (NativeNvml.Init() != 0)
        {
            Logger.Warn("GPU power probe: NVML would not initialise.");
            return false;
        }

        if (NativeNvml.DeviceGetHandleByIndex(0, out device) != 0 || device == IntPtr.Zero)
        {
            Logger.Warn("GPU power probe: no NVML device at index 0.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The mode the EC actually reports, not the saved setting: a mode can be applied by the tray, a
    /// hotkey or the dashboard, and the setting lags whichever process did it.
    /// </summary>
    private static string CurrentMode() =>
        AcerWmiManager.GetEcPowerMode()?.ToString()
        ?? ((Nitrous.Enums.PowerProfile)SettingsManager.Get("LastPowerMode", (int)Nitrous.Enums.PowerProfile.Balanced))
            .ToString();

    private static bool OnBattery() =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus !=
        System.Windows.Forms.PowerLineStatus.Online;

    private static class NativeNvml
    {
        [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
        public static extern int Init();

        [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
        public static extern int Shutdown();

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int DeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName")]
        public static extern int DeviceGetName(IntPtr device, byte[] name, uint length);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        public static extern int DeviceGetPowerManagementLimit(IntPtr device, out uint limit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
        public static extern int DeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
        public static extern int DeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minLimit, out uint maxLimit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        public static extern int DeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")]
        public static extern int DeviceSetPowerManagementLimit(IntPtr device, uint milliwatts);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
        public static extern int DeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern int DeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);
    }
}
