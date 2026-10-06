namespace HeyMelodyWindows.Protocol;

/// <summary>One allocator per connection. Each byte sequence is issued once; exhaustion requires a new connection.</summary>
public sealed class SequenceAllocator
{
    private readonly object _gate = new();
    private int _nextSequence;

    public int Remaining
    {
        get { lock (_gate) return 256 - _nextSequence; }
    }

    public bool TryTake(out byte sequence)
    {
        lock (_gate)
        {
            if (_nextSequence == 256) { sequence = default; return false; }
            sequence = (byte)_nextSequence++;
            return true;
        }
    }
}
