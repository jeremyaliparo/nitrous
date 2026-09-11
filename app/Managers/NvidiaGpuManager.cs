using System;
using System.Linq;
using System.Threading.Tasks;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using Nitrous.Enums;

namespace Nitrous.Managers;

public class NvidiaGpuManager : IDisposable
{
    private PhysicalGPU? _internalGpu;
    public bool IsValid => _internalGpu != null;

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
        try
        {
            try { NVIDIA.Unload(); } catch { }

            NVIDIA.Initialize();
            _internalGpu = GetInternalDiscreteGpu();
        }
        catch
        {
            _internalGpu = null;
        }
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            return PhysicalGPU
                .GetPhysicalGPUs()
                .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop)
                ?? PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public bool GetClocks(out int core, out int memory)
    {
        core = memory = 0;
        if (!IsValid) return false;

        try
        {
            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(_internalGpu!.Handle);
            var p0Clocks = states.Clocks[PerformanceStateId.P0_3DPerformance];

            var coreClock = p0Clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Graphics);
            var memClock = p0Clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Memory);

            core = (coreClock?.FrequencyDeltaInkHz.DeltaValue ?? 0) / 1000;
            memory = (memClock?.FrequencyDeltaInkHz.DeltaValue ?? 0) / 1000;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public int SetClocks(int core, int memory) => SetClocksInternal(core, memory, persist: true);

    private int SetClocksInternal(int core, int memory, bool persist)
    {
        if (!IsValid) return 0;

        if (core < MinCoreOffset || core > MaxCoreOffset) return 0;
        if (memory < MinMemoryOffset || memory > MaxMemoryOffset) return 0;

        if (persist) SaveProfile(core, memory);

        GetClocks(out int currentCore, out int currentMemory);

        if (Math.Abs(core - currentCore) < 5 && Math.Abs(memory - currentMemory) < 5)
            return 1;

        var coreClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000));
        var memoryClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000));

        PerformanceStates20ClockEntryV1[] clocks = { coreClock, memoryClock };
        PerformanceStates20BaseVoltageEntryV1[] voltages = { };

        PerformanceStates20InfoV1.PerformanceState20[] performanceStates = {
                new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, voltages)
            };

        var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);

        try
        {
            GPUApi.SetPerformanceStates20(_internalGpu!.Handle, overclock);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public void ResetOverclock()
    {
        SetClocksInternal(0, 0, persist: false);
        DeleteProfile();
    }

    private void SaveProfile(int core, int memory)
    {
        SettingsManager.Save("GpuCoreOffset", core);
        SettingsManager.Save("GpuMemoryOffset", memory);
    }

    private void DeleteProfile()
    {
        // Resetting to 0 effectively deletes the overclock profile
        SettingsManager.Save("GpuCoreOffset", 0);
        SettingsManager.Save("GpuMemoryOffset", 0);
    }

    public async Task RestoreAtBootAsync()
    {
        int core = SettingsManager.Get("GpuCoreOffset", 0);
        int memory = SettingsManager.Get("GpuMemoryOffset", 0);

        if (core == 0 && memory == 0) return;

        const int maxAttempts = 8;
        const int delayMs = 1500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (_internalGpu == null)
                {
                    InitializeNvAPI();
                }

                if (IsValid)
                {
                    int result = SetClocksInternal(core, memory, persist: false);
                    if (result == 1) return;
                }
            }
            catch { }

            await Task.Delay(delayMs);
        }
    }

    public int ApplyPowerProfileOc(PowerProfile profile)
    {
        int defaultCore = 0;
        int defaultMemory = 0;

        switch (profile)
        {
            case PowerProfile.Quiet:
                defaultCore = -100;
                defaultMemory = -200;
                break;
            case PowerProfile.Balanced:
                defaultCore = 0;
                defaultMemory = 0;
                break;
            case PowerProfile.Performance:
                defaultCore = 100;
                defaultMemory = 150;
                break;
            case PowerProfile.Turbo:
                defaultCore = 150;
                defaultMemory = 300;
                break;
        }

        // Fetch the user's custom config for this profile, falling back to the defaults
        int coreOffset = SettingsManager.Get($"GpuCore_{profile}", defaultCore);
        int memoryOffset = SettingsManager.Get($"GpuMemory_{profile}", defaultMemory);

        // Apply the clocks. (persist: false because we don't want to overwrite the "Global/Manual" profile)
        return SetClocksInternal(coreOffset, memoryOffset, persist: false);
    }

    public void Dispose()
    {
        try
        {
            NVIDIA.Unload();
        }
        catch { }
    }
}
