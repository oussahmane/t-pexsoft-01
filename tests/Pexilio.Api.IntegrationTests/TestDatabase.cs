using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pexilio.Domain.Catalog;
using Pexilio.Domain.Company;
using Pexilio.Domain.Customers;
using Pexilio.Domain.Purchasing;
using Pexilio.Domain.Sales;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Auth;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

internal static class TestDatabase
{
    public const string SigningKey = "integration-test-signing-key-32b";

    public static async Task PrepareAsync(HealthApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        // Compiled migration ids only. This does not connect or apply schema changes.
        var expected = database.Database.GetMigrations().ToArray();
        await PrepareAsync(
            new EfTestDatabaseCommands(database),
            Environment.GetEnvironmentVariable(TestMigrationMode.VariableName),
            expected);
    }

    internal static async Task PrepareAsync(
        ITestDatabaseCommands commands,
        string? migrationMode,
        IReadOnlyCollection<string> expectedMigrationIds)
    {
        var mode = TestMigrationMode.Resolve(migrationMode);
        var identity = TestDatabaseIdentity.Parse(await commands.ReadIdentityAsync());
        TestDatabaseIdentity.EnsurePrepareAllowed(identity);

        if (mode == TestMigrationMode.VerifyHistoryOnly)
        {
            await TestMigrationHistory.EnsureVerifiedAsync(
                commands.ReadAppliedMigrationIdsAsync,
                expectedMigrationIds);
        }
        else if (mode == TestMigrationMode.Apply)
        {
            await commands.ApplyMigrationsAsync();
        }
        else
        {
            throw new InvalidOperationException(
                $"Refusing to prepare pexilio_test because {TestMigrationMode.VariableName} resolved to '{mode}'.");
        }

        await commands.ClearApplicationDataAsync();
    }

    public static async Task ClearApplicationDataAsync(PexilioDbContext database)
    {
        await using var purge = await database.Database.BeginTransactionAsync();
        var connection = database.Database.GetDbConnection();
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        await EnsureConnectedDisposableAsync(connection, transaction);

        // Append-only triggers block DELETE and TRUNCATE for every role, including the table owner.
        // The identity query above runs inside this transaction before the first delete. PostgreSQL
        // rolls ALTER TABLE DISABLE TRIGGER back with the transaction, so a failure leaves every
        // trigger enabled and restores the rows.
        await database.Set<SaleIdempotency>().ExecuteDeleteAsync();
        await database.Set<PurchaseIdempotency>().ExecuteDeleteAsync();
        await database.Set<StockIdempotency>().ExecuteDeleteAsync();
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_refund DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_return_line DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_return DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_payment DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_line DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.purchase_receipt_line DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.purchase_receipt DISABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.stock_movement DISABLE TRIGGER USER""");
        await database.Set<SaleRefund>().ExecuteDeleteAsync();
        await database.Set<SaleReturnLine>().ExecuteDeleteAsync();
        await database.Set<SaleReturn>().ExecuteDeleteAsync();
        await database.Set<SalePayment>().ExecuteDeleteAsync();
        await database.Set<SaleLine>().ExecuteDeleteAsync();
        await database.Set<Sale>().ExecuteDeleteAsync();
        await database.Set<PurchaseReceiptLine>().ExecuteDeleteAsync();
        await database.Set<PurchaseReceipt>().ExecuteDeleteAsync();
        await database.Set<StockMovement>().ExecuteDeleteAsync();
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_refund ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_return_line ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_return ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_payment ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale_line ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.sale ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.purchase_receipt_line ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.purchase_receipt ENABLE TRIGGER USER""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE pexilio.stock_movement ENABLE TRIGGER USER""");

