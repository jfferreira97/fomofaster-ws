using System.Diagnostics;

namespace TelegramBot.Services;

// The process-wide Telegram send budget (a bit under Telegram's ~30 msg/s bulk limit), with two lanes.
// The priority lane (RN4L, active subscribers, trial users) is always served before the normal lane;
// inside a lane it is first come, first served. A plain FIFO bucket made a paying user's alert wait
// behind every free user's send of the alerts that arrived just before it.
internal sealed class PrioritySendLimiter
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource> _priority = new();
    private readonly Queue<TaskCompletionSource> _normal = new();
    private readonly double _perSecond;
    private readonly double _burst;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();
    private bool _pumping;

    public PrioritySendLimiter(double perSecond, double burst)
    {
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
    }

    // Completes when the caller may send one message.
    public Task AcquireAsync(bool priority)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            (priority ? _priority : _normal).Enqueue(tcs);
            if (!_pumping)
            {
                _pumping = true;
                _ = Task.Run(PumpAsync);
            }
        }
        return tcs.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            TaskCompletionSource? next = null;
            var wait = TimeSpan.Zero;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(_burst, _tokens + (double)(now - _lastTicks) / Stopwatch.Frequency * _perSecond);
                _lastTicks = now;

                if (_priority.Count == 0 && _normal.Count == 0) { _pumping = false; return; }

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    next = _priority.Count > 0 ? _priority.Dequeue() : _normal.Dequeue();
                }
                else
                {
                    wait = TimeSpan.FromSeconds((1 - _tokens) / _perSecond);
                }
            }

            if (next is not null) next.TrySetResult();
            else await Task.Delay(wait);
        }
    }
}
