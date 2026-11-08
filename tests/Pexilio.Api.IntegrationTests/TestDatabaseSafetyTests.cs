using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class TestDatabaseSafetyTests : IAsyncLifetime
{
    private HealthApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new HealthApiFactory(LocalEnvironment.TestConnectionString());
        await TestDatabase.PrepareAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_connected_database_is_marked_disposable()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var connection = database.Database.GetDbConnection();
        var snapshot = TestDatabaseIdentity.Parse(await TestDatabase.ReadLiveIdentityTextAsync(connection, transaction: null));

        Assert.Null(TestDatabaseIdentity.Refusal(snapshot));
        Assert.Equal(TestDatabaseIdentity.Marker, snapshot.Marker);
        Assert.Equal("127.0.0.1", snapshot.ServerAddress);
        Assert.True(TestDatabaseNames.IsDisposable(snapshot.DatabaseName));
        Assert.NotEqual(TestDatabaseNames.Shared, snapshot.DatabaseName);
    }

    [Fact]
    public async Task The_direct_sql_cleanup_gate_reads_the_connected_marker()
    {
        await using var connection = new NpgsqlConnection(LocalEnvironment.TestConnectionString());
        await connection.OpenAsync();
        await TestDatabase.EnsureConnectedDisposableAsync(connection);
        await using var command = new NpgsqlCommand(TestDatabaseIdentity.IdentitySql, connection);
        var snapshot = TestDatabaseIdentity.Parse((string)(await command.ExecuteScalarAsync())!);

        Assert.Null(TestDatabaseIdentity.Refusal(snapshot));
        Assert.Equal(TestDatabaseIdentity.Marker, snapshot.Marker);
        Assert.True(TestDatabaseNames.IsDisposable(snapshot.DatabaseName));
    }
}
