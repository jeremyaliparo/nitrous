using System;
using System.IO;
using System.Text;

namespace Nitrous.Diagnostics;

/// <summary>
/// Minimal, dependency-free file logger. Hardware and power-plan failures used to be
/// swallowed silently; this gives users and maintainers a breadcrumb trail without
/// introducing a logging framework.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nitrous", "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "nitrous.log");
    private const long MaxBytes = 1024 * 1024;
    private const int MaxFiles = 3;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
            Info($"Nitrous {typeof(Logger).Assembly.GetName().Version} starting " +
                 $"(admin={Environment.UserName}, os={Environment.OSVersion.Version})");
        }
        catch
        {
            // Logging must never prevent the app from starting.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);

                if (exception != null)
                {
                    line.AppendLine();
                    line.Append(exception);
                }

                File.AppendAllText(LogPath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never throw from diagnostics.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxBytes) return;

        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var source = Path.Combine(LogDirectory, $"nitrous.{i}.log");
            var destination = Path.Combine(LogDirectory, $"nitrous.{i + 1}.log");
            if (!File.Exists(source)) continue;
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }

        File.Move(LogPath, Path.Combine(LogDirectory, "nitrous.1.log"), overwrite: true);
    }
}
