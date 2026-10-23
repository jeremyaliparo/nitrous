using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

public class NvidiaGpuManager : IDisposable
{
    private static readonly FieldInfo ClockEntryFlagsField =
        typeof(PerformanceStates20ClockEntryV1).GetField(
            "_Flags",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("NvAPIWrapper clock entry layout changed.");

    private static readonly FieldInfo PerformanceStateFlagsField =
        typeof(PerformanceStates20InfoV1.PerformanceState20).GetField(
            "_Flags",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("NvAPIWrapper performance state layout changed.");

    private readonly object _gate = new();
    private volatile bool _disposed;
    private PhysicalGPU? _internalGpu;
    private IntPtr _nvmlDeviceHandle = IntPtr.Zero;
    private bool _nvmlInitialized;
    private string _cachedArchName = "Unknown";
    private int _nvmlConsecutiveFailures;
    private DateTime _lastNvmlReset = DateTime.MinValue;
    private bool _initFailureLogged;

    public bool IsValid
    {
        get { lock (_gate) return !_disposed && _internalGpu != null; }
    }

    public int MaxCoreOffset = 250;
    public int MinCoreOffset = -250;
    public int MaxMemoryOffset = 1000;
    public int MinMemoryOffset = -1000;

    public NvidiaGpuManager()
    {
        InitializeNvAPI();
    }

    private void InitializeNvAPI()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { NVIDIA.Unload(); } catch { }
            ResetNvmlLocked();

            _internalGpu = null;

            try
            {
                NVIDIA.Initialize();
                _internalGpu = GetInternalDiscreteGpu();
                _initFailureLogged = false;
            }
            catch (Exception ex)
            {
                if (!_initFailureLogged)
                {
                    Logger.Warn("NVAPI: initialization failed (the dGPU may be asleep).", ex);
                    _initFailureLogged = true;
                }
            }

            InitializeNvmlLocked();
        }
    }

