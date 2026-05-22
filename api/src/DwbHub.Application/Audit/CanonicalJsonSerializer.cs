using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DwbHub.Application.Audit;

/// <summary>
/// Deterministic JSON serialization + SHA-256 hashing for the audit-log
/// hash chain. Key ordering is alphabetical at every nesting level; whitespace
/// is suppressed; null values are preserved; arrays keep insertion order.
/// </summary>
public static class CanonicalJsonSerializer
{
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IReadOnlyDictionary<string, object?> payload)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOpts))
        {
            WriteObject(writer, payload);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static byte[] Hash(string canonicalJson)
        => SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));

    /// <summary>
    /// Builds the canonical tuple JSON `{ "actorUserId":…, "eventType":…, "ipAddress":…,
    /// "occurredAt":…, "payloadJson":…, "tenantId":…, "userAgent":… }` (alphabetical),
    /// then hashes it with SHA-256. `payloadJson` is embedded as a JSON STRING
    /// (already-canonicalized JSON, escaped as a string value) — not as a nested object.
    /// </summary>
    public static byte[] HashEvent(
        long? tenantId, long? actorUserId, string eventType,
        string canonicalPayloadJson,
        IPAddress? ip, string? userAgent, DateTimeOffset occurredAt)
    {
        var wrapper = new Dictionary<string, object?>
        {
            ["actorUserId"] = actorUserId,
            ["eventType"] = eventType,
            ["ipAddress"] = ip?.ToString(),
            ["occurredAt"] = occurredAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["payloadJson"] = canonicalPayloadJson, // STRING embed — not nested
            ["tenantId"] = tenantId,
            ["userAgent"] = userAgent,
        };
        return Hash(Serialize(wrapper));
    }

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> dict)
    {
        writer.WriteStartObject();
        foreach (var kv in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(kv.Key);
            WriteValue(writer, kv.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case IReadOnlyDictionary<string, object?> sub:
                WriteObject(writer, sub);
                break;
            case System.Collections.IEnumerable arr:
                writer.WriteStartArray();
                foreach (var item in arr) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
