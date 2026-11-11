using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Acer's custom boot logo. The firmware reads <c>\EFI\OEM\AcerLogo.jpg</c> or <c>AcerLogo.gif</c>
/// from the EFI system partition at POST; the BIOS setup's "Customize POST animation" setting
/// (misc 8) decides whether it is shown. Nothing here touches the BIOS or the EC: the write is a
/// file on the ESP plus that one documented setting.
/// </summary>
internal static class BootLogo
{
    public const string JpegName = "AcerLogo.jpg";
    public const string GifName = "AcerLogo.gif";

    private const uint SwitchSetting = 0x08;
    private const long SpaceMargin = 1024 * 1024;
    private const long MaxBytes = 8 * 1024 * 1024;

    private static readonly Lazy<bool> Probe = new(ReadSupport);

    /// <summary>SMBIOS record 0x0D, or the BIOS switch answering 0/1.</summary>
    public static bool IsSupported => Probe.Value;

    /// <summary>The picture in use, or null while Acer's logo shows.</summary>
    public static (string Name, byte[]? Data)? Read()
    {
        if (FindPartition() is not { } root) return null;

        foreach (var name in new[] { JpegName, GifName })
        {
            var path = Path.Combine(root, "EFI", "OEM", name);
            try
            {
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                var data = info.Length <= MaxBytes ? File.ReadAllBytes(path) : null;
                return (name, data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dashboard opened without elevation, or the volume is locked: no preview is a
                // better outcome than a crash, and nothing here is required for the other cards.
                Logger.Warn($"Boot logo: could not read {name}.", ex);
                return null;
            }
        }

        return null;
    }

    /// <summary>Re-encodes the picture and writes it to the ESP, then turns the firmware switch on.</summary>
    public static bool Apply(string source, out string error)
    {
        error = string.Empty;

        byte[] data;
        string name;
        try
        {
            var (panelWidth, panelHeight) = PanelSize();
            using var image = Image.FromStream(new MemoryStream(File.ReadAllBytes(source)));
            using var scaled = Scale(image, panelWidth * 2 / 5, panelHeight * 2 / 5);
            data = Encode(scaled, out name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException
                                       or ArgumentException or ExternalException)
        {
            error = "That file could not be read as a picture.";
            Logger.Warn("Boot logo: the picture could not be prepared.", ex);
            return false;
        }

        if (data.Length > MaxBytes)
        {
            error = "The picture is too large for the firmware even after scaling.";
            return false;
        }

        if (FindPartition() is not { } root)
        {
            error = "No EFI system partition was found, so the firmware has nowhere to read it from.";
            return false;
        }

        var folder = Path.Combine(root, "EFI", "OEM");
        if (FreeSpace(root) is { } free && free < data.Length + SpaceMargin)
        {
            error = "The EFI system partition does not have enough free space.";
            return false;
        }

        var temp = Path.Combine(folder, "AcerLogo.new");
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(temp)) File.Delete(temp);

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(flushToDisk: true);
            }

            // The firmware takes whichever picture it finds, so only one may exist.
            foreach (var other in new[] { JpegName, GifName })
            {
                var path = Path.Combine(folder, other);
                if (File.Exists(path)) File.Delete(path);
            }

            var target = Path.Combine(folder, name);
            File.Move(temp, target);

            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(data))
            {
                error = "The picture read back differs from what was written; Acer's logo was left out.";
                File.Delete(target);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            error = "The picture could not be written to the EFI system partition.";
            Logger.Warn("Boot logo: the write failed.", ex);
            return false;
        }

        var previous = ReadSwitch();
        SettingsManager.Save("KbBootLogoFile", name);
        SettingsManager.Save("KbBootLogoSwitch", previous ?? -1);
        SettingsManager.Save("KbBootLogo", 1);

        if (previous is not null && !WriteSwitch(1))
        {
            error = "The picture is in place, but the firmware refused to switch it on.";
            return false;
        }

