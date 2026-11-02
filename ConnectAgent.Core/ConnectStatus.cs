using System.Text.Json;

namespace ConnectAgent.Core;

public sealed record ConnectTaskStatus(int Id, string State, string? Trace);

public sealed record ConnectorStatus(string Name, string State, string? Trace, IReadOnlyList<ConnectTaskStatus> Tasks)
{
    /// <summary>"connector: cause" and "task N: cause" for each part in FAILED; empty when all run.</summary>
    public IReadOnlyList<string> FailedParts()
    {
        var parts = new List<string>();
        if (State == "FAILED")
            parts.Add($"connector: {ConnectStatus.RootCause(Trace)}");
        foreach (var t in Tasks)
            if (t.State == "FAILED")
                parts.Add($"task {t.Id}: {ConnectStatus.RootCause(t.Trace)}");
        return parts;
    }
}

/// <summary>GET /connectors?expand=status from Kafka Connect.</summary>
public static class ConnectStatus
{
    public static IReadOnlyList<ConnectorStatus> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ConnectorStatus>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var status = p.Value.GetProperty("status");
            var connector = status.GetProperty("connector");
            var tasks = new List<ConnectTaskStatus>();
            if (status.TryGetProperty("tasks", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                    tasks.Add(new ConnectTaskStatus(t.GetProperty("id").GetInt32(), Text(t, "state") ?? "", Text(t, "trace")));
            list.Add(new ConnectorStatus(p.Name, Text(connector, "state") ?? "", Text(connector, "trace"), tasks));
        }
        return list;
    }

    /// <summary>The innermost "Caused by:" of a Java stack trace, else its first line, at most 300 characters.</summary>
    public static string RootCause(string? trace)
    {
        if (string.IsNullOrEmpty(trace))
            return "no trace";
        var lines = trace.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var causes = lines.Where(l => l.StartsWith("Caused by: ", StringComparison.Ordinal)).ToArray();
        var line = causes.Length > 0 ? causes[^1]["Caused by: ".Length..] : lines[0];
        return line.Length > 300 ? line[..300] : line;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
