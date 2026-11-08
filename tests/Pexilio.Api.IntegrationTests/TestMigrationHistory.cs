namespace Pexilio.Api.IntegrationTests;

internal static class TestMigrationHistory
{
    internal const string HistoryTableExistsSql =
        """SELECT to_regclass('pexilio."__EFMigrationsHistory"')::text""";

    public static string RequireHistoryTable(object? locatedTable)
    {
        if (locatedTable is null or DBNull)
        {
            throw new TestMigrationHistoryUnavailableException(
                "pexilio.\"__EFMigrationsHistory\" does not exist.");
        }

        if (locatedTable is not string name || string.IsNullOrWhiteSpace(name))
        {
            throw new TestMigrationHistoryUnavailableException(
                "pexilio.\"__EFMigrationsHistory\" could not be read.");
        }

        return name;
    }

    public static async Task EnsureVerifiedAsync(
        Func<Task<IReadOnlyList<string>>> readAppliedMigrationIds,
        IReadOnlyCollection<string> expectedMigrationIds)
    {
        IReadOnlyList<string> applied;
        try
        {
            applied = await readAppliedMigrationIds();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Refusing to prepare pexilio_test because pexilio.\"__EFMigrationsHistory\" is unavailable. "
                + exception.Message,
                exception);
        }

        EnsureExactMatch(expectedMigrationIds, applied);
    }

    public static void EnsureExactMatch(
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> applied)
    {
        var expectedOrdered = Order(expected);
        var appliedOrdered = Order(applied);
        if (expectedOrdered.SequenceEqual(appliedOrdered, StringComparer.Ordinal))
        {
            return;
        }

        var missing = expectedOrdered.Except(appliedOrdered, StringComparer.Ordinal).ToArray();
        var unexpected = appliedOrdered.Except(expectedOrdered, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && unexpected.Length == 0)
        {
            throw new InvalidOperationException(
                "Refusing to prepare pexilio_test because pexilio.\"__EFMigrationsHistory\" does not exactly match the repository migrations. "
                + $"Expected {expectedOrdered.Length} ids and found {appliedOrdered.Length}.");
        }

        throw new InvalidOperationException(
            "Refusing to prepare pexilio_test because pexilio.\"__EFMigrationsHistory\" does not match the repository migrations. "
            + $"Missing: {Format(missing)}. Unexpected: {Format(unexpected)}.");
    }

    private static string[] Order(IReadOnlyCollection<string> ids)
    {
        if (ids.Any(static id => string.IsNullOrWhiteSpace(id)))
        {
            throw new InvalidOperationException(
                "Refusing to prepare pexilio_test because a migration id was blank.");
        }

        return ids.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
    }

    private static string Format(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "(none)" : string.Join(", ", ids);
}
