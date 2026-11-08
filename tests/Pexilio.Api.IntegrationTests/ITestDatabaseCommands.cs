namespace Pexilio.Api.IntegrationTests;

internal interface ITestDatabaseCommands
{
    Task<string> ReadIdentityAsync();

    Task ApplyMigrationsAsync();

    Task<IReadOnlyList<string>> ReadAppliedMigrationIdsAsync();

    Task ClearApplicationDataAsync();
}
