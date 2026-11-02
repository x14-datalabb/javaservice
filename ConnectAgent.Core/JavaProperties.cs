using System.Text;
using System.Text.RegularExpressions;

namespace ConnectAgent.Core;

/// <summary>
/// Reads java.util.Properties files (the Kafka Connect worker and source files): comments,
/// continuation lines, '=' / ':' / whitespace separators and backslash escapes. Values keep
/// trailing whitespace, as Java does.
/// </summary>
public static class JavaProperties
{
    static readonly Regex KeyValue = new(@"^((?:\\.|[^\\=:\s])*)\s*[=:\s]\s*(.*)$", RegexOptions.Singleline);
    static readonly Regex Escape = new(@"\\(u[0-9a-fA-F]{4}|.)", RegexOptions.Singleline);

    /// <summary>Java reads .properties files as ISO-8859-1.</summary>
    public static Dictionary<string, string> Load(string path) =>
        Parse(File.ReadAllText(path, Encoding.Latin1));

    public static Dictionary<string, string> Parse(string text)
    {
        var logical = new List<string>();
        string? current = null;
        foreach (var raw in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.TrimStart(' ', '\t', '\f');
            if (current is null && (line.Length == 0 || line[0] == '#' || line[0] == '!'))
                continue;
            var trailing = line.Length - line.TrimEnd('\\').Length;
            if (trailing % 2 == 1)
            {
                current = (current ?? "") + line[..^1];
                continue;
            }
            logical.Add((current ?? "") + line);
            current = null;
        }
        if (current is not null)
            logical.Add(current);

        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in logical)
        {
            var m = KeyValue.Match(line);
            var (key, value) = m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (line, "");
            props[Unescape(key)] = Unescape(value);
        }
        return props;
    }

    static string Unescape(string s) => Escape.Replace(s, m =>
    {
        var e = m.Groups[1].Value;
        return e switch
        {
            "t" => "\t",
            "n" => "\n",
            "r" => "\r",
            "f" => "\f",
            _ when e.Length == 5 => ((char)Convert.ToInt32(e[1..], 16)).ToString(),
            _ => e,
        };
    });
}
