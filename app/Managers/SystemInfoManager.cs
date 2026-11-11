using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using System.Xml;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public static class SystemInfoManager
{
    private static string? _cachedModel;

    private static readonly object BatteryPowerGate = new();
    private static bool _batteryPowerFailureLogged;

    public sealed record BatteryPowerTelemetry(
        bool PowerOnline,
        bool Charging,
        bool Discharging,
        int ChargeRateMw,
        int DischargeRateMw,
        int VoltageMv);

    /// <summary>Wear of the pack: what it holds now against what it was built to hold.</summary>
    public sealed record BatteryHealthInfo(int DesignCapacityMwh, int FullChargeCapacityMwh, int? CycleCount)
    {
        public int Percent => DesignCapacityMwh <= 0
            ? 0
            : (int)Math.Round(100.0 * FullChargeCapacityMwh / DesignCapacityMwh);
    }

    /// <summary>
    /// Design capacity, current full-charge capacity and cycle count. Read-only, no Acer interface
    /// involved, so it works on every laptop. Null when the pack reports no design capacity, which
    /// would make a percentage meaningless.
    /// </summary>
    public static BatteryHealthInfo? GetBatteryHealth() => ReadBatteryClasses() ?? ReadBatteryReport();

    /// <summary>
    /// The direct WMI classes. Some battery drivers fail to instantiate BatteryStaticData even
    /// though the class is listed (the AN16S-61 does), hence the report fallback below.
    /// </summary>
    private static BatteryHealthInfo? ReadBatteryClasses()
    {
        try
        {
            int design = 0;
            int? cycles = null;

            using (var searcher = new ManagementObjectSearcher(
                       @"root\wmi", "SELECT DesignedCapacity, CycleCount FROM BatteryStaticData"))
            {
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using (item)
                    {
                        design = Convert.ToInt32(item["DesignedCapacity"] ?? 0);
                        if (item["CycleCount"] is { } reported && Convert.ToInt32(reported) > 0)
                            cycles = Convert.ToInt32(reported);
                        break;
                    }
                }
            }

            var full = 0;
            using (var searcher = new ManagementObjectSearcher(
                       @"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            {
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    using (item)
                    {
                        full = Convert.ToInt32(item["FullChargedCapacity"] ?? 0);
                        break;
                    }
                }
            }

            return design > 0 && full > 0 ? new BatteryHealthInfo(design, full, cycles) : null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or FormatException
                                       or InvalidCastException)
        {
            Logger.Info($"System info: BatteryStaticData unavailable ({ex.Message}); using the battery report.");
            return null;
        }
    }

    /// <summary>
    /// <c>powercfg /batteryreport /xml</c> carries the same figures Windows shows, including the
    /// design capacity the class above cannot always provide. Written to a temp file, parsed, deleted.
    /// </summary>
    private static BatteryHealthInfo? ReadBatteryReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nitrous-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var start = new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{path}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start);
            if (process is null) return null;

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            if (!File.Exists(path)) return null;

            var document = XDocument.Load(path);
            XNamespace ns = "http://schemas.microsoft.com/battery/2012";
            var battery = document.Descendants(ns + "Battery")
                .FirstOrDefault(b => (int?)b.Element(ns + "DesignCapacity") > 0);
            if (battery is null) return null;

            var design = (int?)battery.Element(ns + "DesignCapacity") ?? 0;
            var full = (int?)battery.Element(ns + "FullChargeCapacity") ?? 0;
            var cycles = (int?)battery.Element(ns + "CycleCount");
            if (design <= 0 || full <= 0) return null;

            return new BatteryHealthInfo(design, full, cycles is > 0 ? cycles : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Logger.Warn("System info: the battery report could not be read.", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string GetSystemModel()
    {
        if (_cachedModel != null) return _cachedModel;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    _cachedModel = item["Model"]?.ToString() ?? "Unknown System";
                    return _cachedModel;
                }
            }
        }
        catch { }
        return "Unknown System";
    }

    /// <summary>
    /// Reads the battery's measured charge/discharge rate. Windows does not expose the adapter's
    /// rated wattage through this class, so callers must label these values accordingly.
    /// BatteryStatus is a keyless WMI singleton, so it cannot be refreshed with
    /// <see cref="ManagementObject.Get"/>; query it again on every poll instead.
    /// </summary>
    public static BatteryPowerTelemetry GetBatteryPowerTelemetry()
    {
        lock (BatteryPowerGate)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT PowerOnline, Charging, Discharging, ChargeRate, DischargeRate, Voltage " +
                    "FROM BatteryStatus");
                using var collection = searcher.Get();

                foreach (ManagementBaseObject item in collection)
                {
                    using (item)
                    {
                        if (item is not ManagementObject instance) continue;

                        _batteryPowerFailureLogged = false;
                        return new BatteryPowerTelemetry(
                            ReadBool(instance, "PowerOnline"),
                            ReadBool(instance, "Charging"),
                            ReadBool(instance, "Discharging"),
                            ReadInt(instance, "ChargeRate"),
                            ReadInt(instance, "DischargeRate"),
                            ReadInt(instance, "Voltage"));
                    }
                }

                return new BatteryPowerTelemetry(false, false, false, 0, 0, 0);
            }
            catch (Exception ex)
            {
                if (!_batteryPowerFailureLogged)
                {
                    Logger.Warn("System info: battery power telemetry failed; will retry.", ex);
                    _batteryPowerFailureLogged = true;
                }

                return new BatteryPowerTelemetry(false, false, false, 0, 0, 0);
            }
        }
    }

    /// <summary>
    /// Read-only hardware snapshot for diagnostics. Serial numbers, UUIDs, and other unique
    /// identifiers are deliberately excluded so the bundle is safe to attach to an issue.
    /// </summary>
    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = GetHardwareSnapshot();
        var batteryPower = GetBatteryPowerTelemetry();
        result["batteryPower"] = new Dictionary<string, object?>
        {
            ["powerOnline"] = batteryPower.PowerOnline,
            ["charging"] = batteryPower.Charging,
            ["discharging"] = batteryPower.Discharging,
            ["chargeRateMw"] = batteryPower.ChargeRateMw,
            ["dischargeRateMw"] = batteryPower.DischargeRateMw,
            ["voltageMv"] = batteryPower.VoltageMv
        };
        result["os"] = GetOsSnapshot();
        return result;
    }

    private static bool ReadBool(ManagementBaseObject instance, string property) =>
        instance[property] is bool value && value;

    private static int ReadInt(ManagementBaseObject instance, string property)
    {
        try
        {
            return Convert.ToInt32(instance[property] ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private static Dictionary<string, object?> GetHardwareSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        result["computer"] = QueryOne(
            "SELECT Manufacturer, Model, SystemType, TotalPhysicalMemory, NumberOfLogicalProcessors, PCSystemType " +
            "FROM Win32_ComputerSystem",
            "Manufacturer", "Model", "SystemType", "TotalPhysicalMemory",
            "NumberOfLogicalProcessors", "PCSystemType");

        result["bios"] = QueryOne(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate");

        result["baseboard"] = QueryOne(
            "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard",
            "Manufacturer", "Product", "Version");

        result["cpus"] = QueryMany(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, " +
            "LoadPercentage, Status FROM Win32_Processor",
            8,
            "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed",
            "CurrentClockSpeed", "LoadPercentage", "Status");

        result["memoryModules"] = QueryMany(
            "SELECT BankLabel, DeviceLocator, Capacity, Speed, Manufacturer, PartNumber, SMBIOSMemoryType " +
            "FROM Win32_PhysicalMemory",
            16,
            "BankLabel", "DeviceLocator", "Capacity", "Speed", "Manufacturer", "PartNumber", "SMBIOSMemoryType");

        result["gpus"] = QueryMany(
            "SELECT Name, DriverVersion, DriverDate, VideoProcessor, AdapterRAM, Status, " +
            "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate " +
            "FROM Win32_VideoController",
            8,
            "Name", "DriverVersion", "DriverDate", "VideoProcessor", "AdapterRAM", "Status",
            "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate");

        result["battery"] = QueryOne(
            "SELECT Name, EstimatedChargeRemaining, BatteryStatus, Chemistry, DesignVoltage " +
            "FROM Win32_Battery",
            "Name", "EstimatedChargeRemaining", "BatteryStatus", "Chemistry", "DesignVoltage");

        result["chassis"] = QueryOne(
            "SELECT ChassisTypes, Manufacturer, Version FROM Win32_SystemEnclosure",
            "ChassisTypes", "Manufacturer", "Version");

        result["storage"] = QueryMany(
            "SELECT Model, InterfaceType, MediaType, Size, Status FROM Win32_DiskDrive",
            8,
            "Model", "InterfaceType", "MediaType", "Size", "Status");

        result["devices"] = QueryMany(
            "SELECT Name, Status, ClassGuid FROM Win32_PnPEntity " +
            "WHERE Name LIKE '%Acer%' OR Name LIKE '%NVIDIA%'",
            50,
            "Name", "Status", "ClassGuid");

        return result;
    }

    private static Dictionary<string, object?> GetOsSnapshot()
    {
        return QueryOne(
            "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, " +
            "TotalVisibleMemorySize, FreePhysicalMemory, Locale FROM Win32_OperatingSystem",
            "Caption", "Version", "BuildNumber", "OSArchitecture", "LastBootUpTime",
            "TotalVisibleMemorySize", "FreePhysicalMemory", "Locale");
    }

    private static Dictionary<string, object?> QueryOne(string wql, params string[] properties)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                using (item)
                {
                    foreach (var property in properties)
                    {
                        try { result[property] = Normalize(item[property]); }
                        catch { /* property missing on this WMI provider */ }
                    }
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"System info: query failed ({wql}).", ex);
            result["Error"] = ex.Message;
        }

        return result;
    }

    private static List<Dictionary<string, object?>> QueryMany(
        string wql,
        int maxRows,
        params string[] properties)
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var collection = searcher.Get();
            foreach (ManagementBaseObject item in collection)
            {
                if (result.Count >= maxRows) break;

                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in properties)
                    {
                        try { row[property] = Normalize(item[property]); }
                        catch { /* property missing on this WMI provider */ }
                    }

                    result.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"System info: query failed ({wql}).", ex);
            result.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Error"] = ex.Message
            });
        }

        return result;
    }

    private static object? Normalize(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString("O"),
            Array array => array.Cast<object?>().Select(Normalize).ToArray(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal => value,
            _ => value.ToString()
        };
    }
}
