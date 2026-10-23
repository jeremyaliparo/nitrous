using System;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Mvvm;

public class ActionDebouncer : IDisposable
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private Action? _pendingAction;
    private int _generation;
    private bool _disposed;

    public void Debounce(int milliseconds, Action action)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _timer?.Dispose();
            _pendingAction = action;
            var generation = ++_generation;

            _timer = new System.Threading.Timer(_ =>
            {
                Action? pending;
                lock (_gate)
                {
                    if (_disposed || generation != _generation) return;
                    pending = _pendingAction;
                    _pendingAction = null;
                }

                InvokeSafely(pending);
            }, null, milliseconds, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Runs a pending action immediately. Used when a window closes before the debounce
    /// interval elapses, so the last slider position is still persisted.
    /// </summary>
    public void Flush()
    {
        Action? pending;
        lock (_gate)
        {
            if (_disposed) return;

            _generation++;
            _timer?.Dispose();
            _timer = null;
            pending = _pendingAction;
            _pendingAction = null;
        }

        InvokeSafely(pending);
    }

    private static void InvokeSafely(Action? action)
    {
        if (action == null) return;

        try { action(); }
        catch (Exception ex) { Logger.Warn("Debounced action failed.", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _pendingAction = null;
        }
    }
}
