namespace Pexilio.Api.IntegrationTests;

public sealed class TestDatabasePreparationTests
{
    private static readonly string[] RepositoryMigrations =
    [
        "20261027231938_InitialCreate",
        "20261028143621_CompanyAndAccounts",
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(TestMigrationMode.Apply)]
    public async Task Normal_mode_calls_MigrateAsync_and_then_clears_application_data(string? migrationMode)
    {
        var database = new FakeDatabaseCommands();

        await TestDatabase.PrepareAsync(database, migrationMode, RepositoryMigrations);

        Assert.Equal(["read-identity", "migrate", "clear"], database.Calls);
    }

    [Fact]
    public async Task No_migration_mode_does_not_invoke_MigrateAsync()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds = RepositoryMigrations,
            RejectMigrationApplication = true,
        };

        await TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations);

        Assert.DoesNotContain("migrate", database.Calls);
    }

    [Fact]
    public async Task Exact_migration_history_match_permits_preparation()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds = ["20261028143621_CompanyAndAccounts", "20261027231938_InitialCreate"],
            RejectMigrationApplication = true,
        };

        await TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations);

        Assert.Equal(["read-identity", "read-history", "clear"], database.Calls);
    }

    [Fact]
    public async Task Missing_migration_rejects_preparation_before_data_cleanup()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds = ["20261027231938_InitialCreate"],
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations));

        Assert.Contains("Missing: 20261028143621_CompanyAndAccounts", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unexpected: (none)", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity", "read-history"], database.Calls);
    }

    [Fact]
    public async Task Unexpected_migration_rejects_preparation_before_data_cleanup()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds =
            [
                "20261027231938_InitialCreate",
                "20261028143621_CompanyAndAccounts",
                "20269999999999_NotInThisRepository",
            ],
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations));

        Assert.Contains("Missing: (none)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unexpected: 20269999999999_NotInThisRepository", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity", "read-history"], database.Calls);
    }

    [Theory]
    [InlineData(null, "pexilio_dev")]
    [InlineData(TestMigrationMode.Apply, "pexilio_dev")]
    [InlineData(TestMigrationMode.VerifyHistoryOnly, "pexilio_dev")]
    [InlineData(TestMigrationMode.VerifyHistoryOnly, "gropro")]
    [InlineData(TestMigrationMode.VerifyHistoryOnly, "retail_db")]
    [InlineData(TestMigrationMode.Apply, "pexilio_test")]
    [InlineData(TestMigrationMode.VerifyHistoryOnly, "pexilio_test")]
    [InlineData(TestMigrationMode.Apply, "Pexilio_test")]
    [InlineData(TestMigrationMode.VerifyHistoryOnly, "")]
    public async Task Wrong_database_name_always_rejects_preparation(string? migrationMode, string databaseName)
    {
        var database = new FakeDatabaseCommands
        {
            DatabaseName = databaseName,
            AppliedMigrationIds = RepositoryMigrations,
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, migrationMode, RepositoryMigrations));

        Assert.Contains($"Refusing to prepare database '{databaseName}'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity"], database.Calls);
    }

    [Fact]
    public void History_table_lookup_reads_to_regclass_as_text()
    {
        Assert.Equal(
            """SELECT to_regclass('pexilio."__EFMigrationsHistory"')::text""",
            TestMigrationHistory.HistoryTableExistsSql);
    }

    [Fact]
    public void Existing_history_table_text_is_accepted()
    {
        var located = TestMigrationHistory.RequireHistoryTable("pexilio.\"__EFMigrationsHistory\"");

        Assert.Equal("pexilio.\"__EFMigrationsHistory\"", located);
    }

    [Fact]
    public async Task Existing_history_table_permits_preparation_when_migrations_match()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds = RepositoryMigrations,
            HistoryTableLocator = "pexilio.\"__EFMigrationsHistory\"",
            RejectMigrationApplication = true,
        };

        await TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations);

        Assert.Equal(["read-identity", "read-history", "clear"], database.Calls);
    }

    [Theory]
    [InlineData(null)]
    public void Missing_history_table_null_is_rejected(object? locatedTable)
    {
        var exception = Assert.Throws<TestMigrationHistoryUnavailableException>(() =>
            TestMigrationHistory.RequireHistoryTable(locatedTable));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_history_table_database_null_is_rejected()
    {
        var exception = Assert.Throws<TestMigrationHistoryUnavailableException>(() =>
            TestMigrationHistory.RequireHistoryTable(DBNull.Value));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_history_table_scalar_rejects_preparation_before_data_cleanup()
    {
        var database = new FakeDatabaseCommands
        {
            AppliedMigrationIds = RepositoryMigrations,
            HistoryTableLocator = DBNull.Value,
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations));

        Assert.Contains("unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity", "read-history"], database.Calls);
        Assert.IsType<TestMigrationHistoryUnavailableException>(exception.InnerException);
    }

    [Fact]
    public async Task Missing_migration_history_table_rejects_preparation_before_data_cleanup()
    {
        var database = new FakeDatabaseCommands
        {
            HistoryException = new TestMigrationHistoryUnavailableException(
                "pexilio.\"__EFMigrationsHistory\" does not exist."),
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations));

        Assert.Contains("unavailable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity", "read-history"], database.Calls);
        Assert.IsType<TestMigrationHistoryUnavailableException>(exception.InnerException);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("skip")]
    [InlineData("VERIFY-HISTORY-ONLY")]
    public async Task Unrecognized_migration_mode_fails_before_any_database_operation(string migrationMode)
    {
        var database = new FakeDatabaseCommands { RejectMigrationApplication = true };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, migrationMode, RepositoryMigrations));

        Assert.Contains(TestMigrationMode.VariableName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestMigrationMode.VerifyHistoryOnly, exception.Message, StringComparison.Ordinal);
        Assert.Empty(database.Calls);
    }

    [Theory]
    [InlineData("pexilio_test", "127.0.0.1", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("pexilio_dev", "127.0.0.1", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("gropro", "127.0.0.1", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("retail_db", "127.0.0.1", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("pexilio_testing", "127.0.0.1", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("pexilio_test_backup", "127.0.0.1", "55432", "")]
    [InlineData("pexilio_test_harness", "127.0.0.1", "55432", "")]
    [InlineData("pexilio_test_harness", "10.0.0.8", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("pexilio_test_harness", "", "55432", TestDatabaseIdentity.Marker)]
    [InlineData("pexilio_test_harness", "127.0.0.1", "", TestDatabaseIdentity.Marker)]
    public async Task Unverified_identity_rejects_preparation_before_cleanup(
        string databaseName,
        string serverAddress,
        string serverPort,
        string marker)
    {
        var database = new FakeDatabaseCommands
        {
            DatabaseName = databaseName,
            ServerAddress = serverAddress,
            Port = serverPort,
            Marker = marker,
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.VerifyHistoryOnly, RepositoryMigrations));

        Assert.Contains($"Refusing to prepare database '{databaseName}'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity"], database.Calls);
    }

    [Fact]
    public async Task An_unreadable_identity_rejects_preparation_before_cleanup()
    {
        var database = new FakeDatabaseCommands
        {
            IdentityText = "pexilio_test_harness|127.0.0.1",
            RejectMigrationApplication = true,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabase.PrepareAsync(database, TestMigrationMode.Apply, RepositoryMigrations));

        Assert.Contains("could not be verified", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read-identity"], database.Calls);
    }

    private sealed class FakeDatabaseCommands : ITestDatabaseCommands
    {
        public string DatabaseName { get; init; } = "pexilio_test_harness";

        public string ServerAddress { get; init; } = "127.0.0.1";

        public string Port { get; init; } = "55432";

        public string Marker { get; init; } = TestDatabaseIdentity.Marker;

        public string? IdentityText { get; init; }

        public IReadOnlyList<string> AppliedMigrationIds { get; init; } = [];

        public Exception? HistoryException { get; init; }

        public object? HistoryTableLocator { get; init; } = "pexilio.\"__EFMigrationsHistory\"";

        public bool RejectMigrationApplication { get; init; }

        public List<string> Calls { get; } = [];

        public Task<string> ReadIdentityAsync()
        {
            Calls.Add("read-identity");
            return Task.FromResult(IdentityText ?? $"{DatabaseName}|{ServerAddress}|{Port}|{Marker}");
        }

        public Task ApplyMigrationsAsync()
        {
            Calls.Add("migrate");
            if (RejectMigrationApplication)
            {
                throw new InvalidOperationException("MigrateAsync must not be called.");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync()
        {
            Calls.Add("read-history");
            if (HistoryException is not null)
            {
                throw HistoryException;
            }

            TestMigrationHistory.RequireHistoryTable(HistoryTableLocator);
            return Task.FromResult(AppliedMigrationIds);
        }

        public Task ClearApplicationDataAsync()
        {
            Calls.Add("clear");
            return Task.CompletedTask;
        }
    }
}
