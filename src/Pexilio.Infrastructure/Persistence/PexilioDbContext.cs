using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pexilio.Domain.Catalog;
using Pexilio.Domain.Company;
using Pexilio.Domain.Customers;
using Pexilio.Domain.Purchasing;
using Pexilio.Domain.Sales;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Auth;
using Pexilio.Infrastructure.Identity;

namespace Pexilio.Infrastructure.Persistence;

public sealed class PexilioDbContext(DbContextOptions<PexilioDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<StockLocation> StockLocations => Set<StockLocation>();

    public DbSet<StockBalance> StockBalances => Set<StockBalance>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<StockIdempotency> StockIdempotencies => Set<StockIdempotency>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerIdempotency> CustomerIdempotencies => Set<CustomerIdempotency>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<Purchase> Purchases => Set<Purchase>();

    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();

    public DbSet<PurchaseIdempotency> PurchaseIdempotencies => Set<PurchaseIdempotency>();

    public DbSet<PurchaseReceipt> PurchaseReceipts => Set<PurchaseReceipt>();

    public DbSet<PurchaseReceiptLine> PurchaseReceiptLines => Set<PurchaseReceiptLine>();

    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<SaleLine> SaleLines => Set<SaleLine>();

    public DbSet<SalePayment> SalePayments => Set<SalePayment>();

    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();

    public DbSet<SaleIdempotency> SaleIdempotencies => Set<SaleIdempotency>();

    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();

    public DbSet<SaleReturnLine> SaleReturnLines => Set<SaleReturnLine>();

    public DbSet<SaleRefund> SaleRefunds => Set<SaleRefund>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("pexilio");
        modelBuilder.HasSequence<long>("purchase_number_seq", "pexilio");
        modelBuilder.HasSequence<long>("purchase_receipt_number_seq", "pexilio");
        modelBuilder.HasSequence<long>("sale_number_seq", "pexilio");
        modelBuilder.HasSequence<long>("sale_return_number_seq", "pexilio");
        modelBuilder.Entity<AppUser>().ToTable("AspNetUsers", "pexilio");
        modelBuilder.Entity<AppRole>().ToTable("AspNetRoles", "pexilio");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("AspNetUserRoles", "pexilio");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("AspNetUserClaims", "pexilio");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("AspNetUserLogins", "pexilio");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("AspNetUserTokens", "pexilio");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("AspNetRoleClaims", "pexilio");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PexilioDbContext).Assembly);
    }
}
