using System.Numerics;
using Pexilio.Domain.Stock;

namespace Pexilio.Domain.Sales;

public readonly record struct ReturnLineInput(
    Guid SaleLineId,
    decimal SoldQuantity,
    decimal LineTotal,
    decimal AllocatedSaleDiscount,
    decimal PreviouslyReturnedQuantity,
    decimal PreviouslyReturnedAmount,
    decimal Quantity);

public readonly record struct ReturnLineValuation(
    Guid SaleLineId,
    decimal Quantity,
    decimal Amount,
    decimal RemainingQuantity,
    decimal RemainingValue);

public static class SaleReturnRules
{
    public static bool TryValueLines(
        IReadOnlyList<ReturnLineInput>? lines,
        out IReadOnlyList<ReturnLineValuation> valuations,
        out decimal returnAmount,
        out IReadOnlyList<FieldError> errors)
    {
        valuations = [];
        returnAmount = 0m;
        if (lines is null || lines.Count == 0)
        {
            errors = [new FieldError("lines", "lines.required")];
            return false;
        }

        var found = new List<FieldError>();
        var seen = new HashSet<Guid>();
        var valued = new List<ReturnLineValuation>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var prefix = $"lines[{index}]";
            var duplicate = !seen.Add(line.SaleLineId);
            if (duplicate)
            {
                found.Add(new FieldError($"{prefix}.saleLineId", "lines.duplicate"));
            }

            if (!TryValueLine(line, prefix, out var valuation, out var lineErrors))
            {
                found.AddRange(lineErrors);
                continue;
            }

            if (!duplicate)
            {
                valued.Add(valuation);
            }
        }

        if (found.Count > 0 || valued.Count != lines.Count)
        {
            errors = found;
            return false;
        }

        valuations = valued;
        returnAmount = valued.Aggregate(0m, (sum, line) => sum + line.Amount);
        errors = found;
        return true;
    }

    public static bool TryValueLine(
        ReturnLineInput line,
        out ReturnLineValuation valuation,
        out IReadOnlyList<FieldError> errors) =>
        TryValueLine(line, "line", out valuation, out errors);

    private static bool TryValueLine(
        ReturnLineInput line,
        string prefix,
        out ReturnLineValuation valuation,
        out IReadOnlyList<FieldError> errors)
    {
        valuation = default;
        var found = new List<FieldError>();
        if (line.SaleLineId == Guid.Empty)
        {
            found.Add(new FieldError($"{prefix}.saleLineId", "saleLine.required"));
        }

        var soldOk = AcceptQuantity(line.SoldQuantity, $"{prefix}.soldQuantity", requirePositive: true, found);
        var totalOk = AcceptMoney(line.LineTotal, $"{prefix}.lineTotal", found);
        var allocatedOk = AcceptMoney(line.AllocatedSaleDiscount, $"{prefix}.allocatedSaleDiscount", found);
        var previousQuantityOk = AcceptQuantity(
            line.PreviouslyReturnedQuantity,
            $"{prefix}.previouslyReturnedQuantity",
            requirePositive: false,
            found);
        var previousAmountOk = AcceptMoney(line.PreviouslyReturnedAmount, $"{prefix}.previouslyReturnedAmount", found);
        var quantityOk = AcceptQuantity(line.Quantity, $"{prefix}.quantity", requirePositive: true, found);
        if (!soldOk || !totalOk || !allocatedOk || !previousQuantityOk || !previousAmountOk || !quantityOk)
        {
            errors = found;
            return false;
        }

        if (line.AllocatedSaleDiscount > line.LineTotal)
        {
            found.Add(new FieldError($"{prefix}.allocatedSaleDiscount", "discount.exceeds"));
            errors = found;
            return false;
        }

        var refundable = line.LineTotal - line.AllocatedSaleDiscount;
        if (line.PreviouslyReturnedQuantity > line.SoldQuantity)
        {
            found.Add(new FieldError($"{prefix}.previouslyReturnedQuantity", "return.exceeds"));
        }

        if (line.PreviouslyReturnedAmount > refundable)
        {
            found.Add(new FieldError($"{prefix}.previouslyReturnedAmount", "amount.exceeds"));
        }

        if (line.PreviouslyReturnedQuantity == 0m && line.PreviouslyReturnedAmount != 0m)
        {
            found.Add(new FieldError($"{prefix}.previouslyReturnedAmount", "amount.mismatch"));
        }

        if (found.Count > 0)
        {
            errors = found;
            return false;
        }

        var remainingQuantity = line.SoldQuantity - line.PreviouslyReturnedQuantity;
        var remainingValue = refundable - line.PreviouslyReturnedAmount;
        if (remainingQuantity == 0m && remainingValue != 0m)
        {
            found.Add(new FieldError($"{prefix}.previouslyReturnedAmount", "amount.mismatch"));
            errors = found;
            return false;
        }

        if (line.Quantity > remainingQuantity)
        {
            found.Add(new FieldError($"{prefix}.quantity", "return.exceeds"));
            errors = found;
            return false;
        }

        if (!TryProportion(remainingValue, remainingQuantity, line.Quantity, out var amount))
        {
            found.Add(new FieldError($"{prefix}.quantity", "amount.mismatch"));
            errors = found;
            return false;
        }

        valuation = new ReturnLineValuation(
            line.SaleLineId,
            line.Quantity,
            amount,
            remainingQuantity - line.Quantity,
            remainingValue - amount);
        errors = found;
        return true;
    }

    // Same half-away scaled division as SaleRules percentage amounts.
    // The slice that consumes the remaining quantity takes the remaining value exactly.
    private static bool TryProportion(
        decimal remainingValue,
        decimal remainingQuantity,
        decimal quantity,
        out decimal amount)
    {
        amount = 0m;
        if (remainingQuantity <= 0m || quantity <= 0m || quantity > remainingQuantity || remainingValue < 0m)
        {
            return false;
        }

        if (quantity == remainingQuantity)
        {
            amount = remainingValue;
            return amount >= 0m && SaleRules.ExactlyScale4(amount);
        }

        var valueScaled = Scaled(remainingValue);
        var quantityScaled = Scaled(quantity);
        var remainingScaled = Scaled(remainingQuantity);
        var quotient = BigInteger.DivRem(valueScaled * quantityScaled, remainingScaled, out var remainder);
        if (remainder * 2 >= remainingScaled)
        {
            quotient += BigInteger.One;
        }

        if (quotient < 0 || quotient > valueScaled || quotient > Scaled(QuantityText.Maximum))
        {
            return false;
        }

        amount = (decimal)quotient / 10000m;
        return SaleRules.ExactlyScale4(amount) && amount <= remainingValue;
    }

    private static bool AcceptQuantity(
        decimal value,
        string field,
        bool requirePositive,
        List<FieldError> errors)
    {
        if (!SaleRules.ExactlyScale4(value))
        {
            errors.Add(new FieldError(field, "quantity.scale"));
            return false;
        }

        if (value < 0m)
        {
            errors.Add(new FieldError(field, "quantity.sign"));
            return false;
        }

        if (requirePositive && value == 0m)
        {
            errors.Add(new FieldError(field, "quantity.positive"));
            return false;
        }

        return true;
    }

    private static bool AcceptMoney(decimal value, string field, List<FieldError> errors)
    {
        if (!SaleRules.ExactlyScale4(value))
        {
            errors.Add(new FieldError(field, "amount.scale"));
            return false;
        }

        if (value < 0m)
        {
            errors.Add(new FieldError(field, "amount.sign"));
            return false;
        }

        return true;
    }

    private static BigInteger Scaled(decimal value) => new(value * 10000m);
}
