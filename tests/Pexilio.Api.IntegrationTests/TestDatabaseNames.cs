namespace Pexilio.Api.IntegrationTests;

internal static class TestDatabaseNames
{
    public const string Shared = "pexilio_test";

    public const string DisposablePrefix = "pexilio_test_";

    public static bool IsShared(string name) => string.Equals(name, Shared, StringComparison.Ordinal);

    public static bool IsDisposable(string name)
    {
        if (!name.StartsWith(DisposablePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[DisposablePrefix.Length..];
        return suffix.Length is >= 1 and <= 32
            && suffix.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));
    }
}
