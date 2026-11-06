using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nitrous.Diagnostics;
using Nitrous.Ui.Theme;

using Application = System.Windows.Application;

namespace Nitrous.Ui;

/// <summary>
/// Renders a dashboard page to a PNG offscreen, so a screenshot does not depend on the window being
/// visible, on top, or on the screen not being locked.
///
/// This exists because capturing the desktop is the wrong tool for the job: it picks up whatever is
/// behind the rounded transparent corners, it cannot produce a 2x asset, it fights the foreground
/// lock, and on a scaled display it silently returns the wrong pixel size. Rendering the visual tree
/// avoids all of that and needs no elevation.
///
/// The window is still shown, at an off-screen position, because the view model only starts polling
/// once the window is visible and the GPU page only polls while it is the active page.
/// </summary>
internal static class UiCapture
{
    public static int Run(string page, string outputPath, double scale, int settleMs)
    {
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeManager.Initialise(app);

            var window = new NitrousDashboard
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                Topmost = false
            };

            window.Show();
            window.SelectPageForCapture(page);

            var wait = settleMs > 0 ? settleMs : DefaultSettleFor(page);
            Logger.Info($"Capture: page '{page}' at scale {scale}, settling {wait} ms, writing '{outputPath}'.");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    Save(window, outputPath, scale);
                }
                catch (Exception ex)
                {
                    Logger.Error("Capture: failed to render the page.", ex);
                    Environment.ExitCode = 2;
                }

                app.Shutdown();
            };
            timer.Start();

            app.Run();
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            Logger.Error("Capture: failed to start.", ex);
            return 1;
        }
    }

    /// <summary>
    /// The GPU page polls NVML on a 2 s tick and re-reads the PnP state first, so a short wait
    /// captures placeholder zeros and a generic "NVIDIA GPU" header. The others only need a
    /// telemetry tick.
    /// </summary>
    private static int DefaultSettleFor(string page) =>
        page.ToLowerInvariant() switch
        {
            "gpu" => 10000,
            "kb" or "keyboard" => 4000,
            _ => 3500
        };

    private static void Save(Window window, string path, double scale)
    {
        scale = Math.Clamp(scale, 1, 4);
        window.UpdateLayout();

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("the window has no measured size yet");

        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);

        bitmap.Render(window);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);

        Logger.Info($"Capture: wrote {path} ({(int)Math.Round(width * scale)}x{(int)Math.Round(height * scale)}).");
    }
}
