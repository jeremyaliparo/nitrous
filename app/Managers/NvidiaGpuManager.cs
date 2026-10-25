using System;
using System.Collections.Generic;
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

    public const string ManageGpuClocksKey = "ManageGpuClocks";
    private const string LastAppliedCoreKey = "GpuLastAppliedCore";
    private const string LastAppliedMemoryKey = "GpuLastAppliedMemory";

    private const int CurrentOffsetDefaultsRevision = 1;
    private const string OffsetDefaultsRevisionKey = "GpuOffsetDefaultsRevision";

    // These were the automatic per-profile offsets before GPU OC became opt-in. Existing
    // installs may have saved them through APPLY/RESET, so migrate exact matches to stock.
    private static readonly (PowerProfile Profile, int Core, int Memory)[] LegacyDefaultOffsets =
    {
        (PowerProfile.Quiet, -100, -200),
        (PowerProfile.Balanced, 0, 0),
        (PowerProfile.Performance, 100, 150),
        (PowerProfile.Turbo, 150, 300)
    };

    private readonly object _gate = new();
    private volatile bool _disposed;
    private PhysicalGPU? _internalGpu;
    private IntPtr _nvmlDeviceHandle = IntPtr.Zero;
    private bool _nvmlInitialized;
    private string _cachedArchName = "Unknown";
    private int _nvmlConsecutiveFailures;
    private DateTime _lastNvmlReset = DateTime.MinValue;
    private DateTime _lastVoltageWarning = DateTime.MinValue;
    private bool _initFailureLogged;

    /// <summary>
    /// GPU clock writes are opt-in. The tray and dashboard are separate processes, so this is
    /// read from the shared registry setting on every check instead of being cached.
    /// </summary>
    public static bool IsClockManagementEnabled() =>
        SettingsManager.Get(ManageGpuClocksKey, 0) == 1;

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
        MigrateLegacyDefaultOffsets();
        InitializeNvAPI();
    }

    private static void MigrateLegacyDefaultOffsets()
    {
        if (SettingsManager.Get(OffsetDefaultsRevisionKey, 0) >= CurrentOffsetDefaultsRevision)
            return;

        foreach (var legacy in LegacyDefaultOffsets)
        {
            var coreKey = $"GpuCore_{legacy.Profile}";
            var memoryKey = $"GpuMemory_{legacy.Profile}";
            var core = SettingsManager.Get(coreKey, int.MinValue);
            var memory = SettingsManager.Get(memoryKey, int.MinValue);

            // Only remove an exact legacy pair. A partially customized pair is left alone.
            if (core != legacy.Core || memory != legacy.Memory)
                continue;

            SettingsManager.Delete(coreKey);
            SettingsManager.Delete(memoryKey);
            Logger.Info($"GPU OC: migrated legacy {legacy.Profile} offsets ({core}/{memory}) to stock.");
        }

        SettingsManager.Save(OffsetDefaultsRevisionKey, CurrentOffsetDefaultsRevision);
    }

    private void InitializeNvAPI()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { NVIDIA.Unload(); } catch { }

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

            // NVML is deliberately not initialized here. Loading it maps a ~23 MB driver
            // image into the process, and the tray does not need it unless the fan curve
            // engine has to fall back to NVML telemetry or the dashboard asks for deep
            // telemetry. GetNvmlTelemetryAsync initializes it on demand.
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
        // Never touch nvml.dll unless this process actually initialized it: calling
        // Shutdown on a fresh process would map the driver image we are trying to avoid.
        if (_nvmlInitialized)
        {
            try { NativeNvml.Shutdown(); } catch { }
        }

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

    /// <summary>
    /// Read-only NVAPI/NVML state for diagnostics. It never writes an offset; a failed read is
    /// reported as an error so a sleeping dGPU is not mistaken for a 0/0 configuration.
    /// </summary>
    public Dictionary<string, object?> GetOffsetDiagnostics()
    {
        var lastAppliedCore = SettingsManager.Get(LastAppliedCoreKey, int.MinValue);
        var lastAppliedMemory = SettingsManager.Get(LastAppliedMemoryKey, int.MinValue);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpuClockManagementEnabled"] = IsClockManagementEnabled(),
            ["gpuClockManagement"] = IsClockManagementEnabled() ? "on" : "off",
            ["lastAppliedOffsets"] = lastAppliedCore == int.MinValue || lastAppliedMemory == int.MinValue
                ? null
                : new Dictionary<string, object?>
                {
                    ["coreMhz"] = lastAppliedCore,
                    ["memoryMhz"] = lastAppliedMemory
                },
            ["configuredCoreRangeMhz"] = new Dictionary<string, object?>
            {
                ["min"] = MinCoreOffset,
                ["max"] = MaxCoreOffset
            },
            ["configuredMemoryRangeMhz"] = new Dictionary<string, object?>
            {
                ["min"] = MinMemoryOffset,
                ["max"] = MaxMemoryOffset
            }
        };

        lock (_gate)
        {
            result["disposed"] = _disposed;
            result["nvapiInitialized"] = _internalGpu != null;
            result["nvmlInitialized"] = _nvmlInitialized;
            result["nvmlDeviceHandleAcquired"] = _nvmlDeviceHandle != IntPtr.Zero;
            result["architecture"] = _cachedArchName;

            try
            {
                if (_disposed)
                {
                    result["error"] = "The GPU manager has been disposed.";
                    return result;
                }

                if (_internalGpu == null) InitializeNvAPI();
                result["nvapiInitialized"] = _internalGpu != null;
                if (_internalGpu == null)
                {
                    result["error"] = "NVAPI is not initialized (GPU asleep or driver unavailable).";
                    return result;
                }

                var states = GPUApi.GetPerformanceStates20(_internalGpu.Handle);
                result["statesEditable"] = states.IsEditable;
                AddVoltageDiagnostics(result, states);

                if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var p0Clocks))
                {
                    result["error"] = "P0 performance state is unavailable.";
                    return result;
                }

                var coreEntry = p0Clocks.FirstOrDefault(c =>
                                    c.DomainId == PublicClockDomain.Graphics && c.IsEditable)
                                ?? p0Clocks.FirstOrDefault(c =>
                                    c.DomainId == PublicClockDomain.Graphics);
                var memoryEntry = p0Clocks.FirstOrDefault(c =>
                                      c.DomainId == PublicClockDomain.Memory && c.IsEditable)
                                  ?? p0Clocks.FirstOrDefault(c =>
                                      c.DomainId == PublicClockDomain.Memory);

                if (coreEntry == null || memoryEntry == null)
                {
                    result["error"] = "Graphics/memory P0 clock entries are unavailable.";
                    return result;
                }

                result["coreEditable"] = coreEntry.IsEditable;
                result["coreOffsetMhz"] = coreEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                var coreRange = coreEntry.FrequencyDeltaInkHz.DeltaRange;
                result["coreDriverRangeMhz"] = new Dictionary<string, object?>
                {
                    ["min"] = coreRange.Minimum / 1000,
                    ["max"] = coreRange.Maximum / 1000
                };

                result["memoryEditable"] = memoryEntry.IsEditable;
                result["memoryOffsetMhz"] = memoryEntry.FrequencyDeltaInkHz.DeltaValue / 1000;
                var memoryRange = memoryEntry.FrequencyDeltaInkHz.DeltaRange;
                result["memoryDriverRangeMhz"] = new Dictionary<string, object?>
                {
                    ["min"] = memoryRange.Minimum / 1000,
                    ["max"] = memoryRange.Maximum / 1000
                };
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
                InvalidateGpuLocked();
                result["nvapiInitialized"] = _internalGpu != null;
                Logger.Warn("GPU OC: diagnostics read failed.", ex);
            }
        }

        return result;
    }

    private static IPerformanceStates20VoltageEntry[] GetP0BaseVoltages(IPerformanceStates20Info states)
    {
        try
        {
            if (states.Voltages != null &&
                states.Voltages.TryGetValue(PerformanceStateId.P0_3DPerformance, out var entries) &&
                entries != null)
            {
                return entries;
            }
        }
        catch
        {
            // A driver that does not expose voltage entries must not break clock diagnostics.
        }

        return Array.Empty<IPerformanceStates20VoltageEntry>();
    }

    private static void AddVoltageDiagnostics(
        Dictionary<string, object?> result,
        IPerformanceStates20Info states)
    {
        try
        {
            var baseVoltages = GetP0BaseVoltages(states);
            var generalVoltages = states.GeneralVoltages ?? Array.Empty<IPerformanceStates20VoltageEntry>();

            var baseOverrides = baseVoltages.Any(entry =>
                entry.IsEditable && entry.ValueDeltaInMicroVolt.DeltaValue != 0);
            var generalOverrides = generalVoltages.Any(entry =>
                entry.IsEditable && entry.ValueDeltaInMicroVolt.DeltaValue != 0);

            result["baseVoltageCount"] = baseVoltages.Length;
            result["generalVoltageCount"] = generalVoltages.Length;
            result["voltageOverridesDetected"] = baseOverrides || generalOverrides;
            result["baseVoltages"] = baseVoltages.Select(entry => new Dictionary<string, object?>
            {
                ["domain"] = entry.DomainId.ToString(),
                ["editable"] = entry.IsEditable,
                ["valueMv"] = Math.Round(entry.ValueInMicroVolt / 1000.0, 1),
                ["deltaMv"] = Math.Round(entry.ValueDeltaInMicroVolt.DeltaValue / 1000.0, 1),
                ["deltaMinMv"] = Math.Round(entry.ValueDeltaInMicroVolt.DeltaRange.Minimum / 1000.0, 1),
                ["deltaMaxMv"] = Math.Round(entry.ValueDeltaInMicroVolt.DeltaRange.Maximum / 1000.0, 1)
            }).ToList();
        }
        catch (Exception ex)
        {
            result["voltageReadError"] = ex.Message;
        }
    }

    private static bool HasVoltageOverrides(IPerformanceStates20Info states)
    {
        try
        {
            var baseVoltages = GetP0BaseVoltages(states);
            var generalVoltages = states.GeneralVoltages ?? Array.Empty<IPerformanceStates20VoltageEntry>();

            return baseVoltages.Any(entry =>
                       entry.IsEditable && entry.ValueDeltaInMicroVolt.DeltaValue != 0) ||
                   generalVoltages.Any(entry =>
                       entry.IsEditable && entry.ValueDeltaInMicroVolt.DeltaValue != 0);
        }
        catch
        {
            return false;
        }
    }

    public async Task<Dictionary<string, object?>> GetDiagnosticsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["offsets"] = GetOffsetDiagnostics()
        };

        try
        {
            var telemetry = await GetNvmlTelemetryAsync(cancellationToken).ConfigureAwait(false);
            result["nvml"] = new Dictionary<string, object?>
            {
                ["name"] = telemetry.Name,
                ["architecture"] = telemetry.Architecture,
                ["coreTemperatureC"] = telemetry.CoreTemp,
                ["gpuLoadPercent"] = telemetry.GpuLoad,
                ["vramUsedMb"] = telemetry.VramUsedMb,
                ["vramTotalMb"] = telemetry.VramTotalMb,
                ["pState"] = telemetry.PState,
                ["currentCoreClockMhz"] = telemetry.CurrentCoreClock,
                ["currentMemoryClockMhz"] = telemetry.CurrentMemoryClock,
                ["powerDrawW"] = telemetry.PowerDrawW,
                ["minPowerLimitW"] = telemetry.MinPowerLimitW,
                ["maxPowerLimitW"] = telemetry.MaxPowerLimitW,
                ["enforcedPowerLimitW"] = telemetry.EnforcedPowerLimitW
            };

            var driverVersion = GetNvmlDriverVersion();
            if (!string.IsNullOrWhiteSpace(driverVersion))
                result["nvmlDriverVersion"] = driverVersion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result["nvmlError"] = ex.Message;
            Logger.Warn("NVML: diagnostics telemetry failed.", ex);
        }

        return result;
    }

    private string? GetNvmlDriverVersion()
    {
        lock (_gate)
        {
            if (_disposed || !_nvmlInitialized) return null;

            try
            {
                var version = new StringBuilder(80);
                return NativeNvml.SystemGetDriverVersion(version, (uint)version.Capacity) == NvmlReturn.Success
                    ? version.ToString()
                    : null;
            }
            catch
            {
                return null;
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
    public bool TrySetClocks(int core, int memory, out string? error, string source = "manual")
    {
        if (!IsClockManagementEnabled())
        {
            error = "GPU clock management is off. Enable 'Nitrous manages GPU clocks' on the GPU page first.";
            return false;
        }

        return TrySetClocksCore(core, memory, out error, source);
    }

    private bool TrySetClocksCore(int core, int memory, out string? error, string source)
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

                    if (HasVoltageOverrides(states) &&
                        DateTime.UtcNow - _lastVoltageWarning > TimeSpan.FromMinutes(5))
                    {
                        Logger.Warn(
                            $"GPU OC: {source}: an external voltage/frequency override is present. " +
                            "Nitrous writes only P0 clock deltas and cannot guarantee that the external curve is preserved.");
                        _lastVoltageWarning = DateTime.UtcNow;
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
                        RecordAppliedOffsets(core, memory);
                        Logger.Info(
                            $"GPU OC: {source}: applied core {currentCore:+#;-#;0} -> {core:+#;-#;0} MHz, " +
                            $"memory {currentMemory:+#;-#;0} -> {memory:+#;-#;0} MHz.");
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

    private static void RecordAppliedOffsets(int core, int memory)
    {
        // 0/0 means Nitrous no longer has an offset of its own to clean up.
        if (core == 0 && memory == 0)
        {
            SettingsManager.Delete(LastAppliedCoreKey);
            SettingsManager.Delete(LastAppliedMemoryKey);
            return;
        }

        SettingsManager.Save(LastAppliedCoreKey, core);
        SettingsManager.Save(LastAppliedMemoryKey, memory);
    }

    /// <summary>
    /// True when Nitrous has a record of applying a non-zero offset that may still be active.
    /// Used by the dashboard when clock management is switched off.
    /// </summary>
    public bool HasOwnedOffsets() => TryGetAppliedOffsets(out _, out _);

    /// <summary>
    /// Returns the last offsets Nitrous wrote, if it still owns a non-zero offset.
    /// </summary>
    public bool TryGetAppliedOffsets(out int core, out int memory)
    {
        core = SettingsManager.Get(LastAppliedCoreKey, int.MinValue);
        memory = SettingsManager.Get(LastAppliedMemoryKey, int.MinValue);
        return core != int.MinValue && memory != int.MinValue && (core != 0 || memory != 0);
    }

    /// <summary>
    /// Clears the offsets Nitrous applied, but only when the GPU still reports exactly those
    /// values. If another tool changed them, they are left alone.
    /// </summary>
    public bool TryClearAppliedOffsets(out string message)
    {
        if (!TryGetAppliedOffsets(out var core, out var memory))
        {
            message = "Nitrous has no record of applying a non-zero GPU offset.";
            return false;
        }

        if (!TryGetClocks(out var currentCore, out var currentMemory, out var readError))
        {
            message = $"Could not read the current GPU offsets: {readError}";
            return false;
        }

        if (currentCore != core || currentMemory != memory)
        {
            message = $"The GPU is at {currentCore:+#;-#;0}/{currentMemory:+#;-#;0} MHz, not the " +
                      $"{core:+#;-#;0}/{memory:+#;-#;0} MHz Nitrous applied. Leaving them untouched.";
            return false;
        }

        if (!TrySetClocksCore(0, 0, out var error, "disable-clear"))
        {
            message = $"Could not clear the GPU offsets: {error}";
            return false;
        }

        message = "Nitrous offsets cleared to 0/0.";
        return true;
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
        if (!IsClockManagementEnabled())
        {
            Logger.Info($"GPU OC: startup: clock management is off; not applying {currentProfile} offsets.");
            return;
        }

        var core = SettingsManager.Get($"GpuCore_{currentProfile}", GetDefaultCore(currentProfile));
        var memory = SettingsManager.Get($"GpuMemory_{currentProfile}", GetDefaultMemory(currentProfile));

        // Management is on, so 0/0 means "stock for this profile" and boot enforces it.
        // The write itself is skipped by TrySetClocks when the driver already reports 0/0.

        // Boot can be slow: the driver may take tens of seconds to expose NVAPI.
        const int maxAttempts = 30;
        const int delayMs = 2000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_disposed) return;

            // The switch can be turned off from the dashboard while this loop is waiting
            // for a slow driver. Stop immediately instead of writing once it appears.
            if (!IsClockManagementEnabled())
            {
                Logger.Info($"GPU OC: startup: clock management was turned off; abandoning {currentProfile} boot apply.");
                return;
            }

            if (TrySetClocks(core, memory, out var error, "startup"))
            {
                Logger.Info($"GPU OC: startup: {currentProfile} offsets confirmed ({core:+#;-#;0}/{memory:+#;-#;0} MHz).");
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
        if (!IsClockManagementEnabled())
            return Task.FromResult(true);

        var core = SettingsManager.Get($"GpuCore_{profile}", GetDefaultCore(profile));
        var memory = SettingsManager.Get($"GpuMemory_{profile}", GetDefaultMemory(profile));

        // The tray is a separate process from the dashboard, so the ownership switch is read
        // above on every tick. When it is on, a 0/0 profile is a real instruction to keep the
        // GPU at stock, not a "leave whatever is there" placeholder.

        return Task.Run(() =>
        {
            if (!TryGetClocks(out var currentCore, out var currentMemory, out _))
                return false;

            if (currentCore == core && currentMemory == memory)
                return true;

            Logger.Warn($"GPU OC: watchdog: offsets drifted from {core:+#;-#;0}/{memory:+#;-#;0} " +
                        $"to {currentCore:+#;-#;0}/{currentMemory:+#;-#;0}; reapplying.");
            return TrySetClocks(core, memory, out var error, "watchdog") || LogEnsureFailure(error);
        });
    }

    private static bool LogEnsureFailure(string? error)
    {
        Logger.Warn($"GPU OC: watchdog re-apply failed: {error}");
        return false;
    }

    public async Task<int> ApplyPowerProfileOcAsync(
        PowerProfile profile,
        bool allowStockReset = false,
        string source = "profile")
    {
        if (!IsClockManagementEnabled())
        {
            Logger.Info($"GPU OC: {source}: clock management is off; not applying {profile} offsets.");
            return 1;
        }

        var coreOffset = SettingsManager.Get($"GpuCore_{profile}", GetDefaultCore(profile));
        var memoryOffset = SettingsManager.Get($"GpuMemory_{profile}", GetDefaultMemory(profile));

        // When the ownership switch is on, 0/0 means "this profile is stock". Automatic paths
        // still pass allowStockReset: false so a stock profile does not fight a manual offset
        // unless the caller is an explicit profile change (click/hotkey/APPLY/RESET).
        if (!allowStockReset && coreOffset == 0 && memoryOffset == 0)
        {
            Logger.Info($"GPU OC: {source}: {profile} is stock (0/0); leaving current offsets untouched.");
            return 1;
        }

        return await Task.Run(() =>
        {
            return TrySetClocks(coreOffset, memoryOffset, out _, source) ? 1 : 0;
        });
    }

    // GPU offsets are opt-in. A profile only changes the firmware power/thermal mode;
    // it must not assume that a universal overclock (or underclock) is stable on every VBIOS.
    public int GetDefaultCore(PowerProfile profile) => 0;

    public int GetDefaultMemory(PowerProfile profile) => 0;

    public void SaveCustomProfileOc(PowerProfile profile, int core, int memory)
    {
        SettingsManager.Save($"GpuCore_{profile}", core);
        SettingsManager.Save($"GpuMemory_{profile}", memory);
    }

    public async Task ResetProfileToDefaultsAsync(PowerProfile profile, string source = "reset")
    {
        SettingsManager.Save($"GpuCore_{profile}", GetDefaultCore(profile));
        SettingsManager.Save($"GpuMemory_{profile}", GetDefaultMemory(profile));
        await ApplyPowerProfileOcAsync(profile, allowStockReset: true, source: source);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try { NVIDIA.Unload(); } catch { }
            if (_nvmlInitialized)
            {
                try { NativeNvml.Shutdown(); } catch { }
            }
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

        [DllImport(NvmlDll, EntryPoint = "nvmlSystemGetDriverVersion", CharSet = CharSet.Ansi)]
        public static extern NvmlReturn SystemGetDriverVersion(StringBuilder version, uint length);
    }
    #endregion
}
