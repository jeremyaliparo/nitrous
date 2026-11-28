using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Push-based process start/stop notifications from WMI's <c>Win32_ProcessStartTrace</c> and
/// <c>Win32_ProcessStopTrace</c>. WMI receives these from the kernel's process-notify path, so this
/// is not a polling query; the tray only wakes when a process actually starts or stops.
///
/// One snapshot is taken when the subscription starts (to include apps already running) and again
/// after a WMI restart or a resume, because events can be missed while the subscription is down.
/// </summary>
internal sealed class ProcessEventWatcher : IDisposable
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _stopping;
    private int _restarting;

    /// <summary>Raised for every process start/stop with the executable name (no extension).</summary>
    public event Action<string, bool>? ProcessChanged;

    /// <summary>Raised after a fresh snapshot, so the consumer can reconcile missed events.</summary>
    public event Action? Resynced;

    public bool IsRunning { get; private set; }

    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning) return true;

            try
            {
                _startWatcher = Create("Win32_ProcessStartTrace", OnProcessStarted);
                _stopWatcher = Create("Win32_ProcessStopTrace", OnProcessStopped);
                _startWatcher.Start();
                _stopWatcher.Start();
                SnapshotLocked();
                IsRunning = true;
                Logger.Info($"App profiles: process events active ({_counts.Count} processes tracked).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("App profiles: process event subscription failed.", ex);
                DisposeWatchersLocked();
                IsRunning = false;
                return false;
            }
        }
    }

    /// <summary>The current running names; a copy, so the caller can evaluate without the lock.</summary>
    public HashSet<string> RunningNames()
    {
        lock (_gate)
            return new HashSet<string>(_counts.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the tracked counts with a fresh process snapshot. Call after resume/WMI restart.</summary>
    public void Resync()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            SnapshotLocked();
        }

        RaiseResynced();
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_gate)
        {
            DisposeWatchersLocked();
            IsRunning = false;
            _counts.Clear();
        }
    }

    private ManagementEventWatcher Create(string eventClass, EventArrivedEventHandler handler)
    {
        var watcher = new ManagementEventWatcher(
            new ManagementScope(@"\\.\root\cimv2"),
            new WqlEventQuery($"SELECT * FROM {eventClass}"));
        watcher.EventArrived += handler;
        watcher.Stopped += OnWatcherStopped;
        return watcher;
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        var name = Normalize(e.NewEvent["ProcessName"]);
        if (name.Length == 0) return;

        lock (_gate)
        {
            _counts.TryGetValue(name, out var count);
            _counts[name] = count + 1;
        }

        RaiseChanged(name, started: true);
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        var name = Normalize(e.NewEvent["ProcessName"]);
        if (name.Length == 0) return;

        lock (_gate)
        {
            if (_counts.TryGetValue(name, out var count))
            {
                if (count <= 1) _counts.Remove(name);
                else _counts[name] = count - 1;
            }
        }

        RaiseChanged(name, started: false);
    }

    private void OnWatcherStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        if (Interlocked.Exchange(ref _restarting, 1) != 0) return;

        Logger.Warn("App profiles: the process event subscription stopped; resubscribing.");
        _ = Task.Delay(RestartDelay).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _restarting, 0);
            if (_stopping) return;

            lock (_gate)
            {
                DisposeWatchersLocked();
                IsRunning = false;
            }

            if (Start())
                RaiseResynced();
        }, TaskScheduler.Default);
    }

    private void SnapshotLocked()
    {
        _counts.Clear();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                _counts.TryGetValue(name, out var count);
                _counts[name] = count + 1;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string Normalize(object? value)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return Path.GetFileNameWithoutExtension(raw).Trim();
    }

    private void RaiseChanged(string name, bool started)
    {
        try
        {
            ProcessChanged?.Invoke(name, started);
        }
        catch (Exception ex)
        {
            Logger.Warn("App profiles: a process-event handler failed.", ex);
        }
    }

    private void RaiseResynced()
    {
        try
        {
            Resynced?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warn("App profiles: a resync handler failed.", ex);
        }
    }

    private void DisposeWatchersLocked()
    {
        if (_startWatcher != null)
        {
            _startWatcher.EventArrived -= OnProcessStarted;
            _startWatcher.Stopped -= OnWatcherStopped;
            try { _startWatcher.Stop(); } catch (ManagementException) { }
            _startWatcher.Dispose();
            _startWatcher = null;
        }

        if (_stopWatcher != null)
        {
            _stopWatcher.EventArrived -= OnProcessStopped;
            _stopWatcher.Stopped -= OnWatcherStopped;
            try { _stopWatcher.Stop(); } catch (ManagementException) { }
            _stopWatcher.Dispose();
            _stopWatcher = null;
        }
    }
}
