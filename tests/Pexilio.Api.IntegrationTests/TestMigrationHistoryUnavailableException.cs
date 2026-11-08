namespace Pexilio.Api.IntegrationTests;

internal sealed class TestMigrationHistoryUnavailableException : Exception
{
    public TestMigrationHistoryUnavailableException(string message)
        : base(message)
    {
    }

    public TestMigrationHistoryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
