namespace MiniVideoFloat;

internal sealed class WheelGesture
{
    private long _lastDown = long.MinValue;
    internal bool Feed(int delta, long timestampMs)
    {
        if (delta < 0) { _lastDown = timestampMs; return false; }
        bool matched = delta > 0 && _lastDown != long.MinValue && timestampMs >= _lastDown && timestampMs - _lastDown <= 800;
        _lastDown = long.MinValue;
        return matched;
    }
    internal void Reset() => _lastDown = long.MinValue;
}
