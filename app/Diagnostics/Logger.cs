using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Nitrous.Diagnostics;

/// <summary>
/// Minimal, dependency-free file logger. Hardware and power-plan failures used to be
/// swallowed silently; this gives users and maintainers a breadcrumb trail without
/// introducing a logging framework.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly string LogDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nitrous", "logs");
    private static readonly string LogPath = Path.Combine(LogDirectoryPath, "nitrous.log");
    private static readonly Mutex? CrossProcessGate = CreateCrossProcessGate();
    private const long MaxBytes = 1024 * 1024;
    private const int MaxFiles = 3;

    private static int _initialized;
    private static volatile string _role = "app";

    public static string LogDirectory => LogDirectoryPath;
    public static string Role => _role;

    /// <summary>
    /// Initializes the log once per process. The tray and dashboard are separate processes
    /// writing to the same file, so the role is recorded on every line and cross-process
    /// writes are serialized with a named mutex.
    /// </summary>
    public static void Initialize(string role = "app")
    {
        if (!string.IsNullOrWhiteSpace(role)) _role = role;
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        try
        {
            Directory.CreateDirectory(LogDirectoryPath);
            Info($"Nitrous {typeof(Logger).Assembly.GetName().Version} starting " +
                 $"(pid={Environment.ProcessId}, role={_role}, os={Environment.OSVersion.Version}, " +
                 $"runtime={RuntimeInformation.FrameworkDescription})");
        }
        catch
        {
            // Logging must never prevent the app from starting.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Current log first, then rotated logs oldest-last.</summary>
    public static IReadOnlyList<string> GetLogFiles()
    {
        var files = new List<string>();
        if (File.Exists(LogPath)) files.Add(LogPath);

        for (var i = 1; i <= MaxFiles; i++)
        {
            var path = Path.Combine(LogDirectoryPath, $"nitrous.{i}.log");
            if (File.Exists(path)) files.Add(path);
        }

        return files;
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectoryPath);

                var ownsCrossProcessGate = EnterCrossProcessGate();
                try
                {
                    RotateIfNeeded();

                    var line = new StringBuilder()
                        .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append(" [").Append(level).Append("] [")
                        .Append(_role).Append(':').Append(Environment.ProcessId).Append("] ")
                        .Append(message);

                    if (exception != null)
                    {
                        line.AppendLine();
                        line.Append(exception);
                    }

                    File.AppendAllText(LogPath, line.AppendLine().ToString(), new UTF8Encoding(false));
                }
                finally
                {
                    ExitCrossProcessGate(ownsCrossProcessGate);
                }
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
            var source = Path.Combine(LogDirectoryPath, $"nitrous.{i}.log");
            var destination = Path.Combine(LogDirectoryPath, $"nitrous.{i + 1}.log");
            if (!File.Exists(source)) continue;
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }

        File.Move(LogPath, Path.Combine(LogDirectoryPath, "nitrous.1.log"), overwrite: true);
    }

    private static Mutex? CreateCrossProcessGate()
    {
        try
        {
            // Local\ is scoped to the current logon session, which is exactly the scope the
            // tray and dashboard share. Global\ would need additional privileges.
            return new Mutex(false, @"Local\NitrousLog");
        }
        catch
        {
            return null;
        }
    }

    private static bool EnterCrossProcessGate()
    {
        if (CrossProcessGate == null) return false;

        try
        {
            return CrossProcessGate.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died while holding it; we now own it.
            return true;
        }
        catch
        {
            // If the mutex cannot be used, still attempt the write. The append is best-effort.
            return false;
        }
    }

    private static void ExitCrossProcessGate(bool owned)
    {
        if (!owned || CrossProcessGate == null) return;

        try
        {
            CrossProcessGate.ReleaseMutex();
        }
        catch
        {
            // Not owned (or already released); never throw from diagnostics.
        }
    }
}
