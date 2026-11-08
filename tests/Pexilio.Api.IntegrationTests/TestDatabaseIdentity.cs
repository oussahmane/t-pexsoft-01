using System.Globalization;

namespace Pexilio.Api.IntegrationTests;

internal static class TestDatabaseIdentity
{
    public const string Marker = "pexilio:integration-disposable";

    public const string IdentitySql = """
        SELECT current_database()
            || '|' || COALESCE(host(inet_server_addr()), '')
            || '|' || COALESCE(inet_server_port()::text, '')
            || '|' || COALESCE((
                SELECT shobj_description(d.oid, 'pg_database')
                FROM pg_database AS d
                WHERE d.datname = current_database()
            ), '')
        """;

    public readonly record struct Snapshot(string DatabaseName, string ServerAddress, string ServerPort, string Marker);

    public static Snapshot Parse(string? text)
    {
        var parts = text?.Split('|') ?? [];
        if (parts.Length != 4)
        {
            throw new InvalidOperationException(
                "Refusing to delete rows because the database identity could not be verified.");
        }

        return new Snapshot(parts[0], parts[1], parts[2], parts[3]);
    }

    public static string? Refusal(Snapshot snapshot)
    {
        if (!TestDatabaseNames.IsDisposable(snapshot.DatabaseName))
        {
            return "name";
        }

        if (!string.Equals(snapshot.ServerAddress, "127.0.0.1", StringComparison.Ordinal))
        {
            return "address";
        }

        if (!int.TryParse(snapshot.ServerPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return "port";
        }

        if (!string.Equals(snapshot.Marker, Marker, StringComparison.Ordinal))
        {
            return "marker";
        }

        return null;
    }

    public static void EnsurePrepareAllowed(Snapshot snapshot)
    {
        if (Refusal(snapshot) is not null)
        {
            throw new InvalidOperationException($"Refusing to prepare database '{snapshot.DatabaseName}'.");
        }
    }

    public static void EnsureDeleteAllowed(Snapshot snapshot)
    {
        if (Refusal(snapshot) is not null)
        {
            throw new InvalidOperationException($"Refusing to delete rows from database '{snapshot.DatabaseName}'.");
        }
    }
}
