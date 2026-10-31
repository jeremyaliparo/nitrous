using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Managers;

public static class DisplayManager
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_DISPLAYFREQUENCY = 0x00400000;
    public const int CDS_UPDATEREGISTRY = 0x00000001;

    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;
    private const int DispChangeFailed = -1;
    private const int DispChangeBadMode = -2;
    private const int DispChangeNotUpdated = -3;
    private const int DispChangeBadFlags = -4;
    private const int DispChangeBadParam = -5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODEW lpDevMode, IntPtr hwnd, uint dwFlags, IntPtr lParam);

    public static int GetPrimaryMaxRefreshRate()
    {
        var maxHz = 60;
        try
        {
            var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode)) return maxHz;

            var currentWidth = mode.dmPelsWidth;
            var currentHeight = mode.dmPelsHeight;

            var testMode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            for (var i = 0; EnumDisplaySettingsW(null, i, ref testMode); i++)
            {
                if (testMode.dmPelsWidth == currentWidth &&
                    testMode.dmPelsHeight == currentHeight &&
                    testMode.dmDisplayFrequency > maxHz)
                {
                    maxHz = testMode.dmDisplayFrequency;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not enumerate refresh rates.", ex);
        }

        return maxHz;
    }

    /// <summary>The refresh rate Windows is actually running right now.</summary>
    public static int GetCurrentRefreshRate()
    {
        try
        {
            var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode)
                ? mode.dmDisplayFrequency
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void ApplyRefreshProfile(RefreshProfile profile, bool isAcPower)
    {
        try
        {
            var maxHz = GetPrimaryMaxRefreshRate();
            var targetHz = profile switch
            {
                RefreshProfile.Hz60 => 60,
                RefreshProfile.MaxHz => maxHz,
                RefreshProfile.Auto => isAcPower ? maxHz : 60,
                _ => 60
            };

            var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref mode))
            {
                Logger.Warn("Display: could not read the current display mode.");
                return;
            }

            Logger.Info($"Display: profile {profile} -> target {targetHz} Hz " +
                        $"(current {mode.dmDisplayFrequency} Hz at {mode.dmPelsWidth}x{mode.dmPelsHeight}, max {maxHz} Hz).");

            if (mode.dmDisplayFrequency == targetHz)
            {
                Logger.Info($"Display: already at {targetHz} Hz; nothing to change.");
                return;
            }

            // Apply the driver's own entry for this resolution and refresh instead of mutating the
            // current mode. Asking for a mode the driver advertised cannot be "bad mode", which is
            // what used to happen on panels that do not expose the target at every colour format.
            if (!TryFindMode(mode.dmPelsWidth, mode.dmPelsHeight, targetHz, out var target))
            {
                Logger.Warn($"Display: {targetHz} Hz is not supported at " +
                            $"{mode.dmPelsWidth}x{mode.dmPelsHeight}; leaving the current mode unchanged.");
                return;
            }

            var result = ChangeDisplaySettingsExW(null, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result != DispChangeSuccessful)
            {
                Logger.Warn($"Display: ChangeDisplaySettingsExW returned {DescribeResult(result)} " +
                            $"while switching to {targetHz} Hz.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: refresh-profile change failed.", ex);
        }
    }

    public static Dictionary<string, object?> GetDiagnosticsSnapshot()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var current = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current))
            {
                result["error"] = "EnumDisplaySettingsW could not read the current display mode.";
                return result;
            }

            var supported = new SortedSet<int>();
            var test = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            for (var i = 0; i < 1024 && EnumDisplaySettingsW(null, i, ref test); i++)
            {
                if (test.dmPelsWidth == current.dmPelsWidth &&
                    test.dmPelsHeight == current.dmPelsHeight &&
                    test.dmDisplayFrequency > 0)
                {
                    supported.Add(test.dmDisplayFrequency);
                }
            }

            result["current"] = new Dictionary<string, object?>
            {
                ["width"] = current.dmPelsWidth,
                ["height"] = current.dmPelsHeight,
                ["refreshHz"] = current.dmDisplayFrequency,
                ["bitsPerPixel"] = current.dmBitsPerPel
            };
            result["supportedRefreshHz"] = supported.ToArray();
            result["maxRefreshHz"] = supported.Count > 0 ? supported.Max : current.dmDisplayFrequency;
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: diagnostics snapshot failed.", ex);
            result["error"] = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// The driver's own mode entry for a resolution and refresh, so it can be applied whole.
    /// </summary>
    private static bool TryFindMode(int width, int height, int targetHz, out DEVMODEW mode)
    {
        mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
        var candidate = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };

        for (var i = 0; EnumDisplaySettingsW(null, i, ref candidate); i++)
        {
            if (candidate.dmPelsWidth == width &&
                candidate.dmPelsHeight == height &&
                candidate.dmDisplayFrequency == targetHz)
            {
                mode = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsRefreshSupported(int width, int height, int targetHz)
    {
        var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
        for (var i = 0; EnumDisplaySettingsW(null, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width &&
                mode.dmPelsHeight == height &&
                mode.dmDisplayFrequency == targetHz)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeResult(int result) => result switch
    {
        DispChangeSuccessful => "success",
        DispChangeRestart => "restart required",
        DispChangeFailed => "failed",
        DispChangeBadMode => "bad mode",
        DispChangeNotUpdated => "not updated",
        DispChangeBadFlags => "bad flags",
        DispChangeBadParam => "bad parameter",
        _ => $"unknown ({result})"
    };
}
