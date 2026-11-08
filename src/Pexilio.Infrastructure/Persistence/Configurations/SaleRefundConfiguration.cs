using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pexilio.Domain.Sales;
using Pexilio.Infrastructure.Identity;

namespace Pexilio.Infrastructure.Persistence.Configurations;

public sealed class SaleRefundConfiguration : IEntityTypeConfiguration<SaleRefund>
{
    public void Configure(EntityTypeBuilder<SaleRefund> builder)
    {
        builder.ToTable("sale_refund", "pexilio", table =>
        {
            table.HasCheckConstraint("CK_sale_refund_amount", "\"Amount\" > 0");
            table.HasCheckConstraint("CK_sale_refund_method", "\"Method\" IN ('cash', 'transfer', 'card')");
            table.HasTrigger("sale_refund_append_only");
            table.HasTrigger("sale_refund_append_only_truncate");
            table.HasTrigger("sale_outstanding_on_refund");
            table.HasTrigger("sale_refund_within_return");
        });
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Method).HasMaxLength(32).IsRequired();
        builder.Property(refund => refund.Amount).HasColumnType("numeric(19,4)");
        builder.Property(refund => refund.Reference).HasMaxLength(CustomerPaymentValidator.ReferenceMaxLength);
        builder.Property(refund => refund.Notes).HasMaxLength(CustomerPaymentValidator.NotesMaxLength);
        builder.HasIndex(refund => new { refund.CompanyId, refund.SaleReturnId, refund.SaleId })
            .HasDatabaseName("IX_sale_refund_company_return");
        builder.HasOne<SaleReturn>().WithMany(saleReturn => saleReturn.Refunds)
            .HasForeignKey(refund => new { refund.SaleReturnId, refund.SaleId, refund.CompanyId })
            .HasPrincipalKey(saleReturn => new { saleReturn.Id, saleReturn.SaleId, saleReturn.CompanyId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_sale_refund_return");
        builder.HasOne<Sale>().WithMany()
            .HasForeignKey(refund => new { refund.SaleId, refund.CompanyId })
            .HasPrincipalKey(sale => new { sale.Id, sale.CompanyId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_sale_refund_sale");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(refund => refund.RefundedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
