namespace HeyMelodyWindows.Protocol;

/// <summary>Exact request correlation; delayed responses from an earlier sequence never match.</summary>
public readonly record struct RequestKey(ushort Command, byte Sequence)
{
    public bool MatchesResponse(Packet response) => response is not null
        && (Command & 0x8000) == 0
        && response.Command == (ushort)(Command | 0x8000)
        && response.Sequence == Sequence;
}
