using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

public static class SystemInfoManager
{
    private static string? _cachedModel;

    private static readonly object BatteryPowerGate = new();
    private static ManagementObjectSearcher? _batteryPowerSearcher;
    private static ManagementObjectCollection? _batteryPowerCollection;
    private static ManagementObject? _batteryPowerInstance;
    private static bool _batteryPowerFailureLogged;

    public sealed record BatteryPowerTelemetry(
        bool PowerOnline,
        bool Charging,
        bool Discharging,
        int ChargeRateMw,
        int DischargeRateMw,
        int VoltageMv);

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
    /// </summary>
    public static BatteryPowerTelemetry GetBatteryPowerTelemetry()
    {
        lock (BatteryPowerGate)
        {
            try
            {
                if (_batteryPowerInstance == null)
                {
                    _batteryPowerSearcher = new ManagementObjectSearcher(
                        @"root\wmi",
                        "SELECT PowerOnline, Charging, Discharging, ChargeRate, DischargeRate, Voltage " +
                        "FROM BatteryStatus");
                    _batteryPowerCollection = _batteryPowerSearcher.Get();

                    foreach (ManagementBaseObject item in _batteryPowerCollection)
                    {
                        if (item is not ManagementObject instance) continue;
                        _batteryPowerInstance = instance;
                        break;
                    }
                }

                if (_batteryPowerInstance == null)
                    return new BatteryPowerTelemetry(false, false, false, 0, 0, 0);

                return new BatteryPowerTelemetry(
                    ReadBool(_batteryPowerInstance, "PowerOnline"),
                    ReadBool(_batteryPowerInstance, "Charging"),
                    ReadBool(_batteryPowerInstance, "Discharging"),
                    ReadInt(_batteryPowerInstance, "ChargeRate"),
                    ReadInt(_batteryPowerInstance, "DischargeRate"),
                    ReadInt(_batteryPowerInstance, "Voltage"));
            }
            catch (Exception ex)
            {
                if (!_batteryPowerFailureLogged)
                {
                    Logger.Warn("System info: battery power telemetry failed; will retry.", ex);
                    _batteryPowerFailureLogged = true;
                }

                DisposeBatteryPowerLocked();
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

    private static void DisposeBatteryPowerLocked()
    {
        try { _batteryPowerCollection?.Dispose(); } catch { }
        try { _batteryPowerSearcher?.Dispose(); } catch { }
        _batteryPowerCollection = null;
        _batteryPowerSearcher = null;
        _batteryPowerInstance = null;
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
