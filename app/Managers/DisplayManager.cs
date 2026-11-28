using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
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
    private const int DeviceInfoGetTargetName = 2;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        /// <summary>The monitor's own name from its EDID, e.g. "27G64" or "NE160QDM-NYL".</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;

        /// <summary>The monitor's device interface path, which leads to its EDID in the registry.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
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

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetTargetName(ref TargetDeviceName requestPacket);

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
    /// One display Windows is driving: its GDI adapter name, the monitor's own EDID name, the mode it
    /// is in now, and the rates it offers at that resolution. The GDI name is what
    /// <c>ChangeDisplaySettingsExW</c> takes, so a rate can be applied to this display alone.
    /// </summary>
    public sealed record DisplayTarget(
        string GdiName,
        string Adapter,
        string Monitor,
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
            var monitors = ActivePathMonitors();

            for (uint i = 0; ; i++)
            {
                var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
                if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

                var current = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
                if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current)) continue;

                monitors.TryGetValue(device.DeviceName, out var monitor);
                var driverRates = SupportedRates(device.DeviceName, current.dmPelsWidth, current.dmPelsHeight);
                var declared = monitor?.Edid is { } edid
                    ? DeclaredTimings(edid)
                    : null;

                displays.Add(new DisplayTarget(
                    device.DeviceName,
                    CleanAdapterName(device.DeviceString),
                    monitor?.Name ?? "",
                    (device.StateFlags & DisplayDevicePrimaryDevice) != 0,
                    current.dmPelsWidth,
                    current.dmPelsHeight,
                    current.dmDisplayFrequency,
                    FilterRates(driverRates, declared, current.dmDisplayFrequency,
                        SeenRates(device.DeviceName))));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not enumerate the attached displays.", ex);
        }

        return displays.OrderByDescending(d => d.IsPrimary).ToList();
    }

    // --- the monitor behind each path: its EDID name and its declared timings ----------------------

    private sealed record PathMonitor(string Name, byte[]? Edid);

    private static readonly Dictionary<string, byte[]?> EdidCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EdidGate = new();

    /// <summary>
    /// The monitor name and EDID for every active path, keyed by the GDI adapter name. Windows
    /// reads both from the monitor's device path; the EDID is what decides which refresh rates the
    /// monitor actually declares, so the list can match the OS instead of the driver's full set.
    /// </summary>
    private static Dictionary<string, PathMonitor> ActivePathMonitors()
    {
        var monitors = new Dictionary<string, PathMonitor>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 ||
                    pathCount == 0)
                {
                    return monitors;
                }

                var paths = new PathInfo[pathCount];
                var modes = modeCount > 0 ? Marshal.AllocHGlobal((int)modeCount * ModeInfoSize) : IntPtr.Zero;
                try
                {
                    var result = QueryDisplayConfig(
                        QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

                    if (result == ErrorInsufficientBuffer) continue;
                    if (result != 0) return monitors;

                    for (var i = 0; i < pathCount && i < paths.Length; i++)
                    {
                        if (paths[i].TargetInfo.TargetAvailable == 0) continue;

                        var gdi = ReadSourceName(paths[i].SourceInfo);
                        if (string.IsNullOrEmpty(gdi)) continue;

                        var (name, devicePath) = ReadTargetName(paths[i].TargetInfo);
                        monitors[gdi] = new PathMonitor(name, ReadEdid(devicePath));
                    }

                    return monitors;
                }
                finally
                {
                    if (modes != IntPtr.Zero) Marshal.FreeHGlobal(modes);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not read the monitor names.", ex);
        }

        return monitors;
    }

    private static (string Name, string DevicePath) ReadTargetName(PathTargetInfo target)
    {
        var request = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DeviceInfoGetTargetName,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = target.AdapterId,
                Id = target.Id
            }
        };

        return DisplayConfigGetTargetName(ref request) == 0
            ? (request.MonitorFriendlyDeviceName ?? "", request.MonitorDevicePath ?? "")
            : ("", "");
    }

    /// <summary>
    /// A monitor's EDID, from its device interface path
    /// (<c>\\?\DISPLAY#BOE0CED#5&amp;...&amp;UID512#{interface}</c>). The bytes never change for a
    /// given path, so they are cached.
    /// </summary>
    private static byte[]? ReadEdid(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;

        lock (EdidGate)
        {
            if (EdidCache.TryGetValue(devicePath, out var cached)) return cached;
        }

        byte[]? edid = null;
        try
        {
            var path = devicePath;
            var lastHash = path.LastIndexOf('#');
            if (lastHash > 0) path = path[..lastHash];

            path = path.Replace('#', '\\');
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + path + @"\Device Parameters");
            edid = key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not read the monitor EDID.", ex);
        }

        lock (EdidGate)
        {
            EdidCache[devicePath] = edid;
        }

        return edid;
    }

    /// <summary>
    /// The refresh rates a monitor declares in its EDID: base-block detailed timings, CTA detailed
    /// timings and the DisplayID Type I/Type VII timings laptop panels use for their fast modes.
    /// </summary>
    private static IReadOnlyList<(int Width, int Height, double Hz)> DeclaredTimings(byte[] edid)
    {
        var timings = new List<(int Width, int Height, double Hz)>();

        try
        {
            if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
                return timings;

            for (var i = 0; i < 4; i++)
                AddDetailedTiming(edid, 54 + (i * 18), timings);

            for (var block = 1; block * 128 < edid.Length; block++)
            {
                var offset = block * 128;
                if (offset + 128 > edid.Length) break;

                if (edid[offset] == 0x02)
                {
                    // CTA-861: byte 2 is the offset to the detailed timings.
                    var start = offset + edid[offset + 2];
                    for (var p = start; p + 18 <= offset + 127; p += 18)
                        if (!AddDetailedTiming(edid, p, timings)) break;
                }
                else if (edid[offset] == 0x70)
                {
                    AddDisplayIdTimings(edid, offset, timings);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Display: could not parse the monitor EDID.", ex);
        }

        return timings;
    }

    /// <summary>Reads one 18-byte EDID detailed timing; false when the descriptor is not a timing.</summary>
    private static bool AddDetailedTiming(byte[] edid, int p, List<(int Width, int Height, double Hz)> timings)
    {
        if (p + 18 > edid.Length) return false;

        var clock = edid[p] | (edid[p + 1] << 8);
        if (clock == 0) return false;

        var width = edid[p + 2] | ((edid[p + 4] & 0xF0) << 4);
        var height = edid[p + 5] | ((edid[p + 7] & 0xF0) << 4);
        var hTotal = width + (edid[p + 3] | ((edid[p + 4] & 0x0F) << 8));
        var vTotal = height + (edid[p + 6] | ((edid[p + 7] & 0x0F) << 8));

        if (hTotal <= 0 || vTotal <= 0) return false;

        var interlaced = (edid[p + 17] & 0x80) != 0;
        var hz = clock * 10000.0 / (hTotal * (double)vTotal) * (interlaced ? 2 : 1);
        timings.Add((width, height, hz));
        return true;
    }

    /// <summary>
    /// DisplayID data blocks (EDID extension tag 0x70). Version 1.x declares Type I timings under
    /// tag 0x03 in 10 kHz units; version 2.0 declares Type VII timings under tag 0x22 in 1 kHz.
    /// </summary>
    private static void AddDisplayIdTimings(byte[] edid, int offset, List<(int Width, int Height, double Hz)> timings)
    {
        var version = edid[offset + 1];
        var length = Math.Min((int)edid[offset + 2], 121);
        var end = offset + 5 + length;

        for (var p = offset + 5; p + 3 <= end;)
        {
            var tag = edid[p];
            var size = edid[p + 2];
            if (tag == 0 && size == 0) break;
            if (p + 3 + size > end) break;

            var type1 = version < 0x20 && tag == 0x03;
            var type7 = version >= 0x20 && tag == 0x22;

            if (type1 || type7)
            {
                for (var t = 0; t + 20 <= size; t += 20)
                    AddDisplayIdTiming(edid, p + 3 + t, type7, timings);
            }

            p += 3 + size;
        }
    }

    private static void AddDisplayIdTiming(byte[] edid, int p, bool type7,
        List<(int Width, int Height, double Hz)> timings)
    {
        if (p + 20 > edid.Length) return;

        var clockKhz = (type7 ? 1 : 10) * (1 + (edid[p] + (edid[p + 1] << 8) + (edid[p + 2] << 16)));
        var width = 1 + (edid[p + 4] | (edid[p + 5] << 8));
        var hTotal = width + 1 + (edid[p + 6] | (edid[p + 7] << 8));
        var height = 1 + (edid[p + 12] | (edid[p + 13] << 8));
        var vTotal = height + 1 + (edid[p + 14] | (edid[p + 15] << 8));

        if (hTotal <= 0 || vTotal <= 0 || clockKhz <= 0) return;

        var interlaced = (edid[p + 3] & 0x10) != 0;
        var hz = clockKhz * 1000.0 / (hTotal * (double)vTotal) * (interlaced ? 2 : 1);
        timings.Add((width, height, hz));
    }

    /// <summary>
    /// Keeps the driver rates the monitor's EDID declares at this resolution, plus the rate it is
    /// running now, so the list matches Windows Settings instead of offering every mode the driver
    /// can synthesize. A resolution the EDID does not describe keeps the full driver list, because
    /// there is nothing to filter against and the scaled modes are legitimate there.
    /// </summary>
    private static IReadOnlyList<int> FilterRates(
        IReadOnlyList<int> driverRates,
        IReadOnlyList<(int Width, int Height, double Hz)>? declared,
        int currentHz,
        IReadOnlyList<int> seen)
    {
        if (declared is null || declared.Count == 0 || driverRates.Count < 2)
            return driverRates;

        var keep = new SortedSet<int>();
        foreach (var hz in driverRates)
        {
            if (declared.Any(t => Math.Abs(t.Hz - hz) <= 0.75))
                keep.Add(hz);
        }

        // One declared rate is a scaled/non-native resolution, not the monitor's discrete mode set.
        if (keep.Count < 2) return driverRates;

        // Rates this display has actually run at stay offered: 120 Hz on the BOE panel is not in
        // its EDID but the panel runs it, and dropping it after switching to 180 would strand the
        // user with no way back.
        foreach (var hz in seen)
        {
            if (driverRates.Contains(hz)) keep.Add(hz);
        }

        if (currentHz > 0 && driverRates.Contains(currentHz))
            keep.Add(currentHz);

        return keep.ToArray();
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
    private static bool SourceMatches(PathSourceInfo source, string gdiName) =>
        string.Equals(ReadSourceName(source), gdiName, StringComparison.OrdinalIgnoreCase);

    private static string? ReadSourceName(PathSourceInfo source)
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

            return DisplayConfigGetSourceName(ref request) == 0 ? request.ViewGdiDeviceName : null;
        }
        catch
        {
            return null;
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

    /// <summary>Distinct refresh rates a display offers at its current resolution.</summary>
    public static IReadOnlyList<int> GetSupportedRefreshRates(string? gdiDeviceName = null)
    {
        var displays = GetDisplays();
        var display = displays.FirstOrDefault(d =>
                          string.Equals(d.GdiName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                      ?? displays.FirstOrDefault();

        return display?.Rates ?? Array.Empty<int>();
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

        // Remember what this display has run at, so a rate the EDID does not list stays reachable.
        var current = GetCurrentRefreshRate(gdi);
        var seen = SeenRates(gdi)
            .Append(current)
            .Append(hz)
            .Where(h => h > 0)
            .Distinct()
            .OrderBy(h => h)
            .TakeLast(12);
        SettingsManager.Save(DisplayKey("RefreshSeen", gdi), string.Join(',', seen));

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

        var (_, gdi) = PrimaryAdapter();
        return gdi ?? "";
    }

    /// <summary>
    /// One registry value per display. The GDI name ("\\.\DISPLAY1") is reduced to letters and
    /// digits so the value name stays a plain registry name.
    /// </summary>
    private static string IdFor(string gdiDeviceName) =>
        string.Concat(gdiDeviceName.Where(char.IsLetterOrDigit));

    private static string DisplayKey(string baseKey, string gdiDeviceName) =>
        $"{baseKey}_{IdFor(gdiDeviceName)}";

    /// <summary>The rates this display has been switched to, newest last; empty before any choice.</summary>
    private static IReadOnlyList<int> SeenRates(string gdiDeviceName)
    {
        var text = SettingsManager.Get(DisplayKey("RefreshSeen", gdiDeviceName), "");
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

        var rates = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var hz) && hz > 0) rates.Add(hz);
        }

        return rates;
    }

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

            var displays = GetDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            var offered = primary?.Rates ?? supported.ToArray();

            result["current"] = new Dictionary<string, object?>
            {
                ["width"] = current.dmPelsWidth,
                ["height"] = current.dmPelsHeight,
                ["refreshHz"] = current.dmDisplayFrequency,
                ["bitsPerPixel"] = current.dmBitsPerPel
            };
            result["adapter"] = GetAdapterName();
            result["monitor"] = primary?.Monitor;
            result["bitsPerColorChannel"] = GetBitsPerColorChannel();
            result["supportedRefreshHz"] = offered;
            result["driverRefreshHz"] = supported.ToArray();
            result["maxRefreshHz"] = offered.Count > 0 ? offered.Max() : current.dmDisplayFrequency;
            result["displays"] = displays.Select(d => new Dictionary<string, object?>
            {
                ["gdiName"] = d.GdiName,
                ["adapter"] = d.Adapter,
                ["monitor"] = d.Monitor,
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
