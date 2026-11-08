namespace Pexilio.Api.IntegrationTests;

/// <summary>
/// Test clock for statement fixtures. Sales and payments take their timestamps from
/// <see cref="TimeProvider.GetUtcNow"/> at insert time, so the clock is moved before
/// the API call. Append-only triggers still reject later updates.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void MoveTo(DateTimeOffset utcNow) => _utcNow = utcNow;
}
