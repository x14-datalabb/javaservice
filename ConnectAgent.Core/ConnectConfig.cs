using System.Security.Cryptography.X509Certificates;

namespace ConnectAgent.Core;

/// <summary>
/// config\connect.cfg: the worker file on the first line, then one file per source, relative to
/// the install directory (the parent of bin).
/// </summary>
public sealed record ConnectConfig(string Worker, IReadOnlyList<string> Sources)
{
    public static ConnectConfig Read(string connectCfg, string baseDir)
    {
        var paths = File.ReadAllLines(connectCfg)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(p => Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(baseDir, p)))
            .ToList();
        if (paths.Count == 0)
            throw new InvalidDataException($"{connectCfg} lists no files");
        return new ConnectConfig(paths[0], paths.Skip(1).Where(File.Exists).ToList());
    }

    /// <summary>The source database of each connector: name -> (host, port), from the source files.</summary>
    public static Dictionary<string, (string Host, int Port)> SourceDatabases(IEnumerable<string> sourceFiles)
    {
        var result = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var file in sourceFiles)
        {
            var p = JavaProperties.Load(file);
            if (!p.TryGetValue("name", out var name) || name.Trim().Length == 0)
                continue;
            if (!p.TryGetValue("database.hostname", out var host) || host.Trim().Length == 0)
                continue;
            var port = p.TryGetValue("database.port", out var s) && int.TryParse(s.Trim(), out var n) ? n : 1433;
            result[name.Trim()] = (host.Trim(), port);
        }
        return result;
    }

    /// <summary>
    /// The worker's own certificate and key (ssl.keystore.location, PKCS12). The REST listener
    /// presents it and requires a client certificate from the same CA, so the health check
    /// authenticates with it and accepts only that server certificate.
    /// </summary>
    public static X509Certificate2 WorkerCertificate(IReadOnlyDictionary<string, string> worker, string baseDir)
    {
        if (!worker.TryGetValue("ssl.keystore.location", out var location) || location.Trim().Length == 0)
            throw new InvalidDataException("the worker file has no ssl.keystore.location");
        location = location.Trim();
        if (!Path.IsPathRooted(location))
            location = Path.Combine(baseDir, location);
        worker.TryGetValue("ssl.keystore.password", out var password);
        return new X509Certificate2(location, password, X509KeyStorageFlags.DefaultKeySet);
    }
}
