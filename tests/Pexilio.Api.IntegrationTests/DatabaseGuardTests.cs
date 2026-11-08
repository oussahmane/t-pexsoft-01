namespace Pexilio.Api.IntegrationTests;

public sealed class DatabaseGuardTests
{
    [Fact]
    public void Rejects_the_development_database()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseGuard.EnsureTestDatabase(
                "Host=127.0.0.1;Port=5432;Database=pexilio_dev;Username=pexilio_dev;Password=not-used"));

        Assert.Contains("pexilio_test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_unrelated_database()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseGuard.EnsureTestDatabase(
                "Host=127.0.0.1;Database=gropro;Username=pexilio_dev;Password=not-used"));
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseGuard.EnsureTestDatabase(
                "Host=127.0.0.1;Database=retail_db;Username=pexilio_dev;Password=not-used"));
    }

    [Fact]
    public void Accepts_a_disposable_loopback_database()
    {
        DatabaseGuard.EnsureTestDatabase(
            "Host=127.0.0.1;Port=5432;Database=pexilio_test_phase553;Username=pexilio_dev;Password=not-used");
    }

    [Fact]
    public void Rejects_a_remote_host()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseGuard.EnsureTestDatabase(
                "Host=10.0.0.8;Database=pexilio_test;Username=pexilio_dev;Password=not-used"));
    }
}
