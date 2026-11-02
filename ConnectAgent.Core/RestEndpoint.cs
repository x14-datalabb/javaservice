using System.Text.RegularExpressions;

namespace ConnectAgent.Core;

/// <summary>
/// Where Kafka Connect's REST API listens. The MSI template uses listeners=HTTPS://127.0.0.1:0,
/// a random port, so the port is taken from the line Jetty logs when the connector starts:
/// <c>Started https_127.0.0.10@1a2b3c{SSL, (ssl, http/1.1)}{127.0.0.1:52011}</c>. Connect names
/// the connector protocol + "_" + host + configured port; the last braces hold the bound address.
/// </summary>
public static class RestEndpoint
{
    static readonly Regex Jetty = new(@"Started (https?)_[^@\s]*@[0-9a-fA-F]+\{[^}]*\}\{([^}:]*):(\d+)\}");
    static readonly Regex Listener = new(@"^(https?)://([^:/]*):(\d+)", RegexOptions.IgnoreCase);

    /// <summary>The REST address in a Connect log line, or null.</summary>
    public static Uri? FromLogLine(string line)
    {
        var m = Jetty.Match(line);
        return m.Success ? Make(m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>The first listener in the worker's "listeners", or null when its port is 0 or missing.</summary>
    public static Uri? FromListeners(string? listeners)
    {
        if (string.IsNullOrWhiteSpace(listeners))
            return null;
        var m = Listener.Match(listeners.Split(',')[0].Trim());
        if (!m.Success)
            return null;
        var port = int.Parse(m.Groups[3].Value);
        return port == 0 ? null : Make(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, port);
    }

    static Uri Make(string scheme, string host, int port)
    {
        if (host is "" or "0.0.0.0")
            host = "127.0.0.1";
        return new Uri($"{scheme}://{host}:{port}");
    }
}
