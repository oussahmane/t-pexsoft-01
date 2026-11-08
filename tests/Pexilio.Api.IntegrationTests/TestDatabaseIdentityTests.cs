namespace Pexilio.Api.IntegrationTests;

public sealed class TestDatabaseIdentityTests
{
    private const string AcceptedName = "pexilio_test_harness";

    private static TestDatabaseIdentity.Snapshot Identity(string name, string address, string port, string marker) =>
        new(name, address, port, marker);

    [Fact]
    public void Identity_sql_reads_the_connected_database_rather_than_a_connection_string()
    {
        Assert.Contains("current_database()", TestDatabaseIdentity.IdentitySql, StringComparison.Ordinal);
        Assert.Contains("inet_server_addr()", TestDatabaseIdentity.IdentitySql, StringComparison.Ordinal);
        Assert.Contains("inet_server_port()", TestDatabaseIdentity.IdentitySql, StringComparison.Ordinal);
        Assert.Contains("shobj_description(d.oid, 'pg_database')", TestDatabaseIdentity.IdentitySql, StringComparison.Ordinal);
        Assert.Contains("d.datname = current_database()", TestDatabaseIdentity.IdentitySql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pexilio_test", "name")]
    [InlineData("pexilio_dev", "name")]
    [InlineData("gropro", "name")]
    [InlineData("retail_db", "name")]
    [InlineData("pexilio_testing", "name")]
    [InlineData("Pexilio_test_harness", "name")]
    [InlineData("", "name")]
    public void Unauthorized_names_are_refused_even_with_the_marker(string name, string reason)
    {
        var snapshot = Identity(name, "127.0.0.1", "55432", TestDatabaseIdentity.Marker);

        Assert.Equal(reason, TestDatabaseIdentity.Refusal(snapshot));
        AssertRefused(snapshot);
    }

    [Fact]
    public void A_backup_name_without_the_marker_is_refused()
    {
        var snapshot = Identity("pexilio_test_backup", "127.0.0.1", "55432", "");

        Assert.Equal("marker", TestDatabaseIdentity.Refusal(snapshot));
        AssertRefused(snapshot);
    }

    [Theory]
    [InlineData(AcceptedName, "127.0.0.1", "55432", "")]
    [InlineData(AcceptedName, "127.0.0.1", "55432", "pexilio_test")]
    [InlineData(AcceptedName, "127.0.0.1", "55432", "pexilio:integration-disposable ")]
    public void An_accepted_name_without_the_exact_marker_is_refused(string name, string address, string port, string marker)
    {
        var snapshot = Identity(name, address, port, marker);

        Assert.Equal("marker", TestDatabaseIdentity.Refusal(snapshot));
        AssertRefused(snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("10.0.0.8")]
    [InlineData("127.0.0.1/32")]
    public void A_marker_on_a_non_loopback_connection_is_refused(string address)
    {
        var snapshot = Identity(AcceptedName, address, "55432", TestDatabaseIdentity.Marker);

        Assert.Equal("address", TestDatabaseIdentity.Refusal(snapshot));
        AssertRefused(snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5432abc")]
    [InlineData("-1")]
    public void A_missing_or_invalid_server_port_is_refused(string port)
    {
        var snapshot = Identity(AcceptedName, "127.0.0.1", port, TestDatabaseIdentity.Marker);

        Assert.Equal("port", TestDatabaseIdentity.Refusal(snapshot));
        AssertRefused(snapshot);
    }

    [Fact]
    public void An_accepted_name_with_the_marker_on_loopback_is_allowed()
    {
        var snapshot = Identity(AcceptedName, "127.0.0.1", "55432", TestDatabaseIdentity.Marker);

        Assert.Null(TestDatabaseIdentity.Refusal(snapshot));
        TestDatabaseIdentity.EnsurePrepareAllowed(snapshot);
        TestDatabaseIdentity.EnsureDeleteAllowed(snapshot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pexilio_test_harness")]
    [InlineData("pexilio_test_harness|127.0.0.1|55432")]
    [InlineData("pexilio_test_harness|127.0.0.1|55432|pexilio:integration-disposable|extra")]
    public void An_unreadable_identity_fails_closed(string? text)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TestDatabaseIdentity.Parse(text));

        Assert.Contains("could not be verified", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertRefused(TestDatabaseIdentity.Snapshot snapshot)
    {
        var prepare = Assert.Throws<InvalidOperationException>(() => TestDatabaseIdentity.EnsurePrepareAllowed(snapshot));
        var delete = Assert.Throws<InvalidOperationException>(() => TestDatabaseIdentity.EnsureDeleteAllowed(snapshot));

        Assert.Contains($"Refusing to prepare database '{snapshot.DatabaseName}'", prepare.Message, StringComparison.Ordinal);
        Assert.Contains($"Refusing to delete rows from database '{snapshot.DatabaseName}'", delete.Message, StringComparison.Ordinal);
    }
}
