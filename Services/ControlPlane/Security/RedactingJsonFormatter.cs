using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace NightSignal.ControlPlane.Security;

/// <summary>
/// Structured console logs: one JSON object per line (timestamp, level, category, event, message, template
/// arguments, exception). Every string passes through <see cref="Redaction.Redact"/> as a last line of defence;
/// the code itself never logs tokens, passwords, keys or secrets.
/// </summary>
public sealed class RedactingJsonFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "night-signal-json";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        string message = entry.Formatter(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message) && entry.Exception is null) return;

        var fields = new Dictionary<string, string?>();
        if (entry.State is IReadOnlyList<KeyValuePair<string, object?>> values)
            foreach (KeyValuePair<string, object?> kv in values)
                if (kv.Key != "{OriginalFormat}")
                    fields[kv.Key] = Redaction.Redact(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));

        writer.WriteLine(JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            level = entry.LogLevel.ToString(),
            category = entry.Category,
            eventId = entry.EventId.Id,
            message = Redaction.Redact(message),
            fields,
            exception = entry.Exception is null ? null : Redaction.Redact(entry.Exception.ToString()),
        }));
    }
}
