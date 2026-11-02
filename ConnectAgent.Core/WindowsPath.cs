namespace ConnectAgent.Core;

/// <summary>Windows paths compared as Windows does: case-insensitive, either slash.</summary>
public static class WindowsPath
{
    /// <summary>
    /// True when <paramref name="path"/> lies inside <paramref name="directory"/>, at any depth.
    /// Both must be full paths. The directory itself and a sibling that merely starts with the
    /// same name (C:\X14\Sorcerer2) are not inside.
    /// </summary>
    public static bool IsUnder(string directory, string path)
    {
        var dir = Normalize(directory).TrimEnd('\\');
        return dir.Length > 0 && Normalize(path).StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a drive or share root such as C:\, where "inside" would mean everything.</summary>
    public static bool IsRoot(string directory)
    {
        var dir = Normalize(directory).TrimEnd('\\');
        return dir.Length <= 2 || (dir.StartsWith(@"\\", StringComparison.Ordinal) && dir.Count(c => c == '\\') <= 3);
    }

    static string Normalize(string p)
    {
        p = p.Replace('/', '\\');
        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }
}
