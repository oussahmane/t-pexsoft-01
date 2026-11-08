using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pexilio.Application;
using Pexilio.Domain.Sales;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class CashSaleApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SaleLockGate _lockGate = new();

    private HealthApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new HealthApiFactory(
            LocalEnvironment.TestConnectionString(),
            services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(ISaleProductLock)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ISaleProductLock>(_lockGate);
            });
        await TestDatabase.PrepareAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task A_cash_sale_of_several_tracked_products_posts_one_movement_each()
    {
        using var client = await AdministratorClientAsync();
        var water = await CreateProductAsync(client, "EAU-S1", true, true);
        var flour = await CreateProductAsync(client, "FAR-S1", true, true);
        await OpenAsync(client, water.Id, "4.0000");
        await OpenAsync(client, flour.Id, "1.0000");

        var response = await SendSaleAsync(client, Guid.NewGuid(), "30.0000",
            (water.Id, "2.0000", "10.0000", "20.0000"),
            (flour.Id, "1.0000", "10.0000", "10.0000"));
        var sale = await ReadSaleAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("V-", sale.Number, StringComparison.Ordinal);
        Assert.Equal("30.0000", sale.Total);
        Assert.Equal("0.0000", sale.DiscountAmount);
        Assert.Equal(2, sale.Lines.Count);
        Assert.All(sale.Lines, line => Assert.True(line.TracksStock));
        Assert.All(sale.Lines, line => Assert.NotNull(line.StockMovementId));
        Assert.Equal("cash", Assert.Single(sale.Payments).Method);
        Assert.Equal("30.0000", sale.PaidAmount);
        Assert.Equal("0.0000", sale.OutstandingAmount);
        Assert.Equal("paid", sale.PaymentState);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(2, await database.SaleLines.CountAsync(line => line.SaleId == sale.Id));
        var movements = await database.StockMovements.Where(movement => movement.SourceType == StockSourceTypes.Sale).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.All(movements, movement => Assert.True(movement.QuantityDelta < 0));
        Assert.Equal(-2.0000m, movements.Single(movement => movement.ProductId == water.Id).QuantityDelta);
        Assert.Equal(2.0000m, await database.StockBalances.Where(balance => balance.ProductId == water.Id).Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(0.0000m, await database.StockBalances.Where(balance => balance.ProductId == flour.Id).Select(balance => balance.Quantity).SingleAsync());
        var listed = await client.GetFromJsonAsync<List<SaleSummaryResponse>>("/api/v1/sales", JsonOptions);
        var summary = Assert.Single(listed!);
        Assert.Equal(sale.Id, summary.Id);
        Assert.Equal("paid", summary.PaymentState);
        Assert.Equal("0.0000", summary.OutstandingAmount);
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        Assert.Equal("Eau", detail!.Lines[0].ProductName);
        Assert.Equal("paid", detail.PaymentState);
        Assert.Equal("0.0000", detail.OutstandingAmount);
    }

    [Fact]
    public async Task An_untracked_product_sells_without_a_movement()
    {
        using var client = await AdministratorClientAsync();
        var service = await CreateProductAsync(client, "SRV-S1", true, false);
        var response = await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (service.Id, "1.0000", "10.0000", "10.0000"));
        var sale = await ReadSaleAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var line = Assert.Single(sale.Lines);
        Assert.False(line.TracksStock);
        Assert.Null(line.StockMovementId);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
    }

    [Fact]
    public async Task Inactive_duplicate_price_and_stock_failures_leave_no_sale()
    {
        using var client = await AdministratorClientAsync();
        var inactive = await CreateProductAsync(client, "EAU-S2", false, true);
        var tracked = await CreateProductAsync(client, "EAU-S3", true, true);
        var rejectedInactive = await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (inactive.Id, "1.0000", "10.0000", "10.0000"));
        var duplicate = await SendSaleAsync(client, Guid.NewGuid(), "20.0000",
            (tracked.Id, "1.0000", "10.0000", "10.0000"),
            (tracked.Id, "1.0000", "10.0000", "10.0000"));
        var wrongPrice = await SendSaleAsync(client, Guid.NewGuid(), "9.0000", (tracked.Id, "1.0000", "9.0000", "9.0000"));
        var wrongTotal = await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (tracked.Id, "2.0000", "10.0000", "15.0000"));
        var insufficient = await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (tracked.Id, "1.0000", "10.0000", "10.0000"));

        Assert.Equal("sale.product_inactive", await CodeAsync(rejectedInactive));
        Assert.Equal("lines.duplicate", await FieldAsync(duplicate));
        Assert.Equal("unitPrice.mismatch", await FieldAsync(wrongPrice));
        Assert.Equal("lineTotal.mismatch", await FieldAsync(wrongTotal));
        Assert.Equal("stock.insufficient", await CodeAsync(insufficient));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.Sales.CountAsync());
        Assert.Equal(0, await database.SalePayments.CountAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
    }

    [Fact]
    public async Task The_same_key_replays_and_a_different_body_conflicts()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S4", true, true);
        await OpenAsync(client, product.Id, "5.0000");
        var key = Guid.NewGuid();
        var first = await SendSaleAsync(client, key, "10.0000", (product.Id, "1.0000", "10.0000", "10.0000"));
        var replay = await SendSaleAsync(client, key, "10.0000", (product.Id, "1.0000", "10.0000", "10.0000"));
        var conflict = await SendSaleAsync(client, key, "20.0000", (product.Id, "2.0000", "10.0000", "20.0000"));
        var original = await ReadSaleAsync(first);
        var repeated = await ReadSaleAsync(replay);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original.Id, repeated.Id);
        Assert.Equal("10.0000", repeated.PaidAmount);
        Assert.Equal("0.0000", repeated.OutstandingAmount);
        Assert.Equal("paid", repeated.PaymentState);
        Assert.Equal("sale.idempotency_conflict", await CodeAsync(conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Sales.CountAsync());
        Assert.Equal(1, await database.StockMovements.CountAsync(movement => movement.SourceType == StockSourceTypes.Sale));
        Assert.Equal(4.0000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task A_catalog_edit_does_not_change_the_historical_snapshot()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S5", true, true);
        await OpenAsync(client, product.Id, "2.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (product.Id, "1.0000", "10.0000", "10.0000")));
        var updated = await client.PatchAsJsonAsync($"/api/v1/products/{product.Id}", new
        {
            sku = "EAU-S5",
            name = "Autre",
            unit = "kg",
            unitPrice = "12.0000",
            trackStock = false,
            isActive = true,
        });
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var line = Assert.Single(detail!.Lines);
        Assert.Equal("Eau", line.ProductName);
        Assert.Equal("u", line.ProductUnit);
        Assert.Equal("10.0000", line.UnitPrice);
        Assert.True(line.TracksStock);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.False(await database.Products.Where(item => item.Id == product.Id).Select(item => item.TrackStock).SingleAsync());
        Assert.True(await database.SaleLines.Where(item => item.Id == line.Id).Select(item => item.TracksStock).SingleAsync());
    }

    [Fact]
    public async Task A_product_update_waits_for_the_sale_lock_and_the_sale_uses_the_locked_price()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S6", true, true);
        await OpenAsync(client, product.Id, "3.0000");
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lockGate.Arm(async cancellationToken =>
        {
            locked.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        Task<HttpResponseMessage>? saleTask = null;
        Task<HttpResponseMessage>? patchTask = null;
        try
        {
            saleTask = SendSaleAsync(client, Guid.NewGuid(), "10.0000", (product.Id, "1.0000", "10.0000", "10.0000"));
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            patchTask = client.PatchAsJsonAsync($"/api/v1/products/{product.Id}", new
            {
                sku = "EAU-S6",
                name = "Eau",
                unit = "u",
                unitPrice = "15.0000",
                trackStock = true,
                isActive = true,
            });
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
                await WaitUntilCatalogUpdateBlocksAsync(database);
            }

            Assert.False(patchTask.IsCompleted);
            release.TrySetResult();
            var sold = await ReadSaleAsync(await saleTask);
            var patched = await patchTask;
            var updated = await patched.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);
            var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sold.Id}", JsonOptions);

            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            Assert.Equal("10.0000", Assert.Single(sold.Lines).ListUnitPrice);
            Assert.Equal("10.0000", Assert.Single(detail!.Lines).UnitPrice);
            Assert.Equal("15.0000", updated!.UnitPrice);
        }
        finally
        {
            release.TrySetResult();
            _lockGate.Disarm();
            await DrainAsync(saleTask);
            await DrainAsync(patchTask);
        }
    }

    [Fact]
    public async Task A_movement_inserted_before_a_failed_commit_is_rolled_back()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S7", true, true);
        await OpenAsync(client, product.Id, "5.0000");
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var context = await LoadContextAsync(database, product.Id);
        var movementId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        await using var tx = await database.Database.BeginTransactionAsync();
        database.Sales.Add(CashDocument(context, saleId, lineId, movementId, quantity: 2.0000m, movementDelta: -1.0000m, balanceAfter: 4.0000m));
        database.StockMovements.Add(new StockMovement
        {
            Id = movementId,
            CompanyId = context.CompanyId,
            StockLocationId = context.LocationId,
            ProductId = product.Id,
            MovementType = StockMovementTypes.Sale,
            QuantityDelta = -1.0000m,
            Reason = "Vente incorrecte",
            BalanceAfter = 4.0000m,
            SourceType = StockSourceTypes.Sale,
            SourceId = lineId,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = context.UserId,
        });
        await database.SaveChangesAsync();
        var failure = await Record.ExceptionAsync(() => tx.CommitAsync());

        Assert.NotNull(failure);
        Assert.Contains("sale.movement_integrity", failure.ToString(), StringComparison.Ordinal);
        await using var check = _factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await after.Sales.CountAsync());
        Assert.Equal(0, await after.SalePayments.CountAsync());
        Assert.Equal(1, await after.StockMovements.CountAsync());
        Assert.Equal(5.0000m, await after.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task A_sale_movement_must_reference_its_line_and_a_tracked_line_needs_one()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S8", true, true);
        await OpenAsync(client, product.Id, "5.0000");
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var missing = await LoadContextAsync(database, product.Id);
        await using (var tx = await database.Database.BeginTransactionAsync())
        {
            database.Sales.Add(CashDocument(missing, Guid.NewGuid(), Guid.NewGuid(), movementId: null, quantity: 1.0000m, movementDelta: null, balanceAfter: null));
            await database.SaveChangesAsync();
            var failure = await Record.ExceptionAsync(() => tx.CommitAsync());
            Assert.Contains("sale.movement_integrity", failure!.ToString(), StringComparison.Ordinal);
        }

        database.ChangeTracker.Clear();
        var orphan = await LoadContextAsync(database, product.Id);
        await using (var tx = await database.Database.BeginTransactionAsync())
        {
            var movement = new StockMovement
            {
                Id = Guid.NewGuid(),
                CompanyId = orphan.CompanyId,
                StockLocationId = orphan.LocationId,
                ProductId = product.Id,
                MovementType = StockMovementTypes.Sale,
                QuantityDelta = -1.0000m,
                Reason = "Vente orpheline",
                BalanceAfter = 4.0000m,
                SourceType = StockSourceTypes.Sale,
                SourceId = Guid.NewGuid(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CreatedByUserId = orphan.UserId,
            };
            orphan.Balance.Quantity = 4.0000m;
            database.StockMovements.Add(movement);
            await database.SaveChangesAsync();
            var failure = await Record.ExceptionAsync(() => tx.CommitAsync());
            Assert.Contains("sale.movement_integrity", failure!.ToString(), StringComparison.Ordinal);
        }

        await using var check = _factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await after.Sales.CountAsync());
        Assert.Equal(5.0000m, await after.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task An_untracked_line_rejects_a_movement_and_a_return_keeps_the_sale_snapshot()
    {
        using var client = await AdministratorClientAsync();
        var service = await CreateProductAsync(client, "SRV-S2", true, false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (service.Id, "1.0000", "10.0000", "10.0000")));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var line = await database.SaleLines.AsNoTracking().SingleAsync();
        var userId = await database.Set<AppUser>().Select(user => user.Id).SingleAsync();
        var locationId = Guid.NewGuid();
        database.StockLocations.Add(new StockLocation
        {
            Id = locationId,
            CompanyId = line.CompanyId,
            Code = "MAIN",
            Name = "Principal",
            IsDefault = true,
            PreventNegativeStock = false,
        });
        database.StockBalances.Add(new StockBalance
        {
            Id = Guid.NewGuid(),
            StockLocationId = locationId,
            ProductId = service.Id,
            Quantity = -1.0000m,
        });
        database.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            CompanyId = line.CompanyId,
            StockLocationId = locationId,
            ProductId = service.Id,
            MovementType = StockMovementTypes.Sale,
            QuantityDelta = -1.0000m,
            Reason = "Vente interdite",
            BalanceAfter = -1.0000m,
            SourceType = StockSourceTypes.Sale,
            SourceId = line.Id,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
        });
        await using (var tx = await database.Database.BeginTransactionAsync())
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                await database.SaveChangesAsync();
                await tx.CommitAsync();
            });
            Assert.Contains("sale.movement_integrity", failure!.ToString(), StringComparison.Ordinal);
        }

        database.ChangeTracker.Clear();
        database.ChangeTracker.Clear();
        var posted = await database.SaleLines.AsNoTracking().SingleAsync();
        var returnId = Guid.NewGuid();
        await using (var tx = await database.Database.BeginTransactionAsync())
        {
            database.SaleReturns.Add(new SaleReturn
            {
                Id = returnId,
                CompanyId = posted.CompanyId,
                SaleId = posted.SaleId,
                Number = "AV-000001",
                Reason = "Test",
                ReturnedAtUtc = DateTimeOffset.UtcNow,
                ReturnedByUserId = userId,
                Lines =
                [
                    new SaleReturnLine
                    {
                        Id = Guid.NewGuid(),
                        ReturnId = returnId,
                        SaleId = posted.SaleId,
                        SaleLineId = posted.Id,
                        CompanyId = posted.CompanyId,
                        ProductId = posted.ProductId,
                        Quantity = 1.0000m,
                        Amount = 0.0000m,
                        TracksStock = true,
                    },
                ],
            });
            var failure = await Record.ExceptionAsync(async () =>
            {
                await database.SaveChangesAsync();
                await tx.CommitAsync();
            });
            Assert.Contains("sale.return_movement_integrity", failure!.ToString(), StringComparison.Ordinal);
        }

        await using var check = _factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await after.Sales.CountAsync());
        Assert.Equal(0, await after.SaleReturns.CountAsync());
        Assert.Equal(0, await after.StockMovements.CountAsync());
        Assert.Equal(sale.Id, (await after.Sales.SingleAsync()).Id);
    }

    [Fact]
    public async Task Two_movements_for_one_product_still_fail_stock_reconciliation()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-S9", true, true);
        await OpenAsync(client, product.Id, "10.0000");
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var userId = await database.StockMovements.Select(movement => movement.CreatedByUserId).SingleAsync();
        var ledger = scope.ServiceProvider.GetRequiredService<IStockService>();
        await using var tx = await database.Database.BeginTransactionAsync();
        var first = await ledger.PostInCurrentTransactionAsync(
            StockMovementTypes.Sale, product.Id, -1.0000m, "Vente double", null, userId, StockSourceTypes.Sale, Guid.NewGuid(), CancellationToken.None);
        var second = await ledger.PostInCurrentTransactionAsync(
            StockMovementTypes.Sale, product.Id, -1.0000m, "Vente double", null, userId, StockSourceTypes.Sale, Guid.NewGuid(), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => tx.CommitAsync());

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Contains("stock.reconciliation", failure!.ToString(), StringComparison.Ordinal);
        await using var check = _factory.Services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(10.0000m, await after.StockBalances.Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(1, await after.StockMovements.CountAsync());
    }

    [Fact]
    public async Task A_cashier_can_sell_and_an_anonymous_caller_cannot()
    {
        using var admin = await AdministratorClientAsync();
        var product = await CreateProductAsync(admin, "SRV-S3", true, false);
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier.sale@example.com", password = "cashier-password-1" });
        using var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "cashier.sale@example.com", password = "cashier-password-1" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        using var cashier = _factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var sold = await SendSaleAsync(cashier, Guid.NewGuid(), "10.0000", (product.Id, "1.0000", "10.0000", "10.0000"));
        var hidden = await anonymous.GetAsync("/api/v1/sales");

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hidden.StatusCode);
    }

    [Fact]
    public async Task A_customer_linked_cash_sale_snapshots_the_name_and_a_walk_in_sale_omits_it()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-C1", true, true);
        await OpenAsync(client, product.Id, "2.0000");
        var customer = await CreateCustomerAsync(client, "Amin", "individual", "0551000001");
        var duplicate = await CreateCustomerAsync(client, "Amin", "business", "0551000002");
        var key = Guid.NewGuid();

        var first = await SendCustomerSaleAsync(client, key, product.Id, customer.Id);
        var replay = await SendCustomerSaleAsync(client, key, product.Id, customer.Id);
        var sold = await ReadSaleAsync(first);
        var repeated = await ReadSaleAsync(replay);
        var renamed = await client.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", new
        {
            type = "individual",
            name = "Amin Karim",
            phone = "0551000001",
        });
        var stored = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sold.Id}", JsonOptions);
        var walkIn = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (product.Id, "1.0000", "10.0000", "10.0000")));
        var inactiveCustomer = await CreateCustomerAsync(client, "Inactif", "individual", null);
        await client.PutAsJsonAsync($"/api/v1/customers/{inactiveCustomer.Id}", new
        {
            type = "individual",
            name = "Inactif",
            isActive = false,
        });
        var rejected = await SendCustomerSaleAsync(client, Guid.NewGuid(), product.Id, inactiveCustomer.Id);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(sold.Id, repeated.Id);
        Assert.Equal("paid", sold.PaymentState);
        Assert.Equal("paid", repeated.PaymentState);
        Assert.Equal("10.0000", repeated.PaidAmount);
        Assert.Equal("0.0000", repeated.OutstandingAmount);
        Assert.Equal(customer.Id, sold.CustomerId);
        Assert.Equal("Amin", sold.CustomerName);
        Assert.Equal("individual", sold.CustomerType);
        Assert.Equal("10.0000", Assert.Single(sold.Payments).Amount);
        Assert.Equal("Amin", stored!.CustomerName);
        Assert.Equal("individual", stored.CustomerType);
        Assert.Null(walkIn.CustomerId);
        Assert.Equal("paid", walkIn.PaymentState);
        Assert.Equal("10.0000", walkIn.PaidAmount);
        Assert.Equal("0.0000", walkIn.OutstandingAmount);
        Assert.Null(walkIn.CustomerName);
        Assert.Null(walkIn.CustomerType);
        Assert.NotEqual(customer.Id, duplicate.Id);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("customer.inactive", await CodeAsync(rejected));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(2, await database.Sales.CountAsync());
        Assert.Equal(1, await database.SaleIdempotencies.CountAsync(item => item.SaleId == sold.Id));
        Assert.Equal(2, await database.StockMovements.CountAsync(item => item.SourceType == "sale"));
    }

    [Fact]
    public async Task A_customer_sale_can_be_partial_or_unpaid_and_the_same_key_replays_it()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-C2", true, true);
        await OpenAsync(client, product.Id, "4.0000");
        var customer = await CreateCustomerAsync(client, "Sara", "individual", "0551000003");
        var partialKey = Guid.NewGuid();

        var partial = await ReadSaleAsync(await SendTenderAsync(client, partialKey, product.Id, customer.Id, "transfer", "4.0000"));
        var partialReplay = await ReadSaleAsync(await SendTenderAsync(client, partialKey, product.Id, customer.Id, "transfer", "4.0000"));
        var unpaid = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, null, null));
        var card = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, "card", "10.0000"));
        var other = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, "other", "10.0000"));
        var walkInTransfer = await SendTenderAsync(client, Guid.NewGuid(), product.Id, null, "transfer", "10.0000");
        var above = await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, "cash", "10.0001");
        var emptyCustomer = await SendTenderAsync(client, Guid.NewGuid(), product.Id, Guid.Empty, "cash", "10.0000");

        Assert.Equal(partial.Id, partialReplay.Id);
        Assert.Equal("partial", partialReplay.PaymentState);
        Assert.Equal("4.0000", partialReplay.PaidAmount);
        Assert.Equal("6.0000", partialReplay.OutstandingAmount);
        Assert.Equal("transfer", Assert.Single(partialReplay.Payments).Method);
        Assert.Equal("unpaid", unpaid.PaymentState);
        Assert.Equal("0.0000", unpaid.PaidAmount);
        Assert.Equal("10.0000", unpaid.OutstandingAmount);
        Assert.Empty(unpaid.Payments);
        Assert.Equal("paid", card.PaymentState);
        Assert.Equal("card", Assert.Single(card.Payments).Method);
        Assert.Equal("other", Assert.Single(other.Payments).Method);
        Assert.Equal("payment.method", await FieldAsync(walkInTransfer));
        Assert.Equal("payment.total", await FieldAsync(above));
        Assert.Equal("customer.invalid", await FieldAsync(emptyCustomer));
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{unpaid.Id}", JsonOptions);
        Assert.Equal("unpaid", detail!.PaymentState);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(4, await database.Sales.CountAsync());
        Assert.Equal(1, await database.SalePayments.CountAsync(payment => payment.SaleId == partial.Id));
        Assert.Equal(0, await database.SalePayments.CountAsync(payment => payment.SaleId == unpaid.Id));
        Assert.Equal(4, await database.StockMovements.CountAsync(item => item.SourceType == "sale"));
    }

    [Fact]
    public async Task A_customer_payment_allocates_one_receipt_and_replay_returns_it()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P1", true, true);
        await OpenAsync(client, product.Id, "2.0000");
        var customer = await CreateCustomerAsync(client, "Nadia", "individual", "0551000004");
        var sale = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, null, null));
        var key = Guid.NewGuid();

        var first = await SendPaymentAsync(client, sale.Id, key, "4.0000", "transfer", "VIR-1", "Acompte");
        var replay = await SendPaymentAsync(client, sale.Id, key, "4.0000", "transfer", "VIR-1", "Acompte");
        var original = await ReadPaymentAsync(first);
        var repeated = await ReadPaymentAsync(replay);
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);

        Assert.Equal(original.Payment.Id, repeated.Payment.Id);
        Assert.Equal("R-000001", repeated.Payment.ReceiptNumber);
        Assert.Equal("transfer", repeated.Payment.Method);
        Assert.Equal("4.0000", repeated.Payment.Amount);
        Assert.Equal("VIR-1", repeated.Payment.Reference);
        Assert.Equal("Acompte", repeated.Payment.Notes);
        Assert.Equal("4.0000", repeated.PaidAmount);
        Assert.Equal("6.0000", repeated.OutstandingAmount);
        Assert.Equal("partial", repeated.PaymentState);
        Assert.Equal("R-000001", detail!.Payments.Single(payment => payment.Id == original.Payment.Id).ReceiptNumber);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SalePayments.CountAsync(payment => payment.SaleId == sale.Id));
        Assert.Equal(1L, await database.DocumentCounters.Select(counter => counter.LastValue).SingleAsync());
        Assert.Equal(1, await database.StockMovements.CountAsync(movement => movement.SourceType == "sale"));
    }

    [Fact]
    public async Task A_different_payment_body_conflicts_and_an_overpayment_does_not_consume_a_number()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P2", true, true);
        await OpenAsync(client, product.Id, "1.0000");
        var customer = await CreateCustomerAsync(client, "Nadia", "individual", null);
        var sale = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, null, null));
        var key = Guid.NewGuid();
        var created = await ReadPaymentAsync(await SendPaymentAsync(client, sale.Id, key, "4.0000"));
        var conflict = await SendPaymentAsync(client, sale.Id, key, "5.0000");
        var overpayment = await SendPaymentAsync(client, sale.Id, Guid.NewGuid(), "7.0000");

        Assert.Equal("R-000001", created.Payment.ReceiptNumber);
        Assert.Equal("payment.idempotency_conflict", await CodeAsync(conflict));
        Assert.Equal("payment.exceeds", await CodeAsync(overpayment));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SalePayments.CountAsync());
        Assert.Equal(1L, await database.DocumentCounters.Select(counter => counter.LastValue).SingleAsync());
        Assert.Equal(1, await database.StockMovements.CountAsync(movement => movement.SourceType == "sale"));
    }

    [Fact]
    public async Task A_settled_sale_and_a_walk_in_sale_reject_a_later_payment()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P3", true, true);
        await OpenAsync(client, product.Id, "2.0000");
        var customer = await CreateCustomerAsync(client, "Nadia", "individual", null);
        var settled = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, "card", "10.0000"));
        var walkIn = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), "10.0000", (product.Id, "1.0000", "10.0000", "10.0000")));
        var settledPayment = await SendPaymentAsync(client, settled.Id, Guid.NewGuid(), "1.0000");
        var walkInPayment = await SendPaymentAsync(client, walkIn.Id, Guid.NewGuid(), "1.0000");

        Assert.Equal("R-000001", Assert.Single(settled.Payments).ReceiptNumber);
        Assert.NotNull(Assert.Single(walkIn.Payments).ReceiptNumber);
        Assert.Equal("payment.settled", await CodeAsync(settledPayment));
        Assert.Equal("payment.customer_required", await CodeAsync(walkInPayment));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(2, await database.SalePayments.CountAsync());
        Assert.Equal(2L, await database.DocumentCounters.Select(counter => counter.LastValue).SingleAsync());
    }

    [Fact]
    public async Task An_inactive_customer_can_still_pay_an_open_sale()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P4", true, false);
        var customer = await CreateCustomerAsync(client, "Inactif", "individual", null);
        var sale = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, null, null));
        var deactivated = await client.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", new
        {
            type = "individual",
            name = "Inactif",
            isActive = false,
        });
        var paid = await ReadPaymentAsync(await SendPaymentAsync(client, sale.Id, Guid.NewGuid(), "10.0000", "other"));

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("paid", paid.PaymentState);
        Assert.Equal("0.0000", paid.OutstandingAmount);
        Assert.Equal("R-000001", paid.Payment.ReceiptNumber);
    }

    [Fact]
    public async Task Concurrent_payments_cannot_overpay_the_same_sale()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P5", true, true);
        await OpenAsync(client, product.Id, "1.0000");
        var customer = await CreateCustomerAsync(client, "Nadia", "individual", null);
        var sale = await ReadSaleAsync(await SendTenderAsync(client, Guid.NewGuid(), product.Id, customer.Id, null, null));
        var left = SendPaymentAsync(client, sale.Id, Guid.NewGuid(), "6.0000");
        var right = SendPaymentAsync(client, sale.Id, Guid.NewGuid(), "6.0000");
        var responses = await Task.WhenAll(left, right);
        var statuses = responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray();

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], statuses);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("payment.exceeds", await CodeAsync(conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(6.0000m, await database.SalePayments.Select(payment => payment.Amount).SingleAsync());
        Assert.Equal(1L, await database.DocumentCounters.Select(counter => counter.LastValue).SingleAsync());
        Assert.Equal(1, await database.StockMovements.CountAsync(movement => movement.SourceType == "sale"));
    }

    [Fact]
    public async Task A_rolled_back_receipt_allocation_does_not_consume_the_number()
    {
        using var client = await AdministratorClientAsync();
        await CreateProductAsync(client, "EAU-P6", true, true);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var companyId = await database.Companies.Select(company => company.Id).SingleAsync();
        await using (var abandoned = await database.Database.BeginTransactionAsync())
        {
            Assert.Equal(1L, await NextReceiptAsync(database, companyId));
            await abandoned.RollbackAsync();
        }

        await using var committed = await database.Database.BeginTransactionAsync();
        Assert.Equal(1L, await NextReceiptAsync(database, companyId));
        await committed.CommitAsync();
        Assert.Equal(1L, await database.DocumentCounters.Select(counter => counter.LastValue).SingleAsync());
    }

    private async Task<HttpClient> AdministratorClientAsync()
    {
        var client = _factory.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            legalName = "Pexilio Démo",
            defaultTaxRate = "0.19",
            email = "owner@example.com",
            password = "correct-horse-battery",
        });
        if (setup.StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Equal("setup.already_complete", await CodeAsync(setup));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        }

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@example.com", password = "correct-horse-battery" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<ProductResponse> CreateProductAsync(HttpClient client, string sku, bool isActive, bool trackStock)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku,
            name = "Eau",
            unit = "u",
            unitPrice = "10.0000",
            trackStock,
            isActive,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions))!;
    }

    private static async Task OpenAsync(HttpClient client, Guid productId, string quantity)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/opening")
        {
            Content = JsonContent.Create(new { productId, quantity, reason = "Ouverture" }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendSaleAsync(
        HttpClient client,
        Guid key,
        string total,
        params (Guid ProductId, string Quantity, string UnitPrice, string LineTotal)[] lines)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(new
            {
                total,
                lines = lines.Select(line => new
                {
                    productId = line.ProductId,
                    quantity = line.Quantity,
                    unitPrice = line.UnitPrice,
                    lineTotal = line.LineTotal,
                }),
                payments = new[] { new { method = "cash", amount = total } },
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static async Task<CustomerResponse> CreateCustomerAsync(
        HttpClient client,
        string name,
        string type,
        string? phone)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customers")
        {
            Content = JsonContent.Create(new { type, name, phone }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> SendTenderAsync(
        HttpClient client,
        Guid key,
        Guid productId,
        Guid? customerId,
        string? method,
        string? amount)
    {
        var line = new { productId, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" };
        object body;
        if (method is null)
        {
            body = new { customerId, total = "10.0000", lines = new[] { line }, payments = Array.Empty<object>() };
        }
        else if (customerId is null)
        {
            body = new { total = "10.0000", lines = new[] { line }, payments = new[] { new { method, amount } } };
        }
        else
        {
            body = new { customerId, total = "10.0000", lines = new[] { line }, payments = new[] { new { method, amount } } };
        }
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> SendCustomerSaleAsync(HttpClient client, Guid key, Guid productId, Guid customerId)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(new
            {
                customerId,
                total = "10.0000",
                lines = new[]
                {
                    new { productId, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" },
                },
                payments = new[] { new { method = "cash", amount = "10.0000" } },
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> SendPaymentAsync(
        HttpClient client,
        Guid saleId,
        Guid key,
        string amount,
        string method = "cash",
        string? reference = null,
        string? notes = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sales/{saleId}/payments")
        {
            Content = JsonContent.Create(new { method, amount, reference, notes }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static async Task<SalePaymentPostResponse> ReadPaymentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SalePaymentPostResponse>(JsonOptions))!;
    }

    private static async Task<long> NextReceiptAsync(PexilioDbContext database, Guid companyId)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO pexilio.document_counter ("CompanyId", "Series", "LastValue")
            VALUES (@company, 'customer_payment', 1)
            ON CONFLICT ("CompanyId", "Series")
            DO UPDATE SET "LastValue" = pexilio.document_counter."LastValue" + 1
            RETURNING "LastValue"
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "company";
        parameter.Value = companyId;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SaleResponse> ReadSaleAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions))!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        return body?.Code;
    }

    private static async Task<string?> FieldAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        return body?.Errors?.Values.FirstOrDefault()?.FirstOrDefault();
    }

    private static async Task DrainAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private static async Task WaitUntilCatalogUpdateBlocksAsync(PexilioDbContext database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await CatalogUpdateIsWaitingAsync(database))
            {
                return;
            }

            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException("The catalog update did not block on the sale product row lock.");
    }

    private static async Task<bool> CatalogUpdateIsWaitingAsync(PexilioDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        // A blocked FOR UPDATE shows an ungranted transactionid lock. The tuple lock on
        // pexilio.product identifies the row, and the blocking transaction holds that relation.
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity AS waiter
                JOIN pg_locks AS waiting_transaction
                  ON waiting_transaction.pid = waiter.pid
                 AND waiting_transaction.locktype = 'transactionid'
                 AND NOT waiting_transaction.granted
                JOIN pg_locks AS waiting_tuple
                  ON waiting_tuple.pid = waiter.pid
                 AND waiting_tuple.locktype = 'tuple'
                 AND waiting_tuple.relation = 'pexilio.product'::regclass
                JOIN pg_locks AS holding_transaction
                  ON holding_transaction.locktype = 'transactionid'
                 AND holding_transaction.granted
                 AND holding_transaction.transactionid = waiting_transaction.transactionid
                 AND holding_transaction.pid <> waiter.pid
                JOIN pg_locks AS holding_product
                  ON holding_product.pid = holding_transaction.pid
                 AND holding_product.locktype = 'relation'
                 AND holding_product.granted
                 AND holding_product.relation = 'pexilio.product'::regclass
                WHERE waiter.datname = current_database()
                  AND waiter.pid <> pg_backend_pid()
                  AND waiter.wait_event_type = 'Lock'
            )
            """;
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<string> CurrentDatabaseAsync(PexilioDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SaleSeed> LoadContextAsync(PexilioDbContext database, Guid productId)
    {
        var product = await database.Products.SingleAsync(item => item.Id == productId);
        var balance = await database.StockBalances.SingleAsync(item => item.ProductId == productId);
        return new SaleSeed(
            product.CompanyId,
            product.TaxRateId,
            0.190000m,
            balance.StockLocationId,
            await database.Set<AppUser>().Select(user => user.Id).SingleAsync(),
            balance);
    }

    private static Sale CashDocument(
        SaleSeed seed,
        Guid saleId,
        Guid lineId,
        Guid? movementId,
        decimal quantity,
        decimal? movementDelta,
        decimal? balanceAfter)
    {
        var now = DateTimeOffset.UtcNow;
        var total = quantity * 10.0000m;
        var sale = new Sale
        {
            Id = saleId,
            CompanyId = seed.CompanyId,
            Number = "V-" + saleId.ToString("N")[..6],
            Status = SaleStatuses.Posted,
            Subtotal = total,
            DiscountAmount = 0m,
            Total = total,
            CreatedAtUtc = now,
            CreatedByUserId = seed.UserId,
            Lines =
            [
                new SaleLine
                {
                    Id = lineId,
                    SaleId = saleId,
                    CompanyId = seed.CompanyId,
                    ProductId = seed.Balance.ProductId,
                    LineNumber = 1,
                    Quantity = quantity,
                    ListUnitPrice = 10.0000m,
                    UnitPrice = 10.0000m,
                    LineDiscount = 0m,
                    AllocatedSaleDiscount = 0m,
                    LineTotal = total,
                    ProductSku = "EAU",
                    ProductName = "Eau",
                    ProductUnit = "u",
                    TracksStock = true,
                    TaxRateId = seed.TaxRateId,
                    TaxRate = seed.TaxRate,
                    StockMovementId = movementId,
                },
            ],
            Payments =
            [
                new SalePayment
                {
                    Id = Guid.NewGuid(),
                    SaleId = saleId,
                    CompanyId = seed.CompanyId,
                    Method = SaleRules.CashMethod,
                    Amount = total,
                    PaidAtUtc = now,
                    ReceivedByUserId = seed.UserId,
                },
            ],
        };
        if (movementId is Guid posted && movementDelta is decimal delta && balanceAfter is decimal after)
        {
            sale.Lines[0].StockMovementId = posted;
            seed.Balance.Quantity = after;
        }

        return sale;
    }

    private sealed class SaleLockGate : ISaleProductLock
    {
        private Func<CancellationToken, ValueTask>? _hold;

        public void Arm(Func<CancellationToken, ValueTask> hold) => _hold = hold;

        public void Disarm() => _hold = null;

        public ValueTask AfterProductLockAsync(CancellationToken cancellationToken) =>
            _hold is null ? ValueTask.CompletedTask : _hold(cancellationToken);
    }

    private sealed record ProblemBody(string? Code, Dictionary<string, string[]>? Errors);

    private sealed record SaleSeed(
        Guid CompanyId,
        Guid TaxRateId,
        decimal TaxRate,
        Guid LocationId,
        Guid UserId,
        StockBalance Balance);
}