        await database.Set<PurchaseLine>().ExecuteDeleteAsync();
        await database.Set<Purchase>().ExecuteDeleteAsync();
        await database.Set<Supplier>().ExecuteDeleteAsync();
        await database.Set<StockBalance>().ExecuteDeleteAsync();
        await database.Set<StockLocation>().ExecuteDeleteAsync();
        await database.Set<Product>().ExecuteDeleteAsync();
        await database.Database.ExecuteSqlRawAsync("""UPDATE pexilio.category SET "ParentId" = NULL""");
        await database.Set<Category>().ExecuteDeleteAsync();
        await database.RefreshTokens.ExecuteDeleteAsync();
        await database.Set<IdentityUserLogin<Guid>>().ExecuteDeleteAsync();
        await database.Set<IdentityUserToken<Guid>>().ExecuteDeleteAsync();
        await database.Set<IdentityUserClaim<Guid>>().ExecuteDeleteAsync();
        await database.Set<IdentityUserRole<Guid>>().ExecuteDeleteAsync();
        await database.Set<IdentityRoleClaim<Guid>>().ExecuteDeleteAsync();
        await database.Set<AppUser>().ExecuteDeleteAsync();
        await database.Set<AppRole>().ExecuteDeleteAsync();
        await database.Set<TaxRate>().ExecuteDeleteAsync();
        await database.Set<DocumentCounter>().ExecuteDeleteAsync();
        await database.Set<CustomerIdempotency>().ExecuteDeleteAsync();
        await database.Set<Customer>().ExecuteDeleteAsync();
        await database.Set<Company>().ExecuteDeleteAsync();
        await purge.CommitAsync();
    }

    internal static Task EnsureConnectedDisposableAsync(DbConnection connection) =>
        EnsureConnectedDisposableAsync(connection, transaction: null);

    internal static async Task<string> ReadLiveIdentityTextAsync(DbConnection connection, DbTransaction? transaction)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TestDatabaseIdentity.IdentitySql;
        object? value;
        try
        {
            value = await command.ExecuteScalarAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Refusing to delete rows because the database identity could not be verified.",
                exception);
        }

        if (value is not string text || text.Length == 0)
        {
            throw new InvalidOperationException(
                "Refusing to delete rows because the database identity could not be verified.");
        }

        return text;
    }

    private static async Task EnsureConnectedDisposableAsync(DbConnection connection, DbTransaction? transaction)
    {
        var text = await ReadLiveIdentityTextAsync(connection, transaction);
        TestDatabaseIdentity.EnsureDeleteAllowed(TestDatabaseIdentity.Parse(text));
    }

    private sealed class EfTestDatabaseCommands : ITestDatabaseCommands
    {
        private readonly PexilioDbContext _database;

        public EfTestDatabaseCommands(PexilioDbContext database)
        {
            _database = database;
        }

        public async Task<string> ReadIdentityAsync()
        {
            var connection = _database.Database.GetDbConnection();
            return await ReadLiveIdentityTextAsync(connection, _database.Database.CurrentTransaction?.GetDbTransaction());
        }

        public async Task ApplyMigrationsAsync()
        {
            await _database.Database.MigrateAsync();
        }

        public async Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync()
        {
            try
            {
                var connection = _database.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                await using (var exists = connection.CreateCommand())
                {
                    exists.CommandText = TestMigrationHistory.HistoryTableExistsSql;
                    var regclass = await exists.ExecuteScalarAsync();
                    TestMigrationHistory.RequireHistoryTable(regclass);
                }

                var ids = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT "MigrationId"
                        FROM pexilio."__EFMigrationsHistory"
                        ORDER BY "MigrationId"
                        """;
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (reader.IsDBNull(0))
                        {
                            throw new TestMigrationHistoryUnavailableException(
                                "pexilio.\"__EFMigrationsHistory\" returned a missing MigrationId.");
                        }

                        ids.Add(reader.GetString(0));
                    }
                }

                return ids;
            }
            catch (TestMigrationHistoryUnavailableException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TestMigrationHistoryUnavailableException(
                    "pexilio.\"__EFMigrationsHistory\" could not be read.",
                    exception);
            }
        }

        public Task ClearApplicationDataAsync() => TestDatabase.ClearApplicationDataAsync(_database);
    }
}
