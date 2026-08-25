using System;
using System.Threading;

namespace Nitrous.Mvvm;

public class ActionDebouncer
{
    private System.Threading.Timer? _timer;

    public void Debounce(int milliseconds, Action action)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => action(), null, milliseconds, Timeout.Infinite);
    }
}
