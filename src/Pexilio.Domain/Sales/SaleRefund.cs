namespace Pexilio.Domain.Sales;

public sealed class SaleRefund
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid SaleId { get; set; }

    public Guid SaleReturnId { get; set; }

    public decimal Amount { get; set; }

    public string Method { get; set; } = "";

    public string? Reference { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset RefundedAtUtc { get; set; }

    public Guid RefundedByUserId { get; set; }
}
