using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Opens the vendor GPU software Nitrous deliberately does not replace: the NVIDIA App (driver
/// settings, DLSS overrides, per-app profiles) or the NVIDIA Control Panel.
///
/// Detection is layered because the Control Panel ships two ways. A deskop install has an exe on
/// disk; the DCH/Store build does not - it lives under WindowsApps behind a package identity and
/// has no shortcut file, so the only way to see it is the Start apps list, which reports the
/// application id (for example NVIDIACorp.NVIDIAControlPanel_...!App or com.nvidia.nvapp).
///
/// The cheap checks run first; the Start apps list costs a PowerShell launch, so it is only asked
/// when nothing on disk answered. No NVIDIA software at all reports none, and the callers hide the
/// entry rather than offering a button that does nothing.
/// </summary>
public static class NvidiaTools
{
    /// <summary>
    /// What was found. <paramref name="ShellApp"/> means <paramref name="Path"/> is an application
    /// id to be opened through <c>shell:AppsFolder</c> rather than a file to start.
    /// </summary>
    public readonly record struct Target(string Label, string Path, bool ShellApp);

    private static readonly (string Label, string Relative)[] InstallCandidates =
    [
        ("NVIDIA App", @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
        ("NVIDIA Control Panel", @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
        ("NVIDIA Control Panel", @"nvcplui.exe")
    ];

    private static readonly object Gate = new();
    private static Target? _target;

    /// <summary>The vendor tool on this machine, or null when there is none. Searched once.</summary>
    public static Target? Find()
    {
        lock (Gate)
        {
            // Only a hit is cached. A miss has to be retried: the tray starts at logon, when the
            // Start apps list can still be empty, and caching that would leave the entry missing for
            // the whole session.
            if (_target is { } cached) return cached;

            _target = SearchInstall() ?? SearchVendorFolder() ?? SearchStartMenu() ?? SearchStartApps();

            if (_target is { } found)
                Logger.Info($"NVIDIA: using {found.Label} ({found.Path}).");
            else
                Logger.Info("NVIDIA: no NVIDIA App or Control Panel found; will retry on the next request.");

            return _target;
        }
    }

    /// <summary>Starts the vendor tool. False when nothing was found or the launch failed.</summary>
    public static bool Open()
    {
        if (Find() is not { } target)
        {
            Logger.Warn("NVIDIA: no NVIDIA App or Control Panel was found to open.");
            return false;
        }

        try
        {
            var start = target.ShellApp
                ? new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{target.Path}") { UseShellExecute = true }
                : new ProcessStartInfo(target.Path) { UseShellExecute = true };

            Process.Start(start);
            Logger.Info($"NVIDIA: opened {target.Label}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NVIDIA: could not open {target.Label}.", ex);
            return false;
        }
    }

    private static Target? SearchInstall()
    {
        foreach (var (label, path) in CandidatePaths())
        {
            if (File.Exists(path))
                return new Target(label, path, ShellApp: false);
        }

        return null;
    }

    /// <summary>
    /// The vendor folder, searched for the two exes by name. Covers installs whose subfolder is not
    /// the one the installer is documented to use.
    /// </summary>
    private static Target? SearchVendorFolder()
    {
        foreach (var root in ProgramRoots())
        {
            var vendor = Path.Combine(root, "NVIDIA Corporation");
            if (!Directory.Exists(vendor)) continue;

            try
            {
                var executables = Directory.EnumerateFiles(vendor, "*.exe", SearchOption.AllDirectories)
                    .Where(path =>
                    {
                        var name = Path.GetFileName(path);
                        return name.Equals("nvcplui.exe", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("NVIDIA App.exe", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                var controlPanel = executables.FirstOrDefault(path =>
                    Path.GetFileName(path).Equals("nvcplui.exe", StringComparison.OrdinalIgnoreCase));
                if (controlPanel != null)
                    return new Target("NVIDIA Control Panel", controlPanel, ShellApp: false);

                if (executables.FirstOrDefault() is { } app)
                    return new Target("NVIDIA App", app, ShellApp: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn("NVIDIA: could not search the vendor folder.", ex);
            }
        }

        return null;
    }

    /// <summary>Shortcut files, which is where a desktop install usually surfaces.</summary>
    private static Target? SearchStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        var shortcuts = roots
            .Where(root => !string.IsNullOrEmpty(root))
            .SelectMany(EnumerateShortcuts)
            .ToList();

        foreach (var (label, needle) in new[]
                 {
                     ("NVIDIA Control Panel", "nvidia control panel"),
                     ("NVIDIA App", "nvidia app")
                 })
        {
            var match = shortcuts.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                return new Target(label, match, ShellApp: false);
        }

        return null;
    }

    /// <summary>
    /// The Start apps list, the only place a packaged app appears. <c>Get-StartApps</c> returns the
    /// display name and the application id, which is exactly what <c>shell:AppsFolder</c> wants.
    ///
    /// The script is passed as -EncodedCommand rather than -Command: quoting a filter through
    /// ProcessStartInfo is how this kind of call ends up running something else entirely.
    /// </summary>
    private static Target? SearchStartApps()
    {
        const string script =
            "Get-StartApps | Where-Object { $_.Name -like '*NVIDIA*' } | " +
            "ForEach-Object { '{0}|{1}' -f $_.Name, $_.AppID }";

        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Logger.Warn("NVIDIA: the Start apps query timed out.");
                return null;
            }

            var apps = new List<(string Name, string Id)>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.LastIndexOf('|');
                if (separator <= 0 || separator == line.Length - 1) continue;
                apps.Add((line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }

            foreach (var (label, needle) in new[]
                     {
                         ("NVIDIA Control Panel", "nvidia control panel"),
                         ("NVIDIA App", "nvidia app")
                     })
            {
                var match = apps.FirstOrDefault(app => app.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
                if (match.Id.Length > 0)
                    return new Target(label, match.Id, ShellApp: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception)
        {
            Logger.Warn("NVIDIA: could not query the Start apps list.", ex);
        }

        return null;
    }

    private static IEnumerable<(string Label, string Path)> CandidatePaths()
    {
        foreach (var root in ProgramRoots())
        {
            foreach (var (label, relative) in InstallCandidates)
                yield return (label, Path.Combine(root, relative));
        }
    }

    private static IEnumerable<string> ProgramRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        return roots.Where(root => !string.IsNullOrEmpty(root));
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("NVIDIA: could not read the Start Menu.", ex);
            return Array.Empty<string>();
        }
    }
}
