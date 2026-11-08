namespace Pexilio.Api.IntegrationTests;

public sealed class ManualTimeProviderTests
{
    [Fact]
    public void MoveTo_makes_GetUtcNow_return_that_instant()
    {
        var clock = new ManualTimeProvider();
        var instant = new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero);

        clock.MoveTo(instant);

        Assert.Equal(instant, clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);
    }

    [Fact]
    public void The_initial_instant_is_utc()
    {
        var before = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(clock.GetUtcNow(), before, after);
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow().Offset);
    }
}
