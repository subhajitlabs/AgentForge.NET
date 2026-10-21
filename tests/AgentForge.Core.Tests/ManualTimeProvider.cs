namespace AgentForge.Core.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _utcNow.Ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
            _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        ManualTimer[] due;
        lock (_gate)
        {
            _utcNow += duration;
            due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _gate = new();
        private DateTimeOffset _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime;
            _period = period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _owner.GetUtcNow() + dueTime;
                _period = period;
                return true;
            }
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_gate)
                return !_disposed && now >= _dueAt;
        }

        public void Fire()
        {
            lock (_gate)
            {
                if (_disposed || _owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = _period is { Ticks: > 0 }
                    ? _dueAt + _period
                    : DateTimeOffset.MaxValue;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
