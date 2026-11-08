namespace Pexilio.Domain.Sales;

public sealed class SaleReturn
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid SaleId { get; set; }

    public string Number { get; set; } = "";

    public string Reason { get; set; } = "";

    public DateTimeOffset ReturnedAtUtc { get; set; }

    public Guid ReturnedByUserId { get; set; }

    public List<SaleReturnLine> Lines { get; set; } = [];

    public List<SaleRefund> Refunds { get; set; } = [];
}