        Logger.Info($"Boot logo: {name} written ({data.Length} bytes); switch was {previous?.ToString() ?? "absent"}.");
        return true;
    }

    /// <summary>Deletes the picture and puts the firmware switch back the way it was found.</summary>
    public static bool Restore(out string error)
    {
        error = string.Empty;
        if (FindPartition() is not { } root)
        {
            error = "No EFI system partition was found.";
            return false;
        }

        var folder = Path.Combine(root, "EFI", "OEM");
        var removed = false;
        try
        {
            foreach (var name in new[] { JpegName, GifName, "AcerLogo.new" })
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Acer's logo is back, but a file could not be removed.";
            Logger.Warn("Boot logo: restore could not delete the picture.", ex);
            return false;
        }

        var previous = SettingsManager.Get("KbBootLogoSwitch", -1);
        if (previous >= 0) WriteSwitch(previous);

        SettingsManager.Save("KbBootLogo", 0);
        SettingsManager.Save("KbBootLogoFile", string.Empty);
        SettingsManager.Save("KbBootLogoSwitch", -1);
        Logger.Info($"Boot logo: {(removed ? "the custom picture was removed" : "there was no custom picture")}.");
        return true;
    }

    /// <summary>What the firmware advertises, for the diagnostics bundle.</summary>
    public static string Describe()
    {
        var record = AcerSmbios.Read().Gaming(AcerGamingRecord.CustomBootLogo);
        var sw = ReadSwitch();
        return $"record 0x0D={(record?.ToString() ?? "absent")}, misc 0x08={(sw?.ToString() ?? "no answer")}, " +
               $"picture={(Read()?.Name ?? "none")}";
    }

    private static bool ReadSupport()
    {
        try
        {
            if (AcerSmbios.Read().Gaming(AcerGamingRecord.CustomBootLogo) == 1) return true;
            return ReadSwitch() is 0 or 1;
        }
        catch (Exception ex)
        {
            Logger.Warn("Boot logo: the capability probe failed.", ex);
            return false;
        }
    }

    /// <summary>Misc 8's value (bits 8-15); null when the firmware has no such setting.</summary>
    private static int? ReadSwitch()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT * FROM AcerGamingFunction"));
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("GetGamingMiscSetting");
                    inParams["gmInput"] = SwitchSetting;
                    using var outParams = instance.InvokeMethod("GetGamingMiscSetting", inParams, null);
                    if (outParams?["gmOutput"] is not { } raw) return null;

                    var value = (int)((Convert.ToUInt64(raw) >> 8) & 0xFF);
                    return value is 0 or 1 ? value : null;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Logger.Warn("Boot logo: the firmware switch could not be read.", ex);
        }

        return null;
    }

    private static bool WriteSwitch(int value)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT * FROM AcerGamingFunction"));
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    using var inParams = instance.GetMethodParameters("SetGamingMiscSetting");
                    inParams["gmInput"] = ((ulong)(value & 0xFF) << 8) | SwitchSetting;
                    using var outParams = instance.InvokeMethod("SetGamingMiscSetting", inParams, null);
                    return outParams is not null;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Logger.Warn("Boot logo: the firmware switch could not be written.", ex);
        }

        return false;
    }

    /// <summary>The EFI system partition as a volume path, never a drive letter.</summary>
    private static string? FindPartition()
    {
        const string esp = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"),
                new ObjectQuery("SELECT GptType, IsSystem, AccessPaths FROM MSFT_Partition"));
            foreach (ManagementObject partition in searcher.Get())
            {
                using (partition)
                {
                    if (partition["IsSystem"] is not true) continue;
                    if (!string.Equals(partition["GptType"] as string, esp, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var path = (partition["AccessPaths"] as string[] ?? Array.Empty<string>())
                        .FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
                    if (path is not null) return path;
                }
            }

            Logger.Warn("Boot logo: no EFI system partition was found.");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Logger.Warn("Boot logo: the EFI system partition could not be located.", ex);
        }

        return null;
    }

    private static (int Width, int Height) PanelSize()
    {
        try
        {
            using (var modes = new ManagementObjectSearcher(
                       new ManagementScope(@"\\.\root\wmi"),
                       new ObjectQuery("SELECT InstanceName, MonitorSourceModes, PreferredMonitorSourceModeIndex " +
                                       "FROM WmiMonitorListedSupportedSourceModes")))
            {
                foreach (ManagementObject monitor in modes.Get())
                {
                    using (monitor)
                    {
                        if (monitor["MonitorSourceModes"] is not ManagementBaseObject[] list ||
                            monitor["PreferredMonitorSourceModeIndex"] is not ushort preferred ||
                            preferred >= list.Length) continue;
                        var width = Convert.ToInt32(list[preferred]["HorizontalActivePixels"]);
                        var height = Convert.ToInt32(list[preferred]["VerticalActivePixels"]);
                        if (width > 0 && height > 0) return (width, height);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Logger.Warn("Boot logo: the panel mode could not be read; falling back to the desktop size.", ex);
        }

        // Acer's own limit is a share of the panel; the current mode is a close enough substitute.
        return (System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080);
    }

    private static Bitmap Scale(Image source, int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(64, maxWidth);
        maxHeight = Math.Max(64, maxHeight);

        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);                    // a transparent PNG gets the splash's black
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return bitmap;
    }

    /// <summary>
    /// Baseline JPEG, or GIF when the encoder produced something else. Insyde's decoder takes only
    /// baseline, so the frame marker is checked rather than trusted.
    /// </summary>
    private static byte[] Encode(Image image, out string name)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            image.Save(stream, codec, parameters);
        }

        var data = stream.ToArray();
        if (IsBaseline(data))
        {
            name = JpegName;
            Debug.Assert(data[0] == 0xFF && data[1] == 0xD8, "JPEG without an SOI marker");
            return data;
        }

        name = GifName;
        using var gif = new MemoryStream();
        image.Save(gif, ImageFormat.Gif);
        return gif.ToArray();
    }

    /// <summary>SOF0 with three components is what the firmware's decoder needs.</summary>
    private static bool IsBaseline(byte[] data)
    {
        for (var i = 2; i + 4 < data.Length;)
        {
            if (data[i] != 0xFF) return false;
            var marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker == 0xDA) return false;          // the scan started without a frame header
            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length) return false;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return marker == 0xC0;
            i += 2 + length;
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total,
        out ulong totalFree);

    private static long? FreeSpace(string root) =>
        GetDiskFreeSpaceEx(root, out var available, out _, out _) ? (long)available : null;
}
