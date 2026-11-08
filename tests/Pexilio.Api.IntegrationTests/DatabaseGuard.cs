namespace Pexilio.Api.IntegrationTests;

internal static class DatabaseGuard
{
    public static void EnsureTestDatabase(string connectionString)
    {
        var fields = Parse(connectionString);
        var host = fields.GetValueOrDefault("host") ?? "";
        var database = fields.GetValueOrDefault("database") ?? "";
        if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Integration tests refuse a non-loopback PostgreSQL host.");
        }

        if (!TestDatabaseNames.IsShared(database) && !TestDatabaseNames.IsDisposable(database))
        {
            throw new InvalidOperationException(
                "Integration tests refuse to use a database other than pexilio_test or a disposable pexilio_test_<suffix> database.");
        }
    }

    private static Dictionary<string, string> Parse(string connectionString)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            fields[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }

        return fields;
    }
}
