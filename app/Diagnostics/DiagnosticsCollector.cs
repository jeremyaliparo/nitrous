using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nitrous.Managers;

namespace Nitrous.Diagnostics;

public sealed record DiagnosticsExportResult(string ZipPath, int ErrorCount);

/// <summary>
/// Builds a curated, redacted support bundle. It intentionally does not dump raw WMI objects
/// or the whole registry: only the values needed to diagnose Nitrous are collected, and every
/// section is bounded so a sleeping GPU or a slow provider cannot hang the dashboard.
/// </summary>
public static class DiagnosticsCollector
{
    private const int MaxEventCharacters = 512 * 1024;
    private const int MaxEventCount = 150;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan SystemTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CpuTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(45);

    public static string OutputDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nitrous", "diagnostics");

    public static async Task<DiagnosticsExportResult> ExportAsync(
        Func<CancellationToken, Task<Dictionary<string, object?>>>? gpuCollector = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(OutputDirectory);
        CleanupOldTemporaryDirectories();

        var temporaryDirectory = Path.Combine(OutputDirectory, $".tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        var errors = new List<string>();
        var report = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Logger.Info("Diagnostics: export started.");

            progress?.Report("Collecting system information...");
            var system = await CollectSectionAsync(
                "system",
                () => Task.Run(SystemInfoManager.GetDiagnosticsSnapshot, cancellationToken),
                SystemTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (system != null)
            {
                system["process"] = BuildProcessSnapshot();
                report["system"] = system;
                WriteJson(temporaryDirectory, "system.json", system);
            }

            progress?.Report("Collecting Nitrous settings...");
            var settings = await CollectSectionAsync(
                "settings",
                () => Task.Run(SettingsManager.GetAllValues, cancellationToken),
                TimeSpan.FromSeconds(5),
                errors,
                cancellationToken).ConfigureAwait(false);

            if (settings != null)
            {
                report["settings"] = settings;
                WriteJson(temporaryDirectory, "nitrous-settings.json", settings);
            }

            progress?.Report("Probing Acer WMI...");
            var acer = await CollectSectionAsync(
                "acer-wmi",
                () => Task.Run(AcerWmiManager.GetDiagnosticsSnapshot, cancellationToken),
                WmiTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (acer != null)
            {
                report["acer"] = acer;
                WriteJson(temporaryDirectory, "acer-wmi.json", acer);
            }

            progress?.Report("Reading CPU power limits...");
            var cpuPower = await CollectSectionAsync(
                "cpu-power",
                () => Task.Run(CpuPowerManager.GetDiagnosticsSnapshot, cancellationToken),
                CpuTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (cpuPower != null)
            {
                report["cpu-power"] = cpuPower;
                WriteJson(temporaryDirectory, "cpu-power.json", cpuPower);
            }

            progress?.Report("Inspecting Acer services...");
            var acerServices = await CollectSectionAsync(
                "acer-services",
                () => Task.Run(AcerServiceManager.GetDiagnosticsSnapshot, cancellationToken),
                WmiTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (acerServices != null)
            {
                report["acer-services"] = acerServices;
                WriteJson(temporaryDirectory, "acer-services.json", acerServices);
            }

            progress?.Report("Reading display mode...");
            var display = await CollectSectionAsync(
                "display",
                () => Task.Run(DisplayManager.GetDiagnosticsSnapshot, cancellationToken),
                DisplayTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (display != null)
            {
                report["display"] = display;
                WriteJson(temporaryDirectory, "display.json", display);
            }

            progress?.Report("Querying startup task...");
            var startup = await CollectSectionAsync(
                "startup",
                () => Task.Run(StartupManager.GetDiagnosticsSnapshot, cancellationToken),
                StartupTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (startup != null)
            {
                report["startup"] = startup;
                WriteJson(temporaryDirectory, "startup.json", startup);
            }

            progress?.Report("Reading update state...");
            var update = await CollectSectionAsync(
                "update",
                () => Task.Run(UpdateManager.GetDiagnosticsSnapshot, cancellationToken),
                TimeSpan.FromSeconds(10),
                errors,
                cancellationToken).ConfigureAwait(false);

            if (update != null)
            {
                report["update"] = update;
                WriteJson(temporaryDirectory, "updates.json", update);
            }

            progress?.Report("Reading GPU state...");
            Func<Task<Dictionary<string, object?>>> collectGpu = gpuCollector != null
                ? () => gpuCollector(cancellationToken)
                : () => CollectGpuWithTemporaryManagerAsync(cancellationToken);
            var gpu = await CollectSectionAsync(
                "gpu",
                collectGpu,
                GpuTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (gpu != null)
            {
                report["gpu"] = gpu;
                WriteJson(temporaryDirectory, "gpu.json", gpu);
            }

            progress?.Report("Collecting recent event-log excerpts...");
            var events = await CollectSectionAsync(
                "events",
                () => CollectEventLogsAsync(cancellationToken),
                EventTimeout,
                errors,
                cancellationToken).ConfigureAwait(false);

            if (events != null)
            {
                foreach (var pair in events)
                    WriteText(temporaryDirectory, $"events-{pair.Key.ToLowerInvariant()}.txt", pair.Value);
            }

            progress?.Report("Copying log files...");
            var logCount = CopyLogFiles(temporaryDirectory, errors);

            WriteText(temporaryDirectory, "summary.txt",
                BuildSummary(report, errors, logCount));
            WriteText(temporaryDirectory, "errors.txt",
                errors.Count == 0
                    ? "No diagnostic sections failed.\n"
                    : string.Join(Environment.NewLine, errors.Select(error => $"- {error}")) + Environment.NewLine);
            WriteText(temporaryDirectory, "README.txt", BuildReadme());

            var manifest = BuildManifest(temporaryDirectory, errors);
            WriteJson(temporaryDirectory, "manifest.json", manifest);

            var zipPath = GetZipPath();
            progress?.Report("Creating ZIP...");
            ZipFile.CreateFromDirectory(temporaryDirectory, zipPath, CompressionLevel.Optimal, false);

            Logger.Info($"Diagnostics: export completed ({zipPath}, {new FileInfo(zipPath).Length} bytes, " +
                        $"{errors.Count} section error(s)).");
            return new DiagnosticsExportResult(zipPath, errors.Count);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Diagnostics: could not open the output folder.", ex);
        }
    }

    private static async Task<Dictionary<string, object?>> CollectGpuWithTemporaryManagerAsync(
        CancellationToken cancellationToken)
    {
        using var manager = new NvidiaGpuManager();
        return await manager.GetDiagnosticsSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> CollectSectionAsync<T>(
        string name,
        Func<Task<T>> collect,
        TimeSpan timeout,
        List<string> errors,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await collect().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"{name}: {ex.GetType().Name}: {ex.Message}";
            errors.Add(message);
            Logger.Warn($"Diagnostics: {name} collection failed.", ex);
            return null;
        }
    }

    private static Dictionary<string, object?> BuildProcessSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["role"] = Logger.Role,
            ["processId"] = Environment.ProcessId,
            ["elevated"] = IsAdministrator(),
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["culture"] = CultureInfo.CurrentCulture.Name,
            ["uiCulture"] = CultureInfo.CurrentUICulture.Name,
            ["timeZone"] = TimeZoneInfo.Local.Id,
            ["executablePath"] = Environment.ProcessPath
        };

        try
        {
            using var process = Process.GetCurrentProcess();
            result["startedAt"] = process.StartTime.ToString("O");
            result["workingSetBytes"] = process.WorkingSet64;
            result["uptimeSeconds"] = Math.Max(0, (long)(DateTime.Now - process.StartTime).TotalSeconds);
        }
        catch
        {
            // Process metadata is optional.
        }

        return result;
    }

    private static int CopyLogFiles(string temporaryDirectory, List<string> errors)
    {
        var count = 0;
        foreach (var path in Logger.GetLogFiles())
        {
            try
            {
                var content = ReadAllTextShared(path);
                WriteText(temporaryDirectory, Path.Combine("logs", Path.GetFileName(path)), content);
                count++;
            }
            catch (Exception ex)
            {
                var message = $"logs/{Path.GetFileName(path)}: {ex.Message}";
                errors.Add(message);
                Logger.Warn($"Diagnostics: could not read log '{path}'.", ex);
            }
        }

        return count;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Task<Dictionary<string, string>> CollectEventLogsAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var logName in new[] { "System", "Application" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[logName] = ReadEventLog(logName);
            }

            return result;
        }, cancellationToken);
    }

    private static string ReadEventLog(string logName)
    {
        var header = $"# Recent {logName} events (Error, Warning, Critical; newest first; max {MaxEventCount})\n" +
                     $"# Collected {DateTime.Now:O}\n\n";

        try
        {
            var arguments = $"qe {logName} " +
                            $"/q:\"*[System[(Level=1 or Level=2 or Level=3)]]\" " +
                            $"/f:text /c:{MaxEventCount} /rd:true";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("wevtutil.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(); } catch { }
                return header + "# wevtutil timed out after 20 seconds.\n";
            }

            var output = stdoutTask.GetAwaiter().GetResult() +
                         stderrTask.GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(output))
                output = "# No matching events were found.\n";

            if (output.Length > MaxEventCharacters)
                output = output[..MaxEventCharacters] + "\n# Output truncated by Nitrous.\n";

            return header + output.Trim() + "\n";
        }
        catch (Exception ex)
        {
            Logger.Warn($"Diagnostics: could not read the {logName} event log.", ex);
            return header + $"# Could not read the event log: {ex.Message}\n";
        }
    }

    private static string BuildSummary(
        Dictionary<string, object?> report,
        List<string> errors,
        int logCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Nitrous Diagnostics");
        builder.AppendLine("===================");
        builder.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Nitrous version: {UpdateManager.CurrentVersion}");
        builder.AppendLine();

        var system = Section(report, "system");
        var process = Section(system, "process");
        builder.AppendLine($"Process: {Text(process, "role")} (pid {Text(process, "processId")}), " +
                           $"elevated={Text(process, "elevated")}");
        builder.AppendLine($"Runtime: {Text(process, "runtime")}");
        builder.AppendLine($"OS: {Text(process, "osDescription")}");

        var os = Section(system, "os");
        builder.AppendLine($"Windows: {Text(os, "Caption")} {Text(os, "Version")} " +
                           $"build {Text(os, "BuildNumber")} ({Text(os, "OSArchitecture")})");

        var computer = Section(system, "computer");
        builder.AppendLine($"Model: {Text(computer, "Manufacturer")} {Text(computer, "Model")}");
        builder.AppendLine($"System type: {Text(computer, "SystemType")}");

        var bios = Section(system, "bios");
        builder.AppendLine($"BIOS: {Text(bios, "SMBIOSBIOSVersion")} ({FormatWmiDate(Text(bios, "ReleaseDate"))})");

        var cpus = Rows(system, "cpus");
        var cpu = cpus.FirstOrDefault();
        builder.AppendLine($"CPU: {Text(cpu, "Name")} " +
                           $"({Text(cpu, "NumberOfCores")} cores / {Text(cpu, "NumberOfLogicalProcessors")} threads)");
        builder.AppendLine($"RAM: {FormatBytes(Text(computer, "TotalPhysicalMemory"))}");

        var gpus = Rows(system, "gpus");
        foreach (var gpu in gpus)
            builder.AppendLine($"GPU: {Text(gpu, "Name")} | driver {Text(gpu, "DriverVersion")} | " +
                               $"{FormatWmiDate(Text(gpu, "DriverDate"))}");

        builder.AppendLine();
        var acer = Section(report, "acer");
        var profiles = Section(acer, "supportedProfiles");
        builder.AppendLine($"Acer WMI: supported={Text(acer, "hardwareSupported")}, " +
                           $"turbo={Text(acer, "turboSupported")}, " +
                           $"gamingClass={Text(acer, "gamingClassAvailable")}, " +
                           $"batteryClass={Text(acer, "batteryClassAvailable")}, " +
                           $"profileBitmap={Text(profiles, "bitmap")}");

        var telemetry = Section(acer, "telemetry");
        builder.AppendLine($"Acer telemetry: CPU {Text(telemetry, "cpuTemperatureC")} C / " +
                           $"{Text(telemetry, "cpuFanRpm")} RPM; " +
                           $"GPU {Text(telemetry, "gpuTemperatureC")} C / " +
                           $"{Text(telemetry, "gpuFanRpm")} RPM");

        var cpuPower = Section(report, "cpu-power");
        var ac = Section(cpuPower, "ac");
        var dc = Section(cpuPower, "dc");
        builder.AppendLine($"CPU power: scheme {Text(cpuPower, "activeSchemeGuid")}; " +
                           $"AC {Text(ac, "min")}/{Text(ac, "max")}%; " +
                           $"DC {Text(dc, "min")}/{Text(dc, "max")}%; " +
                           $"managed={Text(cpuPower, "manageCpuPower")}");
        builder.AppendLine($"CPU backups retained: {Rows(cpuPower, "backups").Count}");

        var boost = Section(cpuPower, "boost");
        if (boost.Count > 0)
        {
            builder.AppendLine($"CPU boost: AC {Text(boost, "ac")}, DC {Text(boost, "dc")}, " +
                               $"options {Rows(boost, "options").Count}, " +
                               $"fallback={Text(boost, "usedFallback")}");
        }

        var acerServices = Section(report, "acer-services");
        var acerServiceRows = Rows(acerServices, "services");
        builder.AppendLine($"Acer services: {acerServiceRows.Count} detected; " +
                           $"enabled={Text(acerServices, "enabledCount")}, " +
                           $"running={Text(acerServices, "runningCount")}, " +
                           $"backup={Text(acerServices, "backupExists")}");

        var batteryPower = Section(system, "batteryPower");
        builder.AppendLine($"Battery power: AC={Text(batteryPower, "powerOnline")}, " +
                           $"charge={Text(batteryPower, "chargeRateMw")} mW, " +
                           $"discharge={Text(batteryPower, "dischargeRateMw")} mW");

        var gpuReport = Section(report, "gpu");
        var offsets = Section(gpuReport, "offsets");
        var coreRange = Section(offsets, "coreDriverRangeMhz");
        var memoryRange = Section(offsets, "memoryDriverRangeMhz");
        var lastApplied = Section(offsets, "lastAppliedOffsets");
        builder.AppendLine($"GPU offsets: core {Text(offsets, "coreOffsetMhz")} MHz " +
                           $"[{Text(coreRange, "min")}..{Text(coreRange, "max")}], " +
                           $"memory {Text(offsets, "memoryOffsetMhz")} MHz " +
                           $"[{Text(memoryRange, "min")}..{Text(memoryRange, "max")}]; " +
                           $"NvAPI={Text(offsets, "nvapiInitialized")}, " +
                           $"editable={Text(offsets, "statesEditable")}");
        builder.AppendLine($"GPU clock management: {Text(offsets, "gpuClockManagement")}; " +
                           $"last applied {Text(lastApplied, "coreMhz")}/{Text(lastApplied, "memoryMhz")} MHz; " +
                           $"voltage overrides={Text(offsets, "voltageOverridesDetected")}, " +
                           $"P0 base voltage entries={Text(offsets, "baseVoltageCount")}");
        if (offsets.TryGetValue("error", out var gpuError))
            builder.AppendLine($"GPU offset error: {gpuError}");

        var nvml = Section(gpuReport, "nvml");
        builder.AppendLine($"NVML: {Text(nvml, "name")} ({Text(nvml, "architecture")}), " +
                           $"P-state {Text(nvml, "pState")}, {Text(nvml, "coreTemperatureC")} C, " +
                           $"{Text(nvml, "currentCoreClockMhz")}/{Text(nvml, "currentMemoryClockMhz")} MHz, " +
                           $"{Text(nvml, "powerDrawW")} W");

        var display = Section(report, "display");
        var mode = Section(display, "current");
        builder.AppendLine($"Display: {Text(mode, "width")}x{Text(mode, "height")} @ " +
                           $"{Text(mode, "refreshHz")} Hz (max {Text(display, "maxRefreshHz")} Hz)");

        var startup = Section(report, "startup");
        builder.AppendLine($"Startup task present: {Text(startup, "taskExists")}");

        var update = Section(report, "update");
        builder.AppendLine($"Updater: version {Text(update, "currentVersion")}; " +
                           $"exe {Text(update, "executablePath")}; " +
                           $"sha256={Text(update, "sha256")}");

        var settings = Section(report, "settings");
        builder.AppendLine($"Settings: ManageCpuPower={Text(settings, "ManageCpuPower")}, " +
                           $"LastPowerMode={Text(settings, "LastPowerMode")}, " +
                           $"LastFanMode={Text(settings, "LastFanMode")}, " +
                           $"RefreshMode={Text(settings, "RefreshMode")}");

        builder.AppendLine($"Log files included: {logCount}");
        if (errors.Count == 0)
        {
            builder.AppendLine("Section errors: none");
        }
        else
        {
            builder.AppendLine($"Section errors: {errors.Count}");
            foreach (var error in errors) builder.AppendLine($"  - {error}");
        }

        builder.AppendLine();
        builder.AppendLine("Attach the ZIP to the bug report. It redacts the current username, machine");
        builder.AppendLine("name, serial numbers, user-profile paths, SIDs, email addresses, and IP/MAC addresses.");
        return builder.ToString();
    }

    private static string BuildReadme()
    {
        return """
               Nitrous diagnostics bundle
               ===========================

               This ZIP was created by Nitrous' Settings > Diagnostics > Export Diagnostics button.
               It is intended to be attached to a GitHub issue. Nothing in it is uploaded
               automatically.

               Contents
               --------
               summary.txt              Human-readable overview.
               system.json              OS, CPU, GPU, memory, battery, display, and device info.
               nitrous-settings.json    Values under HKCU\Software\Nitrous only.
               acer-wmi.json            Acer WMI class availability, sensor probes, and telemetry.
               acer-services.json       Detected OEM services, start modes, and backup state.
               gpu.json                 NVAPI offsets, driver ranges, and NVML telemetry.
               cpu-power.json           Active scheme, current min/max, boost modes, and backups.
               display.json             Current and supported refresh modes.
               startup.json             Scheduled-task query output.
               updates.json             Version, executable hash, and updater artifacts.
               logs/                    Current and rotated Nitrous logs.
               events-system.txt        Recent System error/warning/critical events.
               events-application.txt   Recent Application error/warning/critical events.
               errors.txt               Sections that could not be collected.
               manifest.json            Machine-readable list of the files above.

               Privacy
               -------
               The exporter omits BIOS, motherboard, and memory serial numbers; system UUIDs;
               the current username and machine name; SIDs; user-profile paths; and IP/MAC
               addresses. WMI values are limited to the fields listed above, and event-log output
               is capped.

               Note
               ----
               A sleeping discrete GPU, an unsupported Acer WMI implementation, or a slow provider
               may make a section unavailable. Check errors.txt first.
               """;
    }

    private static Dictionary<string, object?> BuildManifest(
        string temporaryDirectory,
        List<string> errors)
    {
        var files = Directory
            .GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new Dictionary<string, object?>
            {
                ["name"] = Path.GetRelativePath(temporaryDirectory, path).Replace('\\', '/'),
                ["bytes"] = new FileInfo(path).Length
            })
            .OrderBy(file => file["name"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["createdUtc"] = DateTime.UtcNow.ToString("O"),
            ["nitrousVersion"] = UpdateManager.CurrentVersion,
            ["errors"] = errors,
            ["files"] = files
        };
    }

    private static void WriteJson(string directory, string fileName, object? value)
    {
        var redacted = DiagnosticsRedactor.RedactObject(value);
        var json = JsonSerializer.Serialize(redacted, JsonOptions);
        WriteText(directory, fileName, json);
    }

    private static void WriteText(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

        File.WriteAllText(path, DiagnosticsRedactor.Redact(content), new UTF8Encoding(false));
    }

    private static string GetZipPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"Nitrous-Diagnostics-{stamp}";
        var path = Path.Combine(OutputDirectory, baseName + ".zip");

        for (var suffix = 1; File.Exists(path); suffix++)
            path = Path.Combine(OutputDirectory, $"{baseName}-{suffix}.zip");

        return path;
    }

    private static void CleanupOldTemporaryDirectories()
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(OutputDirectory, ".tmp-*"))
            {
                if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddHours(-6)) continue;
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> Section(Dictionary<string, object?>? parent, string key)
    {
        if (parent != null &&
            parent.TryGetValue(key, out var value) &&
            value is Dictionary<string, object?> child)
        {
            return child;
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static List<Dictionary<string, object?>> Rows(
        Dictionary<string, object?>? parent,
        string key)
    {
        if (parent != null &&
            parent.TryGetValue(key, out var value) &&
            value is IEnumerable<Dictionary<string, object?>> rows)
        {
            return rows.ToList();
        }

        return new List<Dictionary<string, object?>>();
    }

    private static string Text(Dictionary<string, object?>? section, string key)
    {
        if (section == null ||
            !section.TryGetValue(key, out var value) ||
            value == null)
        {
            return "unknown";
        }

        var text = value.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
    }

    private static string FormatBytes(string value)
    {
        return ulong.TryParse(value, out var bytes)
            ? $"{bytes / (1024d * 1024d * 1024d):0.#} GB"
            : value;
    }

    private static string FormatWmiDate(string value)
    {
        // WMI datetime strings look like 20260306000000.000000+000.
        if (value.Length >= 8 &&
            DateTime.TryParseExact(
                value[..8],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return value;
    }
}
