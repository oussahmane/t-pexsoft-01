using Pexilio.Domain.Stock;

namespace Pexilio.Domain.Sales;

public readonly record struct SaleReturnLineDraft(Guid SaleLineId, decimal Quantity);

public readonly record struct SaleRefundTenderDraft(string? Method, decimal Amount, string? Reference, string? Notes);

public readonly record struct SaleReturnDraft(
    string Reason,
    IReadOnlyList<SaleReturnLineDraft> Lines,
    IReadOnlyList<SaleRefundTenderDraft> Refunds);

public static class SaleReturnPosting
{
    public const int ReasonMaxLength = 200;

    public static bool TryNormalize(
        string? reasonText,
        IReadOnlyList<(Guid SaleLineId, string? Quantity)>? lines,
        IReadOnlyList<(string? Method, string? Amount, string? Reference, string? Notes)>? refunds,
        out SaleReturnDraft draft,
        out IReadOnlyList<FieldError> errors)
    {
        draft = default;
        var found = new List<FieldError>();
        var reason = reasonText?.Trim() ?? "";
        if (reason.Length == 0)
        {
            found.Add(new FieldError("reason", "reason.required"));
        }
        else if (reason.Length > ReasonMaxLength)
        {
            found.Add(new FieldError("reason", "reason.length"));
        }

        if (lines is null || lines.Count == 0)
        {
            found.Add(new FieldError("lines", "lines.required"));
            errors = found;
            return false;
        }

        var seen = new HashSet<Guid>();
        var parsedLines = new List<SaleReturnLineDraft>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var prefix = $"lines[{index}]";
            if (line.SaleLineId == Guid.Empty)
            {
                found.Add(new FieldError($"{prefix}.saleLineId", "saleLine.required"));
            }
            else if (!seen.Add(line.SaleLineId))
            {
                found.Add(new FieldError($"{prefix}.saleLineId", "lines.duplicate"));
            }

            if (!QuantityText.TryParse(line.Quantity, "quantity", allowNegative: false, out var quantity, out var quantityCode))
            {
                found.Add(new FieldError($"{prefix}.quantity", quantityCode));
                continue;
            }

            if (quantity == 0m)
            {
                found.Add(new FieldError($"{prefix}.quantity", "quantity.positive"));
                continue;
            }

            if (line.SaleLineId != Guid.Empty && seen.Contains(line.SaleLineId))
            {
                parsedLines.Add(new SaleReturnLineDraft(line.SaleLineId, quantity));
            }
        }

        var parsedRefunds = new List<SaleRefundTenderDraft>();
        var submittedRefunds = refunds ?? [];
        for (var index = 0; index < submittedRefunds.Count; index++)
        {
            var tender = submittedRefunds[index];
            var prefix = $"refunds[{index}]";
            var method = tender.Method?.Trim().ToLowerInvariant() ?? "";
            if (!QuantityText.TryParse(tender.Amount, "refund", allowNegative: false, out var amount, out var amountCode))
            {
                found.Add(new FieldError($"{prefix}.amount", amountCode));
                continue;
            }

            var reference = Optional(tender.Reference, $"{prefix}.reference", "reference.length", CustomerPaymentValidator.ReferenceMaxLength, found);
            var notes = Optional(tender.Notes, $"{prefix}.notes", "notes.length", CustomerPaymentValidator.NotesMaxLength, found);
            parsedRefunds.Add(new SaleRefundTenderDraft(method, amount, reference, notes));
        }

        if (found.Count > 0 || parsedLines.Count != lines.Count || parsedRefunds.Count != submittedRefunds.Count)
        {
            errors = found;
            return false;
        }

        draft = new SaleReturnDraft(reason, parsedLines, parsedRefunds);
        errors = found;
        return true;
    }

    private static string? Optional(string? value, string field, string lengthCode, int maxLength, List<FieldError> found)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            found.Add(new FieldError(field, lengthCode));
            return null;
        }

        return trimmed;
    }
}
