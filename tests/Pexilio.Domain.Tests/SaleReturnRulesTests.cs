using Pexilio.Domain;
using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class SaleReturnRulesTests
{
    [Fact]
    public void A_partial_return_uses_the_remaining_value_and_quantity()
    {
        var line = Line(sold: 4.0000m, lineTotal: 10.0000m, allocated: 0m, quantity: 1.0000m);

        var saved = SaleReturnRules.TryValueLine(line, out var valuation, out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(2.5000m, valuation.Amount);
        Assert.Equal(3.0000m, valuation.RemainingQuantity);
        Assert.Equal(7.5000m, valuation.RemainingValue);
    }

    [Fact]
    public void The_full_remaining_quantity_returns_the_remaining_value_exactly()
    {
        var line = Line(sold: 4.0000m, lineTotal: 10.0000m, allocated: 1.5000m, quantity: 4.0000m);

        var saved = SaleReturnRules.TryValueLine(line, out var valuation, out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(8.5000m, valuation.Amount);
        Assert.Equal(0m, valuation.RemainingQuantity);
        Assert.Equal(0m, valuation.RemainingValue);
    }

    [Fact]
    public void The_exact_remaining_boundary_is_accepted()
    {
        var first = SaleReturnRules.TryValueLine(
            Line(sold: 1.0000m, lineTotal: 3.0000m, allocated: 0m, quantity: 0.7500m),
            out var partial,
            out var firstErrors);
        var second = SaleReturnRules.TryValueLine(
            Line(
                sold: 1.0000m,
                lineTotal: 3.0000m,
                allocated: 0m,
                previousQuantity: 0.7500m,
                previousAmount: partial.Amount,
                quantity: 0.2500m),
            out var rest,
            out var secondErrors);

        Assert.True(first);
        Assert.Empty(firstErrors);
        Assert.True(second);
        Assert.Empty(secondErrors);
        Assert.Equal(0.2500m, rest.Quantity);
        Assert.Equal(3.0000m, partial.Amount + rest.Amount);
    }

    [Fact]
    public void Repeated_partial_returns_sum_to_the_original_refundable_value()
    {
        var slices = new decimal[] { 1.0000m, 1.0000m, 1.0000m };
        var total = Returned(sold: 3.0000m, lineTotal: 10.0000m, allocated: 0m, slices);

        Assert.Equal(10.0000m, total);
    }

    [Theory]
    [InlineData("0.0001")]
    [InlineData("0.3333")]
    [InlineData("1.0000")]
    public void Every_partition_of_a_line_returns_the_refundable_value(string sliceText)
    {
        var slice = decimal.Parse(sliceText, System.Globalization.CultureInfo.InvariantCulture);
        var slices = new List<decimal>();
        var remaining = 2.0000m;
        while (remaining > 0m)
        {
            var next = remaining > slice ? slice : remaining;
            slices.Add(next);
            remaining -= next;
        }

        var total = Returned(sold: 2.0000m, lineTotal: 7.2500m, allocated: 1.2500m, slices);

        Assert.Equal(6.0000m, total);
    }

    [Fact]
    public void One_ten_thousandth_slices_sum_exactly()
    {
        var slices = Enumerable.Repeat(0.0001m, 10_000).ToArray();

        var total = Returned(sold: 1.0000m, lineTotal: 1.0000m, allocated: 0m, slices);

        Assert.Equal(1.0000m, total);
    }

    [Fact]
    public void Half_away_from_zero_rounding_is_applied_to_a_partial_slice()
    {
        var one = Value(sold: 3.0000m, lineTotal: 10.0000m, quantity: 1.0000m);
        var two = Value(sold: 3.0000m, lineTotal: 10.0000m, quantity: 2.0000m);

        Assert.Equal(3.3333m, one);
        Assert.Equal(6.6667m, two);
    }

    [Fact]
    public void A_half_unit_rounds_away_from_zero_and_the_final_slice_keeps_the_total()
    {
        var first = Value(sold: 2.0000m, lineTotal: 0.0001m, quantity: 1.0000m);
        var second = SaleReturnRules.TryValueLine(
            Line(
                sold: 2.0000m,
                lineTotal: 0.0001m,
                allocated: 0m,
                previousQuantity: 1.0000m,
                previousAmount: first,
                quantity: 1.0000m),
            out var rest,
            out var errors);

        Assert.Equal(0.0001m, first);
        Assert.True(second);
        Assert.Empty(errors);
        Assert.Equal(0m, rest.Amount);
        Assert.Equal(0.0001m, first + rest.Amount);
    }

    [Fact]
    public void A_line_discount_is_already_inside_the_line_total()
    {
        var amount = Value(sold: 2.0000m, lineTotal: 8.0000m, allocated: 0m, quantity: 2.0000m);

        Assert.Equal(8.0000m, amount);
    }

    [Fact]
    public void An_allocated_ticket_discount_reduces_the_refundable_value()
    {
        var amount = Value(sold: 2.0000m, lineTotal: 8.0000m, allocated: 1.5000m, quantity: 1.0000m);

        Assert.Equal(3.2500m, amount);
    }

    [Fact]
    public void A_fully_discounted_line_returns_quantity_with_a_zero_amount()
    {
        var saved = SaleReturnRules.TryValueLine(
            Line(sold: 1.5000m, lineTotal: 4.0000m, allocated: 4.0000m, quantity: 0.5000m),
            out var valuation,
            out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(0m, valuation.Amount);
        Assert.Equal(1.0000m, valuation.RemainingQuantity);
        Assert.Equal(0m, valuation.RemainingValue);
    }

    [Fact]
    public void A_fractional_quantity_uses_scale_4()
    {
        var amount = Value(sold: 0.5000m, lineTotal: 1.0000m, quantity: 0.2500m);

        Assert.Equal(0.5000m, amount);
    }

    [Fact]
    public void A_partial_amount_never_exceeds_the_remaining_refundable_value()
    {
        decimal previousQuantity = 0m;
        decimal previousAmount = 0m;
        var guard = 0;
        while (previousQuantity < 3.0000m)
        {
            var quantity = previousQuantity + 0.0001m > 3.0000m ? 3.0000m - previousQuantity : 0.0001m;
            var saved = SaleReturnRules.TryValueLine(
                Line(
                    sold: 3.0000m,
                    lineTotal: 10.0000m,
                    allocated: 0.0001m,
                    previousQuantity: previousQuantity,
                    previousAmount: previousAmount,
                    quantity: quantity),
                out var valuation,
                out var errors);

            Assert.True(saved);
            Assert.Empty(errors);
            Assert.InRange(valuation.Amount, 0m, 9.9999m - previousAmount);
            previousQuantity += quantity;
            previousAmount += valuation.Amount;
            guard++;
            Assert.True(guard <= 30_001);
        }

        Assert.Equal(3.0000m, previousQuantity);
        Assert.Equal(9.9999m, previousAmount);
    }

    [Fact]
    public void An_over_return_is_rejected()
    {
        var saved = SaleReturnRules.TryValueLine(
            Line(sold: 1.0000m, lineTotal: 10.0000m, previousQuantity: 0.7500m, previousAmount: 7.5000m, quantity: 0.2501m),
            out _,
            out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError("line.quantity", "return.exceeds"), errors);
    }

    [Fact]
    public void Zero_and_negative_quantities_are_rejected()
    {
        var zero = Reject(Line(quantity: 0m));
        var negative = Reject(Line(quantity: -0.0001m));

        Assert.Contains(new FieldError("line.quantity", "quantity.positive"), zero);
        Assert.Contains(new FieldError("line.quantity", "quantity.sign"), negative);
    }

    [Fact]
    public void Excess_quantity_precision_is_rejected()
    {
        var errors = Reject(Line(quantity: 1.00001m));

        Assert.Contains(new FieldError("line.quantity", "quantity.scale"), errors);
    }

    [Fact]
    public void A_document_cannot_return_the_same_sale_line_twice()
    {
        var lineId = Guid.NewGuid();
        var saved = SaleReturnRules.TryValueLines(
            [
                Line(lineId, quantity: 1.0000m),
                Line(lineId, quantity: 1.0000m),
            ],
            out _,
            out _,
            out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError("lines[1].saleLineId", "lines.duplicate"), errors);
    }

    [Fact]
    public void Distinct_sale_lines_are_tracked_separately_from_the_product()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var saved = SaleReturnRules.TryValueLines(
            [
                Line(first, sold: 2.0000m, lineTotal: 4.0000m, quantity: 2.0000m),
                Line(second, sold: 5.0000m, lineTotal: 5.0000m, quantity: 1.0000m),
            ],
            out var valuations,
            out var returnAmount,
            out var errors);

        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(2, valuations.Count);
        Assert.Equal(5.0000m, returnAmount);
        Assert.Equal(first, valuations[0].SaleLineId);
        Assert.Equal(4.0000m, valuations[0].Amount);
        Assert.Equal(1.0000m, valuations[1].Amount);
    }

    [Fact]
    public void An_empty_return_is_rejected()
    {
        var saved = SaleReturnRules.TryValueLines([], out _, out _, out var errors);

        Assert.False(saved);
        Assert.Contains(new FieldError("lines", "lines.required"), errors);
    }

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public void Invalid_original_sale_line_values_are_rejected(ReturnLineInput line, FieldError expected)
    {
        var errors = Reject(line);

        Assert.Contains(expected, errors);
    }

    public static IEnumerable<object[]> InvalidLines()
    {
        yield return [Line(sold: 0m), new FieldError("line.soldQuantity", "quantity.positive")];
        yield return [Line(sold: -1.0000m), new FieldError("line.soldQuantity", "quantity.sign")];
        yield return [Line(sold: 1.00001m), new FieldError("line.soldQuantity", "quantity.scale")];
        yield return [Line(lineTotal: -1.0000m), new FieldError("line.lineTotal", "amount.sign")];
        yield return [Line(lineTotal: 1.00001m), new FieldError("line.lineTotal", "amount.scale")];
        yield return [Line(lineTotal: 5.0000m, allocated: 5.0001m), new FieldError("line.allocatedSaleDiscount", "discount.exceeds")];
        yield return [Line(allocated: -0.0001m), new FieldError("line.allocatedSaleDiscount", "amount.sign")];
        yield return [Line(previousQuantity: 2.0000m), new FieldError("line.previouslyReturnedQuantity", "return.exceeds")];
        yield return [Line(previousAmount: 0.0001m), new FieldError("line.previouslyReturnedAmount", "amount.mismatch")];
        yield return [Line(previousQuantity: 1.0000m, previousAmount: 10.0001m), new FieldError("line.previouslyReturnedAmount", "amount.exceeds")];
        yield return [Line(saleLineId: Guid.Empty), new FieldError("line.saleLineId", "saleLine.required")];
    }

    private static decimal Value(
        decimal sold,
        decimal lineTotal,
        decimal quantity,
        decimal allocated = 0m) =>
        AssertValued(Line(sold: sold, lineTotal: lineTotal, allocated: allocated, quantity: quantity)).Amount;

    private static decimal Returned(
        decimal sold,
        decimal lineTotal,
        decimal allocated,
        IReadOnlyList<decimal> slices)
    {
        decimal previousQuantity = 0m;
        decimal previousAmount = 0m;
        foreach (var slice in slices)
        {
            var valuation = AssertValued(Line(
                sold: sold,
                lineTotal: lineTotal,
                allocated: allocated,
                previousQuantity: previousQuantity,
                previousAmount: previousAmount,
                quantity: slice));
            previousQuantity += slice;
            previousAmount += valuation.Amount;
        }

        Assert.Equal(sold, previousQuantity);
        return previousAmount;
    }

    private static ReturnLineValuation AssertValued(ReturnLineInput line)
    {
        var saved = SaleReturnRules.TryValueLine(line, out var valuation, out var errors);
        Assert.True(saved);
        Assert.Empty(errors);
        return valuation;
    }

    private static IReadOnlyList<FieldError> Reject(ReturnLineInput line)
    {
        var saved = SaleReturnRules.TryValueLine(line, out _, out var errors);
        Assert.False(saved);
        return errors;
    }

    private static ReturnLineInput Line(
        Guid? saleLineId = null,
        decimal sold = 1.0000m,
        decimal lineTotal = 10.0000m,
        decimal allocated = 0m,
        decimal previousQuantity = 0m,
        decimal previousAmount = 0m,
        decimal quantity = 1.0000m) =>
        new(
            saleLineId ?? Guid.NewGuid(),
            sold,
            lineTotal,
            allocated,
            previousQuantity,
            previousAmount,
            quantity);
}
