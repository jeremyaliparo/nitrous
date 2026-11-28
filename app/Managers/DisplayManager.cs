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

    // --- the panel's own label: adapter, rate and signal depth ---------------------------

    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
    private const uint DisplayDevicePrimaryDevice = 0x00000004;
    private const uint QdcOnlyActivePaths = 2;
    private const int DeviceInfoGetSourceName = 1;
    private const int DeviceInfoGetAdvancedColorInfo = 9;
    private const uint OutputTechnologyInternal = 0x80000000;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>DISPLAYCONFIG_MODE_INFO is 64 bytes; it is never read here, only sized.</summary>
    private const int ModeInfoSize = 64;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        /// <summary>The GDI name of the adapter driving that path, e.g. "\\\\.\\DISPLAY1".</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;

        /// <summary>Bitfield: bit 0 supported, bit 1 enabled, bit 2 wide colour forced.</summary>
        public uint Value;

        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] PathInfo[] pathArray, ref uint numModeInfoArrayElements, IntPtr modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetSourceName(ref SourceDeviceName requestPacket);

    /// <summary>
    /// The attached adapter's friendly name and GDI name, primary display first. The two travel together
    /// so the colour depth can be read from the path of the display the name describes.
    /// </summary>
    private static (string? Name, string? Gdi) PrimaryAdapter()
    {
        try
        {
            (string? Name, string? Gdi)? attached = null;
            for (uint i = 0; ; i++)
            {
                var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
                if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

                var found = (device.DeviceString, device.DeviceName);
                if ((device.StateFlags & DisplayDevicePrimaryDevice) != 0) return found;
                attached ??= found;
            }

            return attached ?? (null, null);
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not read the adapter name.", ex);
            return (null, null);
        }
    }

    /// <summary>
    /// The display adapter Windows attached to the desktop - on a laptop, the one driving the built-in
    /// panel, e.g. "AMD Radeon 860M". Null when the display device cannot be enumerated.
    /// </summary>
    public static string? GetAdapterName()
    {
        var (name, _) = PrimaryAdapter();
        var cleaned = CleanAdapterName(name);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string CleanAdapterName(string? name) =>
        // The driver's own name carries trademark marks that only make the line longer.
        name?
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim() ?? "";

    /// <summary>
    /// One display Windows is driving: its GDI adapter name, the mode it is in now, and the rates it
    /// offers at that resolution. The name is what <c>ChangeDisplaySettingsExW</c> takes, so a rate
    /// can be applied to this display alone.
    /// </summary>
    public sealed record DisplayTarget(
        string GdiName,
        string Adapter,
        bool IsPrimary,
        int Width,
        int Height,
        int CurrentHz,
        IReadOnlyList<int> Rates);

    /// <summary>Attached displays, primary first. Empty when the desktop cannot be enumerated.</summary>
    public static IReadOnlyList<DisplayTarget> GetDisplays()
    {
        var displays = new List<DisplayTarget>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
                if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

                var current = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
                if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current)) continue;

                displays.Add(new DisplayTarget(
                    device.DeviceName,
                    CleanAdapterName(device.DeviceString),
                    (device.StateFlags & DisplayDevicePrimaryDevice) != 0,
                    current.dmPelsWidth,
                    current.dmPelsHeight,
                    current.dmDisplayFrequency,
                    SupportedRates(device.DeviceName, current.dmPelsWidth, current.dmPelsHeight)));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not enumerate the attached displays.", ex);
        }

        return displays.OrderByDescending(d => d.IsPrimary).ToList();
    }

    /// <summary>
    /// Bits per colour channel the display is running at - 8, 10 or 12 - from Windows' own advanced-colour
    /// state for that display's path. 0 when the driver does not report it.
    /// </summary>
    public static int GetBitsPerColorChannel()
    {
        var (_, gdi) = PrimaryAdapter();

        try
        {
            // The topology can change between the size query and the read (a monitor plugged in, a mode
            // change); the second attempt starts from fresh sizes.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 ||
                    pathCount == 0)
                {
                    return 0;
                }

                var paths = new PathInfo[pathCount];
                var modes = modeCount > 0 ? Marshal.AllocHGlobal((int)modeCount * ModeInfoSize) : IntPtr.Zero;
                try
                {
                    var result = QueryDisplayConfig(
                        QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

                    if (result == ErrorInsufficientBuffer) continue;
                    if (result != 0) return 0;

                    return ReadBitsPerColorChannel(paths, pathCount, gdi);
                }
                finally
                {
                    if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not read the colour depth.", ex);
            return 0;
        }
    }

    /// <summary>
    /// The depth of the display Windows is using: the path whose source is the named adapter, else the
    /// built-in panel, else the first path that reports one.
    /// </summary>
    private static int ReadBitsPerColorChannel(PathInfo[] paths, uint pathCount, string? primaryGdi)
    {
        var primary = -1;
        var internalPanel = -1;
        var first = 0;

        for (var i = 0; i < pathCount && i < paths.Length; i++)
        {
            var target = paths[i].TargetInfo;
            if (target.TargetAvailable == 0) continue;

            var request = new AdvancedColorInfo
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetAdvancedColorInfo,
                    Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                    AdapterId = target.AdapterId,
                    Id = target.Id
                }
            };

            if (DisplayConfigGetDeviceInfo(ref request) != 0) continue;
            if (request.BitsPerColorChannel == 0) continue;

            if (primaryGdi is not null && SourceMatches(paths[i].SourceInfo, primaryGdi))
                primary = (int)request.BitsPerColorChannel;

            if (target.OutputTechnology == OutputTechnologyInternal && internalPanel == 0)
                internalPanel = (int)request.BitsPerColorChannel;

            if (first == 0) first = (int)request.BitsPerColorChannel;
        }

        if (primary > 0) return primary;
        if (internalPanel > 0) return internalPanel;
        return first;
    }

    /// <summary>Whether a display path's source is the adapter with this GDI name.</summary>
    private static bool SourceMatches(PathSourceInfo source, string gdiName)
    {
        try
        {
            var request = new SourceDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    Type = DeviceInfoGetSourceName,
                    Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                    AdapterId = source.AdapterId,
                    Id = source.Id
                }
            };

            return DisplayConfigGetSourceName(ref request) == 0 &&
                   string.Equals(request.ViewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>The refresh rate Windows is actually running right now on that display.</summary>
    public static int GetCurrentRefreshRate(string? gdiDeviceName = null)
    {
        try
        {
            var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            return EnumDisplaySettingsW(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref mode)
                ? mode.dmDisplayFrequency
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Distinct refresh rates a display advertises at its current resolution.</summary>
    public static IReadOnlyList<int> GetSupportedRefreshRates(string? gdiDeviceName = null)
    {
        try
        {
            var current = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref current))
                return Array.Empty<int>();

            return SupportedRates(gdiDeviceName, current.dmPelsWidth, current.dmPelsHeight);
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not enumerate refresh rates.", ex);
            return Array.Empty<int>();
        }
    }

    private static IReadOnlyList<int> SupportedRates(string? gdiDeviceName, int width, int height)
    {
        var rates = new SortedSet<int>();
        var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
        for (var i = 0; i < 4096 && EnumDisplaySettingsW(gdiDeviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width &&
                mode.dmPelsHeight == height &&
                mode.dmDisplayFrequency > 0)
            {
                rates.Add(mode.dmDisplayFrequency);
            }
        }

        return rates.ToArray();
    }

    /// <summary>The rate the user picked for that display; 0 when nothing has been picked yet.</summary>
    public static int GetChosenRefreshRate(string? gdiDeviceName = null)
    {
        Migrate();

        var gdi = ResolveGdi(gdiDeviceName);
        var saved = SettingsManager.Get(DisplayKey("RefreshAcHz", gdi), -1);

        // A display this build has never saved inherits the single-display setting of an older
        // install, so a refresh choice made before per-display targets keeps working.
        return saved >= 0 ? saved : SettingsManager.Get("RefreshAcHz", 0);
    }

    public static RefreshMode GetMode(string? gdiDeviceName = null)
    {
        Migrate();

        var gdi = ResolveGdi(gdiDeviceName);
        var saved = SettingsManager.Get(DisplayKey("RefreshMode", gdi), -1);

        return saved >= 0
            ? (RefreshMode)saved
            : (RefreshMode)SettingsManager.Get("RefreshMode", (int)RefreshMode.Auto);
    }

    /// <summary>Picks a fixed rate for one display; applied on AC and on battery.</summary>
    public static void SetFixedRate(int hz, string? gdiDeviceName = null)
    {
        var gdi = ResolveGdi(gdiDeviceName);
        SettingsManager.Save(DisplayKey("RefreshAcHz", gdi), hz);
        SettingsManager.Save(DisplayKey("RefreshMode", gdi), (int)RefreshMode.Fixed);
        ApplyDisplay(gdi, IsOnAc());
    }

    /// <summary>Auto for one display: the chosen rate on AC, 60 Hz on battery.</summary>
    public static void SetAuto(string? gdiDeviceName = null)
    {
        var gdi = ResolveGdi(gdiDeviceName);
        SettingsManager.Save(DisplayKey("RefreshMode", gdi), (int)RefreshMode.Auto);
        ApplyDisplay(gdi, IsOnAc());
    }

    /// <summary>
    /// Applies the saved profile to every display the user has configured. A display this build
    /// has no choice for keeps Windows' own mode: attaching a monitor must not change it.
    /// </summary>
    public static void Apply(bool isAcPower)
    {
        Migrate();

        foreach (var display in GetDisplays())
        {
            var configured = SettingsManager.Get(DisplayKey("RefreshMode", display.GdiName), -1) >= 0;
            if (!configured && !display.IsPrimary) continue;

            ApplyFor(display, isAcPower);
        }
    }

    /// <summary>
    /// The old setting was Auto/60/Max. Convert it once, so an existing install keeps its choice.
    /// </summary>
    private static void Migrate()
    {
        if (SettingsManager.Get("RefreshAcHz", -1) >= 0) return;

        var legacy = SettingsManager.Get("RefreshMode", (int)RefreshMode.Auto);
        var rate = legacy switch
        {
            1 => 60,
            2 => GetPrimaryMaxRefreshRate(),
            _ => GetPrimaryMaxRefreshRate()
        };

        SettingsManager.Save("RefreshAcHz", rate);
        SettingsManager.Save("RefreshMode", legacy == 0 ? (int)RefreshMode.Auto : (int)RefreshMode.Fixed);
    }

    private static bool IsOnAc() =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
        System.Windows.Forms.PowerLineStatus.Online;

    /// <summary>The GDI name behind a null/empty hint, so the primary display is the default.</summary>
    private static string ResolveGdi(string? gdiDeviceName)
    {
        if (!string.IsNullOrWhiteSpace(gdiDeviceName)) return gdiDeviceName;

        var displays = GetDisplays();
        return (displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault())?.GdiName ?? "";
    }

    /// <summary>
    /// One registry value per display. The GDI name ("\\.\DISPLAY1") is reduced to letters and
    /// digits so the value name stays a plain registry name.
    /// </summary>
    private static string DisplayKey(string baseKey, string gdiDeviceName) =>
        $"{baseKey}_{string.Concat(gdiDeviceName.Where(char.IsLetterOrDigit))}";

    private static void ApplyDisplay(string gdiDeviceName, bool isAcPower)
    {
        var display = GetDisplays().FirstOrDefault(d =>
            string.Equals(d.GdiName, gdiDeviceName, StringComparison.OrdinalIgnoreCase));

        if (display is null)
        {
            Logger.Warn($"Display: '{gdiDeviceName}' is no longer attached; nothing to apply.");
            return;
        }

        ApplyFor(display, isAcPower);
    }

    private static void ApplyFor(DisplayTarget display, bool isAcPower)
    {
        var mode = GetMode(display.GdiName);
        var chosen = GetChosenRefreshRate(display.GdiName);
        var max = display.Rates.Count > 0 ? display.Rates.Max() : display.CurrentHz;

        var targetHz = mode == RefreshMode.Auto && !isAcPower
            ? 60
            : chosen > 0 ? chosen : max;

        ApplyRefreshRate(display, targetHz, mode);
    }

    private static void ApplyRefreshRate(DisplayTarget display, int targetHz, RefreshMode mode)
    {
        try
        {
            Logger.Info($"Display: {display.GdiName} {mode} -> target {targetHz} Hz " +
                        $"(current {display.CurrentHz} Hz at {display.Width}x{display.Height}).");

            if (display.CurrentHz == targetHz)
            {
                Logger.Info($"Display: {display.GdiName} already at {targetHz} Hz; nothing to change.");
                return;
            }

            // Apply the driver's own entry for this resolution and refresh instead of mutating the
            // current mode. Asking for a mode the driver advertised cannot be "bad mode".
            if (!TryFindMode(display.GdiName, display.Width, display.Height, targetHz, out var target))
            {
                Logger.Warn($"Display: {targetHz} Hz is not supported on {display.GdiName} at " +
                            $"{display.Width}x{display.Height}; leaving the current mode unchanged.");
                return;
            }

            var result = ChangeDisplaySettingsExW(display.GdiName, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result != DispChangeSuccessful)
            {
                Logger.Warn($"Display: ChangeDisplaySettingsExW on {display.GdiName} returned " +
                            $"{DescribeResult(result)} while switching to {targetHz} Hz.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Display: refresh-profile change failed on {display.GdiName}.", ex);
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
            result["adapter"] = GetAdapterName();
            result["bitsPerColorChannel"] = GetBitsPerColorChannel();
            result["supportedRefreshHz"] = supported.ToArray();
            result["maxRefreshHz"] = supported.Count > 0 ? supported.Max : current.dmDisplayFrequency;
            result["displays"] = GetDisplays().Select(d => new Dictionary<string, object?>
            {
                ["gdiName"] = d.GdiName,
                ["adapter"] = d.Adapter,
                ["primary"] = d.IsPrimary,
                ["width"] = d.Width,
                ["height"] = d.Height,
                ["refreshHz"] = d.CurrentHz,
                ["mode"] = GetMode(d.GdiName).ToString(),
                ["chosenHz"] = GetChosenRefreshRate(d.GdiName),
                ["supportedRefreshHz"] = d.Rates
            }).ToArray();
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
    private static bool TryFindMode(string gdiDeviceName, int width, int height, int targetHz, out DEVMODEW mode)
    {
        mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
        var candidate = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };

        for (var i = 0; EnumDisplaySettingsW(gdiDeviceName, i, ref candidate); i++)
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
