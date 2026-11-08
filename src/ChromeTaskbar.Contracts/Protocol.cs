using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace ChromeTaskbar.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public const int MaxTabs = 512;
    public const int MaxTitleUnits = 1024;
    public const ulong PidMax = uint.MaxValue;
    public const int MaxIconSide = 64;
    /// <summary>Raw icon bytes per Result frame; tabs past the budget go without an icon (keeps frames under MaxFrameBytes).</summary>
    public const int MaxIconBytesPerFrame = 1536 * 1024;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "Hello", "ReadTabs", "SelectTab", "Invalidate", "Cancel", "Result" };
    private static readonly HashSet<string> ResultStatuses = new(StringComparer.Ordinal)
    { "Success", "Stale", "Unavailable", "Incomplete", "WindowGone", "ForegroundBlocked", "Timeout", "Cancelled", "ProtocolError" };

    public sealed record Message(string Type, ulong WorkerEpoch, ulong RequestId, JsonElement Payload);
    public sealed class ProtocolException(string message) : IOException(message);

    public static string SerializeUnsigned(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    public static TabAvailability ProbeAvailabilityFor(int visited, int depth, TimeSpan elapsed, int tabs, int titleUnits) =>
        visited >= 1000 || depth >= 12 || elapsed >= TimeSpan.FromMilliseconds(750) || tabs > MaxTabs || titleUnits > MaxTitleUnits
            ? TabAvailability.Incomplete : TabAvailability.Available;

    public static ulong ParseUnsigned(string value, string field, ulong maximum = ulong.MaxValue)
    {
        if (string.IsNullOrEmpty(value) || (value.Length > 1 && value[0] == '0') || value.Any(c => c is < '0' or > '9') ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > maximum)
            throw new ProtocolException($"{field} must be a decimal U64 in range.");
        return parsed;
    }
    public static WindowKey ParseWindowKey(JsonElement payload)
    {
        if (!payload.TryGetProperty("windowKey", out var key) || key.ValueKind != JsonValueKind.Object) throw new ProtocolException("windowKey is required.");
        var hwnd = ParseUnsigned(RequireString(key, "hwnd"), "hwnd", (ulong)nint.MaxValue);
        var processId = ParseUnsigned(RequireString(key, "processId"), "processId", PidMax);
        var processStart = ParseUnsigned(RequireString(key, "processStartTimeUtc"), "processStartTimeUtc", long.MaxValue);
        var generation = ParseUnsigned(RequireString(key, "generation"), "generation", long.MaxValue);
        return new WindowKey((nint)hwnd, unchecked((int)(uint)processId), checked((long)processStart), checked((long)generation));
    }

    /// <summary>Optional tab field "icon": null/absent = none; otherwise {width, height, bgra(base64)} with matching length.</summary>
    public static TabIcon? ParseIcon(JsonElement tab)
    {
        if (!tab.TryGetProperty("icon", out var icon) || icon.ValueKind == JsonValueKind.Null) return null;
        if (icon.ValueKind != JsonValueKind.Object ||
            !icon.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) || width is < 1 or > MaxIconSide ||
            !icon.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height) || height is < 1 or > MaxIconSide)
            throw new ProtocolException("tab icon size is invalid.");
        byte[] bgra;
        try { bgra = Convert.FromBase64String(RequireString(icon, "bgra")); }
        catch (FormatException) { throw new ProtocolException("tab icon bgra must be base64."); }
        if (bgra.Length != width * height * 4) throw new ProtocolException("tab icon bgra length mismatch.");
        return new TabIcon(width, height, bgra);
    }

    public static object? IconToWire(TabIcon? icon) =>
        icon is null ? null : new { width = icon.Width, height = icon.Height, bgra = Convert.ToBase64String(icon.Bgra) };

    public static Message Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= MaxFrameBytes) throw new ProtocolException("Frame exceeds MaxFrameBytes.");
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ProtocolException("Frame root must be an object.");
            var type = RequireString(root, "type");
            if (!Types.Contains(type)) throw new ProtocolException("Unknown message type.");
            if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var parsedVersion) || parsedVersion != Version) throw new ProtocolException("Unsupported protocolVersion.");
            var epoch = ParseUnsigned(RequireString(root, "workerEpoch"), "workerEpoch");
            var requestId = ParseUnsigned(RequireString(root, "requestId"), "requestId");
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) throw new ProtocolException("payload is required.");
            ValidatePayload(type, payload);
            return new Message(type, epoch, requestId, payload.Clone());
        }
        catch (JsonException exception) { throw new ProtocolException($"Invalid JSON: {exception.Message}"); }
    }

    public static byte[] Serialize(Message message)
    {
        if (!Types.Contains(message.Type)) throw new ProtocolException("Unknown message type.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("type", message.Type); writer.WriteNumber("protocolVersion", Version);
            writer.WriteString("workerEpoch", SerializeUnsigned(message.WorkerEpoch)); writer.WriteString("requestId", SerializeUnsigned(message.RequestId));
            writer.WritePropertyName("payload"); message.Payload.WriteTo(writer); writer.WriteEndObject();
        }
        var bytes = stream.ToArray(); _ = Parse(bytes); return bytes;
    }

    public static async Task<Message> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(uint)]; await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length >= MaxFrameBytes) throw new ProtocolException("Frame exceeds MaxFrameBytes.");
        var body = new byte[(int)length]; await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false); return Parse(body);
    }

    public static async Task WriteFrameAsync(Stream stream, Message message, CancellationToken cancellationToken)
    {
        var body = Serialize(message); var header = new byte[sizeof(uint)]; BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)body.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false); await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false); await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < buffer.Length;)
        { var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException("Pipe closed during a frame."); offset += read; }
    }

    private static string RequireString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text) throw new ProtocolException($"{property} must be a string.");
        return text;
    }

    private static void ValidatePayload(string type, JsonElement payload)
    {
        if (type is "ReadTabs" or "Invalidate" or "SelectTab")
        {
            _ = ParseWindowKey(payload);
        }
        if (type == "SelectTab")
        {
            var snapshotId = RequireString(payload, "snapshotId");
            if (!Guid.TryParse(snapshotId, out _)) throw new ProtocolException("snapshotId must be a GUID.");
            var token = RequireString(payload, "tabToken");
            if (token.Length == 0 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new ProtocolException("tabToken must be base64url.");
        }
        if (type is "ReadTabs" or "SelectTab")
        {
            var deadline = RequireString(payload, "deadlineUtc");
            if (!DateTimeOffset.TryParseExact(deadline, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                throw new ProtocolException("deadlineUtc must be RFC3339 UTC.");
        }
        if (type == "Cancel") ParseUnsigned(RequireString(payload, "targetRequestId"), "targetRequestId");
        if (type == "Result")
        {
            var status = RequireString(payload, "status");
            if (!ResultStatuses.Contains(status)) throw new ProtocolException("Result status is invalid.");
            if (payload.TryGetProperty("tabs", out var tabs))
            {
                if (status == "Incomplete" || tabs.ValueKind != JsonValueKind.Array || tabs.GetArrayLength() > MaxTabs) throw new ProtocolException("Result tabs are invalid.");
                foreach (var tab in tabs.EnumerateArray())
                {
                    if (tab.ValueKind != JsonValueKind.Object || RequireString(tab, "title").Length > MaxTitleUnits) throw new ProtocolException("Result title is invalid.");
                    _ = ParseIcon(tab);
                }
            }
            if (payload.TryGetProperty("availability", out var availability) &&
            (availability.ValueKind != JsonValueKind.String || availability.GetString() != "Complete" || status != "Success"))
                throw new ProtocolException("Result availability is invalid.");
        }
    }
}
