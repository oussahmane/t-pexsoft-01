using Pexilio.Domain;

namespace Pexilio.Domain.Sales;

public enum CreditOutcome
{
    Allow,
    Integrity,
    NotApplicable,
    ReasonRequired,
    Exceeded,
    Override,
}

public readonly record struct CreditOverrideSnapshot(
    string Reason,
    decimal Limit,
    decimal OutstandingBefore,
    decimal ProjectedOutstanding);

public readonly record struct CreditDecision(
    CreditOutcome Outcome,
    decimal Charge,
    decimal ProjectedOutstanding,
    CreditOverrideSnapshot? Snapshot);

public readonly record struct CreditRequestProblem(int Status, string Code, string Field, string FieldCode);

/// <summary>
/// Decides whether a customer sale may add outstanding debt.
/// Money values stay <see cref="decimal"/> with the existing four-decimal scale.
/// </summary>
public static class CustomerCredit
{
    public const int ReasonMaxLength = 200;

    public const string OverrideForbidden = "creditOverride.forbidden";

    public const string ReasonRequired = "creditOverrideReason.required";

    public const string ReasonLength = "creditOverrideReason.length";

    public const string OverrideCustomer = "creditOverride.customer";

    public const string NotApplicable = "creditOverride.not_applicable";

    public static bool TryPrepare(
        bool customerLinked,
        bool administrator,
        bool creditOverride,
        string? reason,
        out string? normalizedReason,
        out CreditRequestProblem problem)
    {
        var attempted = creditOverride || !string.IsNullOrWhiteSpace(reason);
        if (!administrator && attempted)
        {
            normalizedReason = null;
            problem = new CreditRequestProblem(403, ErrorCodes.Forbidden, "creditOverride", OverrideForbidden);
            return false;
        }

        if (!customerLinked && attempted)
        {
            normalizedReason = null;
            problem = new CreditRequestProblem(400, ErrorCodes.ValidationFailed, "creditOverride", OverrideCustomer);
            return false;
        }

        if (!TryNormalize(reason, out normalizedReason, out var lengthCode))
        {
            problem = new CreditRequestProblem(400, ErrorCodes.ValidationFailed, "creditOverrideReason", lengthCode);
            return false;
        }

        problem = default;
        return true;
    }

    public static CreditDecision Decide(
        decimal? limit,
        decimal currentOutstanding,
        decimal saleTotal,
        decimal initialPayment,
        bool creditOverride,
        string? normalizedReason)
    {
        if (currentOutstanding < 0m || saleTotal < 0m || initialPayment < 0m || initialPayment > saleTotal)
        {
            return new CreditDecision(CreditOutcome.Integrity, 0m, 0m, null);
        }

        var charge = saleTotal - initialPayment;
        var projected = currentOutstanding + charge;
        var exceeds = limit is decimal configured && charge > 0m && projected > configured;
        if (creditOverride && !exceeds)
        {
            return new CreditDecision(CreditOutcome.NotApplicable, charge, projected, null);
        }

        if (!exceeds)
        {
            return new CreditDecision(CreditOutcome.Allow, charge, projected, null);
        }

        if (string.IsNullOrEmpty(normalizedReason))
        {
            return creditOverride
                ? new CreditDecision(CreditOutcome.ReasonRequired, charge, projected, null)
                : new CreditDecision(CreditOutcome.Exceeded, charge, projected, null);
        }

        if (!creditOverride)
        {
            return new CreditDecision(CreditOutcome.Exceeded, charge, projected, null);
        }

        return new CreditDecision(
            CreditOutcome.Override,
            charge,
            projected,
            new CreditOverrideSnapshot(normalizedReason, limit!.Value, currentOutstanding, projected));
    }

    private static bool TryNormalize(string? reason, out string? normalized, out string code)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            normalized = null;
            code = "";
            return true;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > ReasonMaxLength)
        {
            normalized = null;
            code = ReasonLength;
            return false;
        }

        normalized = trimmed;
        code = "";
        return true;
    }
}
