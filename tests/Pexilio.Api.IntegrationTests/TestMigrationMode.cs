namespace Pexilio.Api.IntegrationTests;

/// <summary>
/// Selects how <see cref="TestDatabase.PrepareAsync"/> treats schema migrations.
/// Unset, blank, or <see cref="Apply"/> keeps the existing <c>MigrateAsync</c> behavior.
/// No-migration mode is enabled only by the exact environment value
/// <c>PEXILIO_TEST_MIGRATION_MODE=verify-history-only</c>.
/// Any other value fails before a database operation.
/// </summary>
internal static class TestMigrationMode
{
    public const string VariableName = "PEXILIO_TEST_MIGRATION_MODE";

    public const string Apply = "apply";

    public const string VerifyHistoryOnly = "verify-history-only";

    public static string Resolve(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return Apply;
        }

        if (configuredValue == Apply || configuredValue == VerifyHistoryOnly)
        {
            return configuredValue;
        }

        throw new InvalidOperationException(
            $"{VariableName} must be unset, '{Apply}', or '{VerifyHistoryOnly}'. The value '{configuredValue}' is not recognized.");
    }
}
