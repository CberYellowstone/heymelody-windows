using System.Diagnostics;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Probe;

/// <summary>One control connection, serial GETs, no registration or automatic replies.</summary>
public sealed class ReadOnlySession
{
    public const string X4ProductId = "06EC10";
    private static readonly HashSet<ushort> Allowlist = [0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x010D, 0x0114];
    private readonly IQueryTransport _transport;
    private readonly PacketDecoder _decoder = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TimeSpan _queryTimeout;
    private readonly SequenceAllocator _sequences = new();
    private bool _usable = true;

    public ReadOnlySession(IQueryTransport transport, TimeSpan? queryTimeout = null)
    {
        _transport = transport;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(5);
    }

    public static bool IsAllowed(ushort command, ReadOnlySpan<byte> payload)
        => Allowlist.Contains(command)
            && (command == 0x010D ? payload.SequenceEqual(new byte[] { 1, 0x18 }) : payload.IsEmpty);

    public async Task<QueryResult> QueryAsync(ushort command, ReadOnlyMemory<byte> payload,
        ReadOnlyEvidence evidence, CancellationToken cancellationToken)
    {
        if (!IsAllowed(command, payload.Span))
            throw new InvalidOperationException("This command or payload is outside the read-only policy.");

        await _requestGate.WaitAsync(cancellationToken);
        var result = new QueryResult
        {
            RequestCommand = $"0x{command:X4}",
            ResponseCommand = $"0x{(command | 0x8000):X4}"
        };
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_queryTimeout);
        try
        {
            if (!_usable)
            {
                result.Disposition = "session-unavailable";
                result.Error = "The connection is no longer synchronized; no further query was submitted.";
                return result;
            }
            if (!_sequences.TryTake(out var sequence))
            {
                _usable = false;
                _decoder.Reset();
                result.Disposition = "sequence-exhausted";
                result.Error = "All 256 request sequences are retired; create a new control session.";
                return result;
            }
            result.Sequence = sequence;
            var requestKey = new RequestKey(command, sequence);
            var frame = PacketEncoder.Encode(command, sequence, payload.Span);
            // Preserve attempted submissions even if the native send fails part way through.
            evidence.SentQueries.Add(new SentQuery(result.RequestCommand, sequence,
                Convert.ToHexString(payload.Span), Convert.ToHexString(frame)));
            result.Submitted = true;
            await _transport.SendAsync(frame, timeout.Token);
            var buffer = new byte[4096];
            while (true)
            {
                var length = await _transport.ReadAsync(buffer, timeout.Token);
                if (length <= 0)
                {
                    _decoder.Complete();
                    _usable = false;
                    result.Disposition = "disconnected";
                    result.Error = "The control connection closed before a matching response.";
                    break;
                }
                var packets = _decoder.Feed(buffer.AsSpan(0, length));
                Packet? matched = null;
                foreach (var packet in packets)
                {
                    if (matched is null && requestKey.MatchesResponse(packet))
                        matched = packet;
                    else
                        evidence.IgnoredPackets++;
                }
                if (matched is null)
                    continue;
                result.ResponseMatched = true;
                result.ResponsePayloadLength = matched.Payload.Length;
                if (command == 0x0104 && matched.Payload.Length <= 64)
                    result.ProtocolVersionPayloadHex = Convert.ToHexString(matched.Payload);
                var reply = QueryParsers.Parse(command, matched.Payload);
                result.Status = reply.Status;
                result.Success = reply.Success;
                result.Error = reply.Error;
                result.Data = reply.Data;
                result.Disposition = reply.Success ? "response" : "rejected-or-malformed";
                if (!reply.Success && reply.Status is null or 0)
                {
                    _usable = false;
                    _decoder.Reset();
                }
                break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _usable = false;
            _decoder.Reset();
            result.Disposition = "timeout";
            result.Error = "No matching response arrived within the query timeout.";
        }
        catch (OperationCanceledException)
        {
            _usable = false;
            _decoder.Reset();
            result.Disposition = "cancelled";
            result.Error = "The session was cancelled.";
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            _usable = false;
            _decoder.Reset();
            result.Disposition = "malformed-frame";
            result.Error = "The received control stream failed strict framing validation.";
        }
        catch (Exception)
        {
            _usable = false;
            _decoder.Reset();
            result.Disposition = "transport-failure";
            result.Error = "The control transport failed during this query.";
        }
        finally
        {
            result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
            _requestGate.Release();
        }
        return result;
    }

