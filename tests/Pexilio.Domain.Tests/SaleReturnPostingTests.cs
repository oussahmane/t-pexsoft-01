using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleReturnPostingTests
{
    [Fact]
    public void A_blank_reason_is_rejected()
    {
        var accepted = SaleReturnPosting.TryNormalize("   ", [(Guid.NewGuid(), "1.0000")], null, out _, out var errors);

        Assert.False(accepted);
        Assert.Contains(errors, error => error.Field == "reason" && error.Code == "reason.required");
    }

    [Fact]
    public void A_reason_of_200_characters_is_accepted_and_201_is_rejected()
    {
        var lineId = Guid.NewGuid();
        var accepted = SaleReturnPosting.TryNormalize(new string('a', 200), [(lineId, "1.0000")], null, out var draft, out _);
        var rejected = SaleReturnPosting.TryNormalize(new string('a', 201), [(lineId, "1.0000")], null, out _, out var errors);

        Assert.True(accepted);
        Assert.Equal(200, draft.Reason.Length);
        Assert.False(rejected);
        Assert.Contains(errors, error => error.Code == "reason.length");
    }

    [Fact]
    public void Duplicate_lines_and_a_zero_quantity_are_rejected()
    {
        var lineId = Guid.NewGuid();
        var accepted = SaleReturnPosting.TryNormalize(
            "Retour",
            [(lineId, "1.0000"), (lineId, "1.0000")],
            null,
            out _,
            out var duplicate);
        var zero = SaleReturnPosting.TryNormalize("Retour", [(lineId, "0.0000")], null, out _, out var zeroErrors);

        Assert.False(accepted);
        Assert.Contains(duplicate, error => error.Code == "lines.duplicate");
        Assert.False(zero);
        Assert.Contains(zeroErrors, error => error.Code == "quantity.positive");
    }

    [Fact]
    public void Tender_text_is_trimmed_without_trusting_a_client_return_amount()
    {
        var accepted = SaleReturnPosting.TryNormalize(
            " Retour ",
            [(Guid.NewGuid(), "0.5000")],
            [(" Cash ", "1.5000", "  VIR  ", "   ")],
            out var draft,
            out var errors);

        Assert.True(accepted);
        Assert.Empty(errors);
        Assert.Equal("Retour", draft.Reason);
        var tender = Assert.Single(draft.Refunds);
        Assert.Equal("cash", tender.Method);
        Assert.Equal(1.5000m, tender.Amount);
        Assert.Equal("VIR", tender.Reference);
        Assert.Null(tender.Notes);
    }
}
