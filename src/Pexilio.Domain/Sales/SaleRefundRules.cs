namespace Pexilio.Domain.Sales;

public readonly record struct RefundTender(string Method, decimal Amount);

public readonly record struct SaleRefundQuote(
    decimal ReturnAmount,
    decimal OutstandingBefore,
    decimal RequiredRefund,
    decimal NetAccountReduction);

public static class SaleRefundRules
{
    public static readonly string[] Methods = ["cash", "transfer", "card"];

    public static bool TryQuote(
        decimal returnAmount,
        decimal outstandingBefore,
        decimal unrefundedCollected,
        out SaleRefundQuote quote,
        out IReadOnlyList<FieldError> errors)
    {
        quote = default;
        var found = new List<FieldError>();
        var returnOk = AcceptMoney(returnAmount, "returnAmount", found);
        var outstandingOk = AcceptMoney(outstandingBefore, "outstanding", found);
        var collectedOk = AcceptMoney(unrefundedCollected, "collected", found);
        if (!returnOk || !outstandingOk || !collectedOk)
        {
            errors = found;
            return false;
        }

        var required = returnAmount > outstandingBefore ? returnAmount - outstandingBefore : 0m;
        var net = returnAmount - required;
        if (required < 0m || net < 0m || net > outstandingBefore || required > unrefundedCollected)
        {
            found.Add(new FieldError("refund", required > unrefundedCollected ? "refund.exceeds" : "refund.integrity"));
            errors = found;
            return false;
        }

        quote = new SaleRefundQuote(returnAmount, outstandingBefore, required, net);
        errors = found;
        return true;
    }

    public static bool TryTenders(
        decimal requiredRefund,
        IReadOnlyList<(string? Method, decimal Amount)>? tenders,
        out IReadOnlyList<RefundTender> normalized,
        out IReadOnlyList<FieldError> errors)
    {
        normalized = [];
        var found = new List<FieldError>();
        if (!AcceptMoney(requiredRefund, "refund", found))
        {
            errors = found;
            return false;
        }

        var submitted = tenders ?? [];
        if (requiredRefund == 0m)
        {
            if (submitted.Count > 0)
            {
                found.Add(new FieldError("refunds", "refund.unexpected"));
            }

            errors = found;
            return found.Count == 0;
        }

        if (submitted.Count == 0)
        {
            found.Add(new FieldError("refunds", "refund.under"));
            errors = found;
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<RefundTender>(submitted.Count);
        var amountsValid = true;
        for (var index = 0; index < submitted.Count; index++)
        {
            var tender = submitted[index];
            var method = tender.Method?.Trim().ToLowerInvariant() ?? "";
            if (!Methods.Contains(method))
            {
                found.Add(new FieldError($"refunds[{index}].method", "refund.method"));
                amountsValid = false;
            }
            else if (!seen.Add(method))
            {
                found.Add(new FieldError($"refunds[{index}].method", "refunds.duplicate"));
                amountsValid = false;
            }

            if (!SaleRules.ExactlyScale4(tender.Amount))
            {
                found.Add(new FieldError($"refunds[{index}].amount", "refund.scale"));
                amountsValid = false;
                continue;
            }

            if (tender.Amount < 0m)
            {
                found.Add(new FieldError($"refunds[{index}].amount", "refund.sign"));
                amountsValid = false;
                continue;
            }

            if (tender.Amount == 0m)
            {
                found.Add(new FieldError($"refunds[{index}].amount", "refund.positive"));
                amountsValid = false;
                continue;
            }

            if (Methods.Contains(method) && seen.Contains(method))
            {
                parsed.Add(new RefundTender(method, tender.Amount));
            }
        }

        if (!amountsValid || found.Count > 0 || parsed.Count != submitted.Count)
        {
            errors = found;
            return false;
        }

        var sum = parsed.Aggregate(0m, (total, tender) => total + tender.Amount);
        if (sum < requiredRefund)
        {
            found.Add(new FieldError("refunds", "refund.under"));
        }
        else if (sum > requiredRefund)
        {
            found.Add(new FieldError("refunds", "refund.over"));
        }

        if (found.Count > 0)
        {
            errors = found;
            return false;
        }

        normalized = parsed;
        errors = found;
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
}
