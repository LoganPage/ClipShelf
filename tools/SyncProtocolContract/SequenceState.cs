namespace SyncProtocolContract;

internal sealed record SequenceResult(ulong AcceptedThroughSequence, bool Imported, bool Duplicate);

internal sealed class SequenceState
{
    private sealed class OriginState
    {
        public ulong Watermark;
        public Dictionary<Guid, (ulong Sequence, string Hash)> Events { get; } = [];
        public Dictionary<ulong, Guid> Sequences { get; } = [];
        public SortedSet<ulong> Pending { get; } = [];
    }

    private readonly Dictionary<Guid, OriginState> origins = [];

    public SequenceResult Accept(TextEventMessage envelope)
    {
        Guid origin = envelope.OriginDeviceId;
        Guid eventId = envelope.EventId;
        ulong sequence = envelope.Sequence;
        string hash = envelope.ContentHash;
        if (!origins.TryGetValue(origin, out OriginState? state)) origins[origin] = state = new OriginState();

        if (state.Events.TryGetValue(eventId, out var existingEvent))
        {
            if (existingEvent.Sequence != sequence || !ContentHash.FixedTimeEquals(existingEvent.Hash, hash))
                throw new ProtocolException("sequence_conflict", "eventId was previously associated with different content or sequence.");
            return new(state.Watermark, Imported: false, Duplicate: true);
        }
        if (state.Sequences.TryGetValue(sequence, out Guid existingId) && existingId != eventId)
            throw new ProtocolException("sequence_conflict", "origin/sequence was previously associated with a different eventId.");
        if (sequence <= state.Watermark)
            throw new ProtocolException("sequence_conflict", "Sequence is at or below the persisted watermark without a matching event.");

        state.Events[eventId] = (sequence, hash);
        state.Sequences[sequence] = eventId;
        state.Pending.Add(sequence);
        while (state.Watermark < ulong.MaxValue && state.Pending.Remove(state.Watermark + 1)) state.Watermark++;
        return new(state.Watermark, Imported: true, Duplicate: false);
    }

    public ulong GetWatermark(Guid originDeviceId) => origins.TryGetValue(originDeviceId, out var state) ? state.Watermark : 0;
}
