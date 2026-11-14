using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Opens the vendor GPU software Nitrous deliberately does not replace: the NVIDIA App (driver
/// settings, DLSS overrides, per-app profiles) or the older NVIDIA Control Panel.
///
/// Nothing about the install is assumed. The App's fixed path is tried first because that is where
/// its installer writes, then the Start Menu shortcuts - which also cover the packaged/Store build of
/// the Control Panel - then nvcplui.exe for older installs. A machine with no NVIDIA software reports
/// none, and the caller hides the entry rather than offering a button that does nothing.
/// </summary>
public static class NvidiaTools
{
    /// <summary>What was found: a display label and the file shell-executed to open it.</summary>
    public readonly record struct Target(string Label, string Path);

    private static readonly (string Label, string Relative)[] InstallCandidates =
    [
        ("NVIDIA App", @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
        ("NVIDIA Control Panel", @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
        (@"NVIDIA Control Panel", @"nvcplui.exe")
    ];

    private static Target? _target;
    private static bool _searched;

    /// <summary>The vendor tool on this machine, or null when there is none. Searched once.</summary>
    public static Target? Find()
    {
        if (_searched) return _target;

        _searched = true;
        _target = SearchInstall() ?? SearchStartMenu();
        return _target;
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
            Process.Start(new ProcessStartInfo(target.Path) { UseShellExecute = true });
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
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(root => !string.IsNullOrEmpty(root)))
        {
            foreach (var (label, relative) in InstallCandidates)
            {
                var path = Path.Combine(root, relative);
                if (File.Exists(path))
                    return new Target(label, path);
            }
        }

        return null;
    }

    /// <summary>
    /// Shortcuts are the only reliable handle on packaged installs, whose exe lives under WindowsApps
    /// behind a package identity. The App is preferred over the Control Panel when both are present.
    /// </summary>
    private static Target? SearchStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        var shortcuts = roots
            .Where(root => !string.IsNullOrEmpty(root))
            .SelectMany(root => EnumerateShortcuts(root))
            .ToList();

        foreach (var (label, needle) in new[] { ("NVIDIA App", "nvidia app"), ("NVIDIA Control Panel", "nvidia control panel") })
        {
            var match = shortcuts.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                return new Target(label, match);
        }

        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateShortcuts(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Logger.Warn("NVIDIA: could not read the Start Menu.", ex);
            return Array.Empty<string>();
        }
    }
}