    public async Task RunQueriesAsync(ProbeResult result, CancellationToken cancellationToken)
    {
        result.Phase = "identity";
        var identity = await QueryAsync(0x0103, ReadOnlyMemory<byte>.Empty, result.ReadOnly, cancellationToken);
        result.Queries.Add(identity);
        if (identity.Success && identity.Data.TryGetValue("productId", out var productId) && productId is string text)
            result.Target.ProductId = text;
        result.Target.IdentityVerified = identity.Success && result.Target.ProductId == X4ProductId;
        if (!result.Target.IdentityVerified)
        {
            result.Error = identity.Success ? "The reported Product ID is not Enco X4 (06EC10)." : "Enco X4 identity could not be verified.";
            return;
        }

        result.Phase = "capabilities";
        var capabilities = await QueryAsync(0x0100, ReadOnlyMemory<byte>.Empty, result.ReadOnly, cancellationToken);
        result.Queries.Add(capabilities);
        if (!capabilities.Success || !capabilities.Data.TryGetValue("supportedCommands", out var value) || value is not ushort[] supported)
        {
            result.Error = "The command capability response could not be verified.";
            return;
        }
        var commands = supported.ToHashSet();
        result.Phase = "queries";
        foreach (var command in new ushort[] { 0x0101, 0x0102, 0x0104, 0x0105, 0x0114, 0x010D })
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Protocol-defined foundation queries are allowed independently of the bitmap.
            if ((command is 0x0105 or 0x0114) && !commands.Contains(command))
            {
                result.Queries.Add(new QueryResult
                {
                    RequestCommand = $"0x{command:X4}", ResponseCommand = $"0x{(command | 0x8000):X4}",
                    Disposition = "unsupported", Error = "This query is not advertised by the command capability bitmap."
                });
                continue;
            }
            result.Queries.Add(await QueryAsync(command,
                command == 0x010D ? new byte[] { 1, 0x18 } : ReadOnlyMemory<byte>.Empty,
                result.ReadOnly, cancellationToken));
        }

        var firmware = result.Queries.Single(q => q.RequestCommand == "0x0105");
        var codec = result.Queries.Single(q => q.RequestCommand == "0x0114");
        var feature = result.Queries.Single(q => q.RequestCommand == "0x010D");
        var hasHiRes = feature.Success && feature.Data.TryGetValue("hiRes", out var hiRes) && hiRes is bool;
        result.Success = result.Target.IdentityVerified && result.Queries.All(query => query.Success) && hasHiRes;
        result.Phase = result.Success ? "complete" : "partial";
        if (!hasHiRes)
            result.Warnings.Add("The headset did not provide a confirmed 0/1 state for feature 0x18.");
        if (result.Queries.Any(q => !q.Success))
            result.Warnings.Add("Some queries were unsupported, rejected, malformed, or timed out; see individual results.");
        if (!result.Success)
            result.Error = "The read-only session returned partial evidence; not all required queries were verified.";
        var protocolVersion = result.Queries.Single(query => query.RequestCommand == "0x0104");
        if (protocolVersion.Success && protocolVersion.Data.TryGetValue("protocolVersionInterpretationVerified", out var interpreted)
            && interpreted is false)
            result.Warnings.Add("The protocol-version response is preserved as raw bytes; its binary version semantics are not verified.");
        result.Warnings.Add("Earphone feature state and reported codec do not verify negotiated sample rate, measured bitrate, lossless audio, or latency.");
    }
}

