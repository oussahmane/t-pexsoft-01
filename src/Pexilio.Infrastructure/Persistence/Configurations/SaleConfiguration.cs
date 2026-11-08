using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pexilio.Domain.Company;
using Pexilio.Domain.Customers;
using Pexilio.Domain.Sales;
using Pexilio.Infrastructure.Identity;

namespace Pexilio.Infrastructure.Persistence.Configurations;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("sale", "pexilio", table =>
        {
            table.HasCheckConstraint("CK_sale_status", "\"Status\" = 'posted'");
            table.HasCheckConstraint(
                "CK_sale_amounts",
                """
                "Subtotal" >= 0
                AND "DiscountAmount" >= 0
                AND "Total" >= 0
                AND "Total" = "Subtotal" - "DiscountAmount"
                """);
            // CK_sale_discount accepts a percentage rate from 0 through 100. It does not recompute
            // DiscountAmount from that rate. SalesService is authoritative for the amount. Posted
            // sales stay append-only, so this limitation is not closed with a trigger.
            table.HasCheckConstraint(
                "CK_sale_discount",
                """
                ("DiscountKind" = 'none' AND "DiscountAmount" = 0 AND "DiscountRate" IS NULL)
                OR ("DiscountKind" = 'amount' AND "DiscountRate" IS NULL)
                OR ("DiscountKind" = 'percent' AND "DiscountRate" >= 0 AND "DiscountRate" <= 100)
                """);
            table.HasTrigger("sale_append_only");
            table.HasTrigger("sale_append_only_truncate");
            table.HasTrigger("sale_totals_match_lines");
            table.HasTrigger("sale_outstanding_on_sale");
            table.HasCheckConstraint(
                "CK_sale_customer",
                """
                ("CustomerId" IS NULL AND "CustomerName" IS NULL AND "CustomerType" IS NULL)
                OR ("CustomerId" IS NOT NULL AND char_length(btrim("CustomerName")) > 0 AND "CustomerType" IN ('individual', 'business'))
                """);
            table.HasCheckConstraint(
                "CK_sale_credit_override",
                """
                (NOT "CreditLimitOverridden"
                    AND "CreditOverrideReason" IS NULL
                    AND "CreditOverrideByUserId" IS NULL
                    AND "CreditOverrideAtUtc" IS NULL
                    AND "CreditLimitSnapshot" IS NULL
                    AND "CreditOutstandingBefore" IS NULL
                    AND "CreditProjectedOutstanding" IS NULL)
                OR ("CreditLimitOverridden"
                    AND "CustomerId" IS NOT NULL
                    AND "CreditOverrideReason" IS NOT NULL
                    AND btrim("CreditOverrideReason") = "CreditOverrideReason"
                    AND char_length("CreditOverrideReason") BETWEEN 1 AND 200
                    AND "CreditOverrideByUserId" IS NOT NULL
                    AND "CreditOverrideByUserId" = "CreatedByUserId"
                    AND "CreditOverrideAtUtc" IS NOT NULL
                    AND "CreditLimitSnapshot" IS NOT NULL
                    AND "CreditLimitSnapshot" >= 0
                    AND "CreditOutstandingBefore" IS NOT NULL
                    AND "CreditOutstandingBefore" >= 0
                    AND "CreditProjectedOutstanding" IS NOT NULL
                    AND "CreditProjectedOutstanding" > "CreditLimitSnapshot")
                """);
            table.HasTrigger("sale_credit_override_snapshot");
        });
        builder.HasKey(sale => sale.Id);
        builder.HasAlternateKey(sale => new { sale.Id, sale.CompanyId }).HasName("AK_sale_id_company");
        builder.Property(sale => sale.Number).HasMaxLength(32).IsRequired();
        builder.Property(sale => sale.Status).HasMaxLength(32).IsRequired();
        builder.Property(sale => sale.Subtotal).HasColumnType("numeric(19,4)");
        builder.Property(sale => sale.DiscountAmount).HasColumnType("numeric(19,4)");
        builder.Property(sale => sale.DiscountKind).HasMaxLength(16).IsRequired().HasDefaultValue(DiscountKinds.None);
        builder.Property(sale => sale.DiscountRate).HasColumnType("numeric(7,4)");
        builder.Property(sale => sale.Total).HasColumnType("numeric(19,4)");
        builder.Property(sale => sale.CustomerName).HasMaxLength(CustomerValidator.NameMaxLength);
        builder.Property(sale => sale.CustomerType).HasMaxLength(16);
        builder.Property(sale => sale.CreditLimitOverridden).HasDefaultValue(false);
        builder.Property(sale => sale.CreditOverrideReason).HasMaxLength(CustomerCredit.ReasonMaxLength);
        builder.Property(sale => sale.CreditLimitSnapshot).HasColumnType("numeric(19,4)");
        builder.Property(sale => sale.CreditOutstandingBefore).HasColumnType("numeric(19,4)");
        builder.Property(sale => sale.CreditProjectedOutstanding).HasColumnType("numeric(19,4)");
        builder.HasIndex(sale => new { sale.CompanyId, sale.Number }).IsUnique().HasDatabaseName("UX_sale_company_number");
        builder.HasIndex(sale => new { sale.CompanyId, sale.CustomerId, sale.CreatedAtUtc })
            .HasDatabaseName("IX_sale_company_customer_created");
        builder.HasIndex(sale => new { sale.CompanyId, sale.CreatedAtUtc });
        builder.HasOne<Company>().WithMany().HasForeignKey(sale => sale.CompanyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(sale => new { sale.CustomerId, sale.CompanyId })
            .HasPrincipalKey(customer => new { customer.Id, customer.CompanyId })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_sale_customer");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sale => sale.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sale => sale.CreditOverrideByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
