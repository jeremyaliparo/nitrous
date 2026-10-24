using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Finds the OEM services Acer installs for Predator/Nitro hardware and can reversibly stop and
/// disable them. A backup of the original start modes is written before the first change, and the
/// restore path puts the services back exactly as they were found.
/// </summary>
public static class AcerServiceManager
{
    private static readonly string[] MatchTokens = { "Acer", "Predator", "NitroSense" };
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string BackupFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nitrous",
        "acer-services-backup.json");

    public sealed record AcerServiceInfo(
        string Name,
        string DisplayName,
        string PathName,
        string StartMode,
        string State,
        bool DelayedAutoStart)
    {
        public bool IsDisabled => string.Equals(StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);

        public bool NeedsStop => State is "Running" or "Start Pending" or "Continue Pending"
            or "Paused" or "Pause Pending";
    }

    public sealed record AcerServiceState(
        IReadOnlyList<AcerServiceInfo> Services,
        bool BackupExists,
        string BackupPath,
        string? Error)
    {
        public int EnabledCount => Services.Count(service => !service.IsDisabled);
        public int RunningCount => Services.Count(service => service.NeedsStop);
        public bool CanDisable => Services.Count > 0 && (EnabledCount > 0 || RunningCount > 0);
        public bool CanRestore => BackupExists;
    }

    public sealed record AcerServiceOperationResult(bool Success, string Message, int Changed);

    public static AcerServiceState GetState()
    {
        List<AcerServiceInfo> services;
        try
        {
            services = DiscoverServices();
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: state query failed.", ex);
            return new AcerServiceState(Array.Empty<AcerServiceInfo>(), false, BackupFilePath, ex.Message);
        }

        try
        {
            var backup = ReadBackup();
            return new AcerServiceState(services, backup != null, BackupFilePath, null);
        }
        catch (Exception ex)
        {
            // Keep the discovered services visible, but surface the unreadable backup so the UI
            // can refuse to change anything until the backup is repaired or removed.
            Logger.Warn("Acer services: backup state is unreadable.", ex);
            return new AcerServiceState(services, File.Exists(BackupFilePath), BackupFilePath, ex.Message);
        }
    }

    /// <summary>Stops and disables every detected service, preserving the original state first.</summary>
    public static async Task<AcerServiceOperationResult> DisableAllAsync()
    {
        await OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(DisableAll).ConfigureAwait(false);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>Restores the start modes and running state captured by <see cref="DisableAllAsync"/>.</summary>
    public static async Task<AcerServiceOperationResult> RestoreAllAsync()
    {
        await OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(RestoreAll).ConfigureAwait(false);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var state = GetState();
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["backupExists"] = state.BackupExists,
            ["backupPath"] = state.BackupPath,
            ["enabledCount"] = state.EnabledCount,
            ["runningCount"] = state.RunningCount,
            ["error"] = state.Error,
            ["services"] = state.Services
                .Select(service => new Dictionary<string, object?>
                {
                    ["name"] = service.Name,
                    ["displayName"] = service.DisplayName,
                    ["pathName"] = service.PathName,
                    ["startMode"] = service.StartMode,
                    ["state"] = service.State,
                    ["delayedAutoStart"] = service.DelayedAutoStart
                })
                .ToList()
        };
    }

    private static AcerServiceOperationResult DisableAll()
    {
        try
        {
            var services = DiscoverServices();
            if (services.Count == 0)
                return new AcerServiceOperationResult(false, "No Acer-related services were found.", 0);

            var needsChange = services
                .Where(service => !service.IsDisabled || service.NeedsStop)
                .ToList();

            if (needsChange.Count == 0)
                return new AcerServiceOperationResult(
                    true, "All detected Acer services are already stopped and disabled.", 0);

            var backup = ReadBackup();
            if (backup == null)
                backup = new BackupDocument { CreatedUtc = DateTimeOffset.UtcNow };

            var backupChanged = false;
            foreach (var service in services)
            {
                if (backup.Services.Any(entry =>
                        string.Equals(entry.Name, service.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                backup.Services.Add(new BackupEntry
                {
                    Name = service.Name,
                    DisplayName = service.DisplayName,
                    StartMode = service.StartMode,
                    DelayedAutoStart = service.DelayedAutoStart,
                    WasRunning = service.NeedsStop
                });
                backupChanged = true;
            }

            if (backupChanged)
            {
                if (!TryWriteBackup(backup, out var backupError))
                    return new AcerServiceOperationResult(
                        false, $"Could not save the service backup; nothing was changed. {backupError}", 0);
            }

            var errors = new List<string>();
            StopServices(services, errors);
            DisableServices(services, errors);

            var after = DiscoverServices();
            var remainingEnabled = after.Count(service => !service.IsDisabled);
            var remainingRunning = after.Count(service => service.NeedsStop);

            if (errors.Count > 0 || remainingEnabled > 0 || remainingRunning > 0)
            {
                var detail = errors.Count > 0
                    ? " " + string.Join(" ", errors.Take(3))
                    : "";
                return new AcerServiceOperationResult(
                    false,
                    $"Disabled {needsChange.Count - remainingEnabled} service(s). " +
                    $"{remainingEnabled} still enabled and {remainingRunning} still running.{detail}",
                    needsChange.Count - remainingEnabled);
            }

            Logger.Info($"Acer services: stopped and disabled {needsChange.Count} service(s).");
            return new AcerServiceOperationResult(
                true, $"Stopped and disabled {needsChange.Count} Acer service(s).", needsChange.Count);
        }
        catch (Exception ex)
        {
            Logger.Error("Acer services: disable failed.", ex);
            return new AcerServiceOperationResult(false, ex.Message, 0);
        }
    }

    private static AcerServiceOperationResult RestoreAll()
    {
        try
        {
            var backup = ReadBackup();
            if (backup == null)
                return new AcerServiceOperationResult(true, "No Acer service backup was found.", 0);

            var errors = new List<string>();
            var restored = 0;

            foreach (var entry in backup.Services)
            {
                var startMode = GetScStartMode(entry);
                var result = RunSc("config", entry.Name, "start=", startMode);
                if (!result.Success && result.ExitCode != 1060)
                {
                    errors.Add($"{entry.Name}: could not set start type ({result.Output}).");
                    continue;
                }

                restored++;
            }

            // Start services only after every start type has been restored.
            var toStart = backup.Services
                .Where(entry => entry.WasRunning &&
                                !string.Equals(entry.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Name)
                .ToList();

            for (var pass = 0; pass < 3 && toStart.Count > 0; pass++)
            {
                foreach (var name in toStart.ToList())
                {
                    var result = RunSc("start", name);
                    // 1056 = already running, 1060 = service no longer exists.
                    if (result.Success || result.ExitCode is 1056 or 1060)
                        toStart.Remove(name);
                }

                if (toStart.Count > 0)
                    Thread.Sleep(500);
            }

            foreach (var name in toStart)
                errors.Add($"{name}: could not start the service.");

            if (errors.Count > 0)
            {
                Logger.Warn($"Acer services: restore incomplete; backup retained. {string.Join(" ", errors.Take(3))}");
                return new AcerServiceOperationResult(
                    false,
                    $"Restored {restored}/{backup.Services.Count} service(s), but some changes failed. " +
                    "The backup was kept.",
                    restored);
            }

            TryDeleteBackup();
            Logger.Info($"Acer services: restored {restored} service(s).");
            return new AcerServiceOperationResult(
                true, $"Restored {restored} Acer service(s) to their original state.", restored);
        }
        catch (Exception ex)
        {
            Logger.Error("Acer services: restore failed.", ex);
            return new AcerServiceOperationResult(false, ex.Message, 0);
        }
    }

    private static void StopServices(List<AcerServiceInfo> services, List<string> errors)
    {
        var remaining = services.Where(service => service.NeedsStop).Select(service => service.Name).ToList();

        for (var pass = 0; pass < 2 && remaining.Count > 0; pass++)
        {
            foreach (var name in remaining)
            {
                var result = RunSc("stop", name);
                if (!result.Success && result.ExitCode is not (1060 or 1062))
                    Logger.Warn($"Acer services: stop {name} returned {result.ExitCode}: {result.Output}");
            }

            var deadline = DateTime.UtcNow.AddSeconds(pass == 0 ? 10 : 5);
            while (DateTime.UtcNow < deadline)
            {
                var states = QueryServiceStates();
                remaining = remaining
                    .Where(name => states.TryGetValue(name, out var state) && IsRunningState(state.State))
                    .ToList();

                if (remaining.Count == 0) break;
                Thread.Sleep(250);
            }
        }

        foreach (var name in remaining)
            errors.Add($"{name}: still running.");
    }

    private static void DisableServices(List<AcerServiceInfo> services, List<string> errors)
    {
        foreach (var service in services.Where(service => !service.IsDisabled))
        {
            var result = RunSc("config", service.Name, "start=", "disabled");
            if (!result.Success && result.ExitCode != 1060)
                errors.Add($"{service.Name}: could not disable ({result.Output}).");
        }
    }

    private static List<AcerServiceInfo> DiscoverServices()
    {
        var result = new List<AcerServiceInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, PathName, StartMode, State FROM Win32_Service");
        using var collection = searcher.Get();

        foreach (ManagementBaseObject raw in collection)
        {
            using var item = (ManagementObject)raw;
            var name = item["Name"]?.ToString() ?? "";
            var displayName = item["DisplayName"]?.ToString() ?? name;
            var pathName = item["PathName"]?.ToString() ?? "";
            var startMode = item["StartMode"]?.ToString() ?? "Unknown";
            var state = item["State"]?.ToString() ?? "Unknown";

            if (!IsAcerRelated(name, displayName, pathName)) continue;

            result.Add(new AcerServiceInfo(
                name,
                displayName,
                pathName,
                startMode,
                state,
                ReadDelayedAutoStart(name)));
        }

        return result
            .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAcerRelated(string name, string displayName, string pathName)
    {
        foreach (var token in MatchTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                pathName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ReadDelayedAutoStart(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var value = key?.GetValue("DelayedAutoStart");
            return value != null && Convert.ToInt32(value) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, (string State, string StartMode)> QueryServiceStates()
    {
        var result = new Dictionary<string, (string State, string StartMode)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, State, StartMode FROM Win32_Service");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject raw in collection)
            {
                using var item = (ManagementObject)raw;
                var name = item["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[name] = (item["State"]?.ToString() ?? "Unknown",
                    item["StartMode"]?.ToString() ?? "Unknown");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: could not refresh service states.", ex);
        }

        return result;
    }

    private static bool IsRunningState(string state) =>
        state is "Running" or "Start Pending" or "Continue Pending" or "Paused" or "Pause Pending";

    private static string GetScStartMode(BackupEntry entry)
    {
        if (string.Equals(entry.StartMode, "Boot", StringComparison.OrdinalIgnoreCase))
            return "boot";
        if (string.Equals(entry.StartMode, "System", StringComparison.OrdinalIgnoreCase))
            return "system";
        if (string.Equals(entry.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase))
            return "disabled";
        if (string.Equals(entry.StartMode, "Manual", StringComparison.OrdinalIgnoreCase))
            return "demand";
        if (string.Equals(entry.StartMode, "Auto", StringComparison.OrdinalIgnoreCase))
            return entry.DelayedAutoStart ? "delayed-auto" : "auto";
        return "demand";
    }

    private static (bool Success, int ExitCode, string Output) RunSc(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { }
                return (false, -1, "sc.exe timed out");
            }

            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return (process.ExitCode == 0, process.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            Logger.Warn($"Acer services: could not run sc.exe {string.Join(" ", arguments)}.", ex);
            return (false, -1, ex.Message);
        }
    }

    private static BackupDocument? ReadBackup()
    {
        if (!File.Exists(BackupFilePath)) return null;

        try
        {
            var document = JsonSerializer.Deserialize<BackupDocument>(
                File.ReadAllText(BackupFilePath), JsonOptions);
            if (document == null)
                throw new InvalidDataException("The Acer service backup is empty.");

            document.Services ??= new List<BackupEntry>();
            if (document.Services.Count == 0 || document.Services.Any(entry => string.IsNullOrWhiteSpace(entry.Name)))
                throw new InvalidDataException("The Acer service backup has no valid service entries.");

            return document;
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: the service backup could not be read.", ex);
            throw new InvalidDataException("The Acer service backup is unreadable; refusing to change services.", ex);
        }
    }

    private static bool TryWriteBackup(BackupDocument document, out string? error)
    {
        try
        {
            var directory = Path.GetDirectoryName(BackupFilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var temporaryPath = BackupFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, BackupFilePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Warn("Acer services: could not save the service backup.", ex);
            return false;
        }
    }

    private static void TryDeleteBackup()
    {
        try
        {
            if (File.Exists(BackupFilePath)) File.Delete(BackupFilePath);
        }
        catch (Exception ex)
        {
            Logger.Warn("Acer services: could not remove the service backup.", ex);
        }
    }

    private sealed class BackupDocument
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset CreatedUtc { get; set; }
        public List<BackupEntry> Services { get; set; } = new();
    }

    private sealed class BackupEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string StartMode { get; set; } = "";
        public bool DelayedAutoStart { get; set; }
        public bool WasRunning { get; set; }
    }
}
