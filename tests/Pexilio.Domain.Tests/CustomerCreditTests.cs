using Pexilio.Domain;
using Pexilio.Domain.Sales;

namespace Pexilio.Domain.Tests;

public sealed class CustomerCreditTests
{
    [Fact]
    public void A_null_limit_allows_the_sale()
    {
        var decision = CustomerCredit.Decide(null, 500.0000m, 100.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(100.0000m, decision.Charge);
        Assert.Equal(600.0000m, decision.ProjectedOutstanding);
        Assert.Null(decision.Snapshot);
    }

    [Fact]
    public void A_zero_limit_rejects_new_debt()
    {
        var decision = CustomerCredit.Decide(0m, 0m, 10.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Exceeded, decision.Outcome);
        Assert.Equal(10.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void A_zero_limit_allows_a_fully_paid_sale()
    {
        var decision = CustomerCredit.Decide(0m, 0m, 10.0000m, 10.0000m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(0m, decision.Charge);
    }

    [Fact]
    public void A_sale_exactly_at_the_limit_is_allowed()
    {
        var decision = CustomerCredit.Decide(100.0000m, 40.0000m, 60.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(100.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void One_ten_thousandth_over_the_limit_is_rejected()
    {
        var decision = CustomerCredit.Decide(10.0000m, 0m, 10.0001m, 0m, false, null);

        Assert.Equal(CreditOutcome.Exceeded, decision.Outcome);
        Assert.Equal(10.0001m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void A_partial_initial_payment_consumes_only_the_unpaid_portion()
    {
        var decision = CustomerCredit.Decide(100.0000m, 40.0000m, 80.0000m, 30.0000m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(50.0000m, decision.Charge);
        Assert.Equal(90.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void A_fully_paid_sale_adds_no_debt()
    {
        var decision = CustomerCredit.Decide(10.0000m, 6.0000m, 10.0000m, 10.0000m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(0m, decision.Charge);
        Assert.Equal(6.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void Existing_debt_plus_a_new_charge_can_exceed_the_limit()
    {
        var decision = CustomerCredit.Decide(10.0000m, 7.0000m, 4.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Exceeded, decision.Outcome);
        Assert.Equal(11.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void Debt_reduced_by_a_payment_frees_credit_for_the_next_sale()
    {
        var decision = CustomerCredit.Decide(10.0000m, 6.0000m, 4.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(10.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void Debt_reduced_by_a_return_already_in_the_balance_frees_credit()
    {
        var decision = CustomerCredit.Decide(10.0000m, 6.0000m, 4.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(4.0000m, decision.Charge);
    }

    [Fact]
    public void A_fully_paid_sale_is_allowed_when_existing_debt_is_already_above_the_limit()
    {
        var decision = CustomerCredit.Decide(100.0000m, 120.0000m, 50.0000m, 50.0000m, false, null);

        Assert.Equal(CreditOutcome.Allow, decision.Outcome);
        Assert.Equal(0m, decision.Charge);
        Assert.Equal(120.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void New_debt_while_already_above_the_limit_requires_an_override()
    {
        var decision = CustomerCredit.Decide(100.0000m, 120.0000m, 50.0000m, 40.0000m, false, null);

        Assert.Equal(CreditOutcome.Exceeded, decision.Outcome);
        Assert.Equal(10.0000m, decision.Charge);
        Assert.Equal(130.0000m, decision.ProjectedOutstanding);
    }

    [Fact]
    public void A_negative_outstanding_balance_is_a_financial_integrity_failure()
    {
        var decision = CustomerCredit.Decide(10.0000m, -0.0001m, 1.0000m, 0m, false, null);

        Assert.Equal(CreditOutcome.Integrity, decision.Outcome);
    }

    [Fact]
    public void A_walk_in_override_is_rejected_and_a_normal_walk_in_request_is_not()
    {
        var overridden = CustomerCredit.TryPrepare(false, true, true, "Exception", out _, out var problem);
        var plain = CustomerCredit.TryPrepare(false, true, false, null, out var reason, out _);

        Assert.False(overridden);
        Assert.Equal(400, problem.Status);
        Assert.Equal(CustomerCredit.OverrideCustomer, problem.FieldCode);
        Assert.True(plain);
        Assert.Null(reason);
    }

    [Fact]
    public void A_cashier_override_flag_or_reason_is_forbidden()
    {
        var flagged = CustomerCredit.TryPrepare(true, false, true, null, out _, out var flagProblem);
        var explained = CustomerCredit.TryPrepare(true, false, false, "Please", out _, out var reasonProblem);

        Assert.False(flagged);
        Assert.Equal(403, flagProblem.Status);
        Assert.Equal(ErrorCodes.Forbidden, flagProblem.Code);
        Assert.Equal(CustomerCredit.OverrideForbidden, flagProblem.FieldCode);
        Assert.False(explained);
        Assert.Equal(CustomerCredit.OverrideForbidden, reasonProblem.FieldCode);
    }

    [Fact]
    public void An_override_without_a_reason_is_required_only_when_the_sale_exceeds_the_limit()
    {
        var exceeded = CustomerCredit.Decide(10.0000m, 0m, 11.0000m, 0m, true, null);
        var within = CustomerCredit.Decide(10.0000m, 0m, 10.0000m, 0m, true, null);

        Assert.Equal(CreditOutcome.ReasonRequired, exceeded.Outcome);
        Assert.Equal(CreditOutcome.NotApplicable, within.Outcome);
    }

    [Fact]
    public void A_reason_longer_than_200_characters_is_rejected()
    {
        var prepared = CustomerCredit.TryPrepare(true, true, true, new string('a', 201), out _, out var problem);

        Assert.False(prepared);
        Assert.Equal(CustomerCredit.ReasonLength, problem.FieldCode);
    }

    [Fact]
    public void A_valid_override_captures_the_limit_and_both_balances()
    {
        var prepared = CustomerCredit.TryPrepare(true, true, true, " Client historique ", out var reason, out _);
        var decision = CustomerCredit.Decide(10.0000m, 8.0000m, 5.0000m, 1.0000m, true, reason);

        Assert.True(prepared);
        Assert.Equal(CreditOutcome.Override, decision.Outcome);
        var snapshot = decision.Snapshot!.Value;
        Assert.Equal("Client historique", snapshot.Reason);
        Assert.Equal(10.0000m, snapshot.Limit);
        Assert.Equal(8.0000m, snapshot.OutstandingBefore);
        Assert.Equal(12.0000m, snapshot.ProjectedOutstanding);
    }

    [Fact]
    public void A_reason_without_the_override_flag_does_not_become_an_override()
    {
        var prepared = CustomerCredit.TryPrepare(true, true, false, "  Noted  ", out var reason, out _);
        var decision = CustomerCredit.Decide(10.0000m, 0m, 11.0000m, 0m, false, reason);

        Assert.True(prepared);
        Assert.Equal("Noted", reason);
        Assert.Equal(CreditOutcome.Exceeded, decision.Outcome);
        Assert.Null(decision.Snapshot);
    }

    [Fact]
    public void An_override_on_a_sale_within_the_limit_is_not_applicable()
    {
        var decision = CustomerCredit.Decide(10.0000m, 0m, 10.0000m, 4.0000m, true, "Not needed");

        Assert.Equal(CreditOutcome.NotApplicable, decision.Outcome);
        Assert.Null(decision.Snapshot);
    }
}
