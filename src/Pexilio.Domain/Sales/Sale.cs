namespace Pexilio.Domain.Sales;

public sealed class Sale
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = "";

    public string Status { get; set; } = SaleStatuses.Posted;

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public string DiscountKind { get; set; } = DiscountKinds.None;

    public decimal? DiscountRate { get; set; }

    public decimal Total { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public string? CustomerType { get; set; }

    public bool CreditLimitOverridden { get; set; }

    public string? CreditOverrideReason { get; set; }

    public Guid? CreditOverrideByUserId { get; set; }

    public DateTimeOffset? CreditOverrideAtUtc { get; set; }

    public decimal? CreditLimitSnapshot { get; set; }

    public decimal? CreditOutstandingBefore { get; set; }

    public decimal? CreditProjectedOutstanding { get; set; }

    public List<SaleLine> Lines { get; set; } = [];

    public List<SalePayment> Payments { get; set; } = [];
}