    private void InitializeNvmlLocked()
    {
        try
        {
            if (!_nvmlInitialized)
            {
                if (NativeNvml.Init() != NvmlReturn.Success)
                {
                    Logger.Warn("NVML: initialization failed.");
                    return;
                }

                _nvmlInitialized = true;
            }

            if (_nvmlDeviceHandle == IntPtr.Zero &&
                NativeNvml.DeviceGetHandleByIndex(0, out var handle) == NvmlReturn.Success)
            {
                _nvmlDeviceHandle = handle;
            }

            if (_nvmlDeviceHandle != IntPtr.Zero &&
                NativeNvml.DeviceGetArchitecture(_nvmlDeviceHandle, out var arch) == NvmlReturn.Success)
            {
                _cachedArchName = arch.ToString();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("NVML: initialization failed.", ex);
        }
    }

    private void ResetNvmlLocked()
    {
        try { NativeNvml.Shutdown(); } catch { }
        _nvmlInitialized = false;
        _nvmlDeviceHandle = IntPtr.Zero;
        _lastNvmlReset = DateTime.UtcNow;
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            var gpus = PhysicalGPU.GetPhysicalGPUs();
            return gpus.FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop)
                   ?? gpus.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public bool GetClocks(out int core, out int memory) => TryGetClocks(out core, out memory, out _);

    /// <summary>
    /// Reads the currently applied P0 deltas. Returns false (instead of reporting 0/0) when the
    /// GPU is asleep or NVAPI fails, so callers never mistake "could not read" for "reset".
    /// </summary>
    public bool TryGetClocks(out int core, out int memory, out string? error)
    {
        core = 0;
        memory = 0;
        error = null;

        lock (_gate)
        {
            if (_disposed)
            {
                error = "The GPU manager has been disposed.";
                return false;
            }

            try
            {
                if (_internalGpu == null) InitializeNvAPI();
                if (_internalGpu == null)
                {
                    error = "NVAPI is not initialized (GPU asleep or driver unavailable).";
                    return false;
                }

                var states = GPUApi.GetPerformanceStates20(_internalGpu.Handle);
                if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var p0Clocks))
                {
                    error = "P0 performance state is unavailable.";
                    return false;
                }

                var coreEntry = p0Clocks.FirstOrDefault(c =>
                    c.DomainId == PublicClockDomain.Graphics && c.IsEditable);
                var memoryEntry = p0Clocks.FirstOrDefault(c =>
                    c.DomainId == PublicClockDomain.Memory && c.IsEditable);

                if (coreEntry == null || memoryEntry == null)
                {
                    error = "Editable graphics/memory clock entries are unavailable.";
                    return false;
                }

                core = coreEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                memory = memoryEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                InvalidateGpuLocked();
                return false;
            }
        }
    }

    public int SetClocks(int core, int memory) => TrySetClocks(core, memory, out _) ? 1 : 0;

    /// <summary>
    /// Applies the offsets to P0 and verifies the result by reading it back.
    ///
    /// Blackwell-class drivers reject a full Get -> modify -> Set round trip with
    /// NVAPI_NOT_SUPPORTED. The request is therefore built as one P-state with one clock entry
    /// per call (Graphics and Memory separately) and the editable bit set, which is the shape
    /// the driver accepts.
    /// </summary>
    public bool TrySetClocks(int core, int memory, out string? error)
    {
        error = null;

        if (core < MinCoreOffset || core > MaxCoreOffset)
        {
            error = $"Core offset {core} MHz is outside {MinCoreOffset}..{MaxCoreOffset} MHz.";
            return false;
        }

        if (memory < MinMemoryOffset || memory > MaxMemoryOffset)
        {
            error = $"Memory offset {memory} MHz is outside {MinMemoryOffset}..{MaxMemoryOffset} MHz.";
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                error = "The GPU manager has been disposed.";
                return false;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_internalGpu == null) InitializeNvAPI();
                    if (_internalGpu == null)
                    {
                        error = "NVAPI is not initialized (GPU asleep or driver unavailable).";
                        return false;
                    }

                    var states = GPUApi.GetPerformanceStates20(_internalGpu.Handle);
                    if (!states.IsEditable)
                    {
                        error = "The driver reports that performance states are not editable.";
                        return false;
                    }

                    if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var p0Clocks))
                    {
                        error = "P0 performance state is unavailable.";
                        return false;
                    }

                    var coreEntry = p0Clocks.FirstOrDefault(c =>
                        c.DomainId == PublicClockDomain.Graphics && c.IsEditable);
                    var memoryEntry = p0Clocks.FirstOrDefault(c =>
                        c.DomainId == PublicClockDomain.Memory && c.IsEditable);

                    if (coreEntry == null || memoryEntry == null)
                    {
                        error = "Editable graphics/memory clock entries are unavailable.";
                        return false;
                    }

                    var currentCore = coreEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                    var currentMemory = memoryEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                    if (currentCore == core && currentMemory == memory)
                    {
                        return true; // idempotent
                    }

                    var coreRange = coreEntry.FrequencyDeltaInkHz.DeltaRange;
                    var memoryRange = memoryEntry.FrequencyDeltaInkHz.DeltaRange;
                    if (!IsWithinRange(core * 1000, coreRange.Minimum, coreRange.Maximum))
                    {
                        error = $"Core offset {core} MHz is outside the driver range " +
                                $"{coreRange.Minimum / 1000}..{coreRange.Maximum / 1000} MHz.";
                        return false;
                    }

                    if (!IsWithinRange(memory * 1000, memoryRange.Minimum, memoryRange.Maximum))
                    {
                        error = $"Memory offset {memory} MHz is outside the driver range " +
                                $"{memoryRange.Minimum / 1000}..{memoryRange.Maximum / 1000} MHz.";
                        return false;
                    }

                    // Blackwell-class drivers (notably the 596.x branch) reject a full
                    // Get -> modify -> Set round trip with NVAPI_NOT_SUPPORTED, even when
                    // only one field changed. Send a sparse request instead: one P-state,
                    // one clock entry, one editable flag. Split core and memory into two
                    // independent calls so one domain cannot poison the other's packet.
                    if (core != currentCore)
                        ApplyClockDomainLocked(_internalGpu.Handle, PerformanceStateId.P0_3DPerformance,
                            PublicClockDomain.Graphics, core * 1000);

                    if (memory != currentMemory)
                        ApplyClockDomainLocked(_internalGpu.Handle, PerformanceStateId.P0_3DPerformance,
                            PublicClockDomain.Memory, memory * 1000);

                    if (TryGetClocks(out var appliedCore, out var appliedMemory, out var readError) &&
                        appliedCore == core && appliedMemory == memory)
                    {
                        Logger.Info($"GPU OC: applied core {core:+#;-#;0} MHz, memory {memory:+#;-#;0} MHz.");
                        return true;
                    }

                    error = $"Driver did not retain the offsets " +
                            $"(wanted {core}/{memory}, read {appliedCore}/{appliedMemory})" +
                            (readError == null ? "." : $": {readError}");
                    return false;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    InvalidateGpuLocked();

                    if (attempt == 0)
                    {
                        // A sleeping/reset GPU leaves a stale NVAPI handle. Rebuild it and retry once.
                        InitializeNvAPI();
                        continue;
                    }

                    Logger.Warn("GPU OC: failed to apply offsets.", ex);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Sends a single-domain P0 offset using the sparse request shape that Blackwell-class
    /// drivers accept. A full Get -> modify -> Set round trip (all clocks, the Video entry, or
    /// both domains in one packet) is rejected with NVAPI_NOT_SUPPORTED on those drivers even
    /// though Get succeeds. The driver wants numPstates=1, numClocks=1, numBaseVoltages=0 and
    /// the editable bit set on both the P-state and the clock entry.
    /// </summary>
    private static void ApplyClockDomainLocked(
        PhysicalGPUHandle gpuHandle,
        PerformanceStateId stateId,
        PublicClockDomain domain,
        int deltaKhz)
    {
        var entry = new PerformanceStates20ClockEntryV1(
            domain,
            new PerformanceStates20ParameterDelta(deltaKhz));
        entry = WithEditableFlag(entry);

        var performanceState = new PerformanceStates20InfoV1.PerformanceState20(
            stateId,
            new[] { entry },
            Array.Empty<PerformanceStates20BaseVoltageEntryV1>());
        performanceState = WithEditableFlag(performanceState);

        var request = new PerformanceStates20InfoV1(
            new[] { performanceState },
            clocksCount: 1,
            baseVoltagesCount: 0);

        GPUApi.SetPerformanceStates20(gpuHandle, request);
    }

    private static PerformanceStates20ClockEntryV1 WithEditableFlag(
        PerformanceStates20ClockEntryV1 entry)
    {
        object boxed = entry;
        ClockEntryFlagsField.SetValue(boxed, 1u);
        return (PerformanceStates20ClockEntryV1)boxed;
    }

    private static PerformanceStates20InfoV1.PerformanceState20 WithEditableFlag(
        PerformanceStates20InfoV1.PerformanceState20 state)
    {
        object boxed = state;
        PerformanceStateFlagsField.SetValue(boxed, 1u);
        return (PerformanceStates20InfoV1.PerformanceState20)boxed;
    }

    private static bool IsWithinRange(int value, int minimum, int maximum)
    {
        // Some drivers report an empty 0..0 range; treat that as "no advertised limit".
        if (minimum == 0 && maximum == 0) return true;
        return value >= minimum && value <= maximum;
    }

    private void InvalidateGpuLocked()
    {
        _internalGpu = null;
    }

    public async Task ApplyOnBootAsync(PowerProfile currentProfile)
    {
        var core = SettingsManager.Get($"GpuCore_{currentProfile}", GetDefaultCore(currentProfile));
        var memory = SettingsManager.Get($"GpuMemory_{currentProfile}", GetDefaultMemory(currentProfile));

        // A stock profile is not an instruction to clear an offset somebody else (or an
        // earlier profile) applied. This was the old boot behavior; the old profile switch
        // path lost it and cleared a working OC back to 0.
        if (core == 0 && memory == 0)
        {
            Logger.Info($"GPU OC: boot profile {currentProfile} is stock (0/0); leaving offsets untouched.");
            return;
        }

        // Boot can be slow: the driver may take tens of seconds to expose NVAPI.
        const int maxAttempts = 30;
        const int delayMs = 2000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_disposed) return;

            if (TrySetClocks(core, memory, out var error))
            {
                Logger.Info($"GPU OC: boot profile {currentProfile} applied ({core}/{memory} MHz).");
                return;
            }

            if (attempt == 1 || attempt % 5 == 0)
                Logger.Warn($"GPU OC: boot apply {attempt}/{maxAttempts} failed for {currentProfile}: {error}");

            await Task.Delay(delayMs);
        }

        Logger.Error($"GPU OC: could not apply boot profile {currentProfile} after {maxAttempts} attempts.");
    }

    /// <summary>
    /// Idempotent watchdog used after resume and on a timer. It only writes when a successful
    /// read shows the offsets drifted; a failed read is left alone so a sleeping dGPU is not woken.
    /// </summary>
    public Task<bool> EnsureClocksAppliedAsync(PowerProfile profile)
    {
        var core = SettingsManager.Get($"GpuCore_{profile}", GetDefaultCore(profile));
        var memory = SettingsManager.Get($"GpuMemory_{profile}", GetDefaultMemory(profile));

        // 0/0 means "stock / no override" for automatic repairs; do not use the watchdog to
        // clear an offset that was applied manually or by another tool.
        if (core == 0 && memory == 0)
            return Task.FromResult(true);

        return Task.Run(() =>
        {
            if (!TryGetClocks(out var currentCore, out var currentMemory, out _))
                return false;

            if (currentCore == core && currentMemory == memory)
                return true;

            Logger.Warn($"GPU OC: offsets drifted from {core}/{memory} to {currentCore}/{currentMemory}; reapplying.");
            return TrySetClocks(core, memory, out var error) || LogEnsureFailure(error);
        });
    }

    private static bool LogEnsureFailure(string? error)
    {
        Logger.Warn($"GPU OC: watchdog re-apply failed: {error}");
        return false;
    }

    public async Task<int> ApplyPowerProfileOcAsync(PowerProfile profile, bool allowStockReset = false)
    {
        var coreOffset = SettingsManager.Get($"GpuCore_{profile}", GetDefaultCore(profile));
        var memoryOffset = SettingsManager.Get($"GpuMemory_{profile}", GetDefaultMemory(profile));

        // Automatic startup/resume/AC-DC/profile application must not wipe an existing OC
        // just because this profile's saved offsets are 0/0. Explicit Apply/Power-profile
        // clicks pass allowStockReset: true and still return the GPU to stock.
        if (!allowStockReset && coreOffset == 0 && memoryOffset == 0)
        {
            Logger.Info($"GPU OC: {profile} is stock (0/0); leaving current offsets untouched.");
            return 1;
        }

        return await Task.Run(() =>
        {
            return TrySetClocks(coreOffset, memoryOffset, out _) ? 1 : 0;
        });
    }

    public int GetDefaultCore(PowerProfile profile) => profile switch
    {
        PowerProfile.Quiet => -100,
        PowerProfile.Performance => 100,
        PowerProfile.Turbo => 150,
        _ => 0
    };

    public int GetDefaultMemory(PowerProfile profile) => profile switch
    {
        PowerProfile.Quiet => -200,
        PowerProfile.Performance => 150,
        PowerProfile.Turbo => 300,
        _ => 0
    };

    public void SaveCustomProfileOc(PowerProfile profile, int core, int memory)
    {
        SettingsManager.Save($"GpuCore_{profile}", core);
        SettingsManager.Save($"GpuMemory_{profile}", memory);
    }

    public async Task ResetProfileToDefaultsAsync(PowerProfile profile)
    {
        SettingsManager.Save($"GpuCore_{profile}", GetDefaultCore(profile));
        SettingsManager.Save($"GpuMemory_{profile}", GetDefaultMemory(profile));
        await ApplyPowerProfileOcAsync(profile, allowStockReset: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try { NVIDIA.Unload(); } catch { }
            try { NativeNvml.Shutdown(); } catch { }
            _internalGpu = null;
            _nvmlInitialized = false;
            _nvmlDeviceHandle = IntPtr.Zero;
        }
    }

    public class GpuTelemetry
    {
        public string Name { get; set; } = "Unknown";
        public string Architecture { get; set; } = "Unknown";
        public int CoreTemp { get; set; }
        public int GpuLoad { get; set; }
        public int VramUsedMb { get; set; }
        public int VramTotalMb { get; set; }
        public string PState { get; set; } = "Unknown";
        public int CurrentCoreClock { get; set; }
        public int CurrentMemoryClock { get; set; }
        public double PowerDrawW { get; set; }
        public double MinPowerLimitW { get; set; }
        public double MaxPowerLimitW { get; set; }
        public double EnforcedPowerLimitW { get; set; }
    }

    public async Task<GpuTelemetry> GetNvmlTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var telemetry = new GpuTelemetry();

        return await Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed) return telemetry;

                if (_nvmlDeviceHandle == IntPtr.Zero)
                {
                    if (!_nvmlInitialized) InitializeNvmlLocked();

                    // A previous init succeeded but the handle was lost (sleep/driver reset).
                    if (_nvmlDeviceHandle == IntPtr.Zero &&
                        DateTime.UtcNow - _lastNvmlReset > TimeSpan.FromSeconds(30))
                    {
                        ResetNvmlLocked();
                        InitializeNvmlLocked();
                    }
                }

                if (_nvmlDeviceHandle == IntPtr.Zero)
                    return telemetry;

                var anySuccess = false;

                try
                {
                    var nameBuilder = new StringBuilder(96);
                    if (NativeNvml.DeviceGetName(_nvmlDeviceHandle, nameBuilder, 96) == NvmlReturn.Success)
                    {
                        telemetry.Name = nameBuilder.ToString();
                        anySuccess = true;
                    }

                    telemetry.Architecture = _cachedArchName;

                    if (NativeNvml.DeviceGetTemperature(_nvmlDeviceHandle, NvmlTemperatureSensors.Gpu, out var temp) == NvmlReturn.Success)
                    {
                        telemetry.CoreTemp = (int)temp;
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetUtilizationRates(_nvmlDeviceHandle, out var util) == NvmlReturn.Success)
                    {
                        telemetry.GpuLoad = (int)util.Gpu;
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetMemoryInfo(_nvmlDeviceHandle, out var memory) == NvmlReturn.Success)
                    {
                        telemetry.VramUsedMb = (int)(memory.Used / (1024 * 1024));
                        telemetry.VramTotalMb = (int)(memory.Total / (1024 * 1024));
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetPerformanceState(_nvmlDeviceHandle, out var pState) == NvmlReturn.Success)
                    {
                        telemetry.PState = pState.ToString().Replace("Pstate", "P");
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetClockInfo(_nvmlDeviceHandle, NvmlClockType.Graphics, out var coreClock) == NvmlReturn.Success)
                    {
                        telemetry.CurrentCoreClock = (int)coreClock;
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetClockInfo(_nvmlDeviceHandle, NvmlClockType.Mem, out var memClock) == NvmlReturn.Success)
                    {
                        telemetry.CurrentMemoryClock = (int)memClock;
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetPowerUsage(_nvmlDeviceHandle, out var powerDraw) == NvmlReturn.Success)
                    {
                        telemetry.PowerDrawW = Math.Round(powerDraw / 1000.0, 1);
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetEnforcedPowerLimit(_nvmlDeviceHandle, out var enforced) == NvmlReturn.Success)
                    {
                        telemetry.EnforcedPowerLimitW = Math.Round(enforced / 1000.0, 1);
                        anySuccess = true;
                    }

                    if (NativeNvml.DeviceGetPowerManagementLimitConstraints(_nvmlDeviceHandle, out var minLimit, out var maxLimit) == NvmlReturn.Success)
                    {
                        telemetry.MinPowerLimitW = Math.Round(minLimit / 1000.0, 1);
                        telemetry.MaxPowerLimitW = Math.Round(maxLimit / 1000.0, 1);
                        anySuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    if (_nvmlConsecutiveFailures == 0)
                        Logger.Warn("NVML: telemetry read failed; will retry.", ex);
                }

                if (anySuccess)
                {
                    _nvmlConsecutiveFailures = 0;
                }
                else
                {
                    _nvmlConsecutiveFailures++;
                    // Rebuild the NVML handle after ~30 seconds of total failure (e.g. after sleep),
                    // but never more often than once a minute.
                    if (_nvmlConsecutiveFailures >= 15 && DateTime.UtcNow - _lastNvmlReset > TimeSpan.FromMinutes(1))
                    {
                        ResetNvmlLocked();
                        InitializeNvmlLocked();
                        _nvmlConsecutiveFailures = 0;
                    }
                }

                return telemetry;
            }
        }, cancellationToken);
    }

    #region NVML Native Bindings

    public enum NvmlReturn
    {
        Success = 0,
        Uninitialized = 1,
        InvalidArgument = 2,
        NotSupported = 3,
        NoPermission = 4,
        AlreadyInitialized = 5,
        NotFound = 6
    }

    public enum NvmlDeviceArchitecture
    {
        Kepler = 2, Maxwell = 3, Pascal = 4, Volta = 5,
        Turing = 6, Ampere = 7, Ada = 8, Hopper = 9,
        Blackwell = 10, Rubin = 13
    }

    public enum NvmlClockType { Graphics = 0, Sm = 1, Mem = 2, Video = 3 }
    public enum NvmlTemperatureSensors { Gpu = 0 }
    public enum NvmlPstates
    {
        Pstate0 = 0, Pstate1 = 1, Pstate2 = 2, Pstate3 = 3,
        Pstate4 = 4, Pstate5 = 5, Pstate6 = 6, Pstate7 = 7,
        Pstate8 = 8, Pstate9 = 9, Pstate10 = 10, Pstate11 = 11,
        Pstate12 = 12, Pstate13 = 13, Pstate14 = 14, Pstate15 = 15,
        Unknown = 32
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private static class NativeNvml
    {
        private const string NvmlDll = "nvml.dll";

        [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
        public static extern NvmlReturn Init();

        [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
        public static extern NvmlReturn Shutdown();

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern NvmlReturn DeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName")]
        public static extern NvmlReturn DeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
        public static extern NvmlReturn DeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern NvmlReturn DeviceGetTemperature(IntPtr device, NvmlTemperatureSensors sensorType, out uint temp);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetClockInfo")]
        public static extern NvmlReturn DeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clock);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
        public static extern NvmlReturn DeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
        public static extern NvmlReturn DeviceGetPowerUsage(IntPtr device, out uint power);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        public static extern NvmlReturn DeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
        public static extern NvmlReturn DeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minLimit, out uint maxLimit);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPerformanceState")]
        public static extern NvmlReturn DeviceGetPerformanceState(IntPtr device, out NvmlPstates pState);

        [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetArchitecture")]
        public static extern NvmlReturn DeviceGetArchitecture(IntPtr device, out NvmlDeviceArchitecture arch);
    }
    #endregion
}
