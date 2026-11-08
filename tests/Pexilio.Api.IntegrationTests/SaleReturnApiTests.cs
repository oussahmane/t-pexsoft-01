using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pexilio.Application;
using Pexilio.Domain.Accounts;
using Pexilio.Domain.Sales;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class SaleReturnApiTests : IAsyncLifetime
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
    public async Task A_paid_walk_in_return_restores_stock_and_keeps_paid_amount_gross()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-FULL", true);
        await OpenAsync(client, product.Id, "1.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var line = Assert.Single(sale.Lines);

        var response = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Client a changé d'avis",
            [(line.Id, "1.0000")],
            [("cash", "10.0000", "CAISSE", null)],
            clientAmount: "999.0000");
        var posted = await ReadReturnAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("AV-", posted.Number, StringComparison.Ordinal);
        Assert.Equal("10.0000", posted.ReturnAmount);
        Assert.Equal("10.0000", posted.RequiredRefund);
        Assert.Equal("0.0000", posted.NetAccountReduction);
        Assert.Equal("10.0000", posted.PaidAmount);
        Assert.Equal("10.0000", posted.RefundedAmount);
        Assert.Equal("0.0000", posted.OutstandingAmount);
        Assert.Equal("paid", posted.PaymentState);
        var returnedLine = Assert.Single(posted.Lines);
        Assert.Equal("10.0000", returnedLine.Amount);
        Assert.NotNull(returnedLine.StockMovementId);
        var refund = Assert.Single(posted.Refunds);
        Assert.Equal("cash", refund.Method);
        Assert.Equal("CAISSE", refund.Reference);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(1.0000m, await database.StockBalances.Where(balance => balance.ProductId == product.Id).Select(balance => balance.Quantity).SingleAsync());
        var movement = await database.StockMovements.SingleAsync(item => item.SourceType == StockSourceTypes.SaleReturn);
        Assert.Equal(StockMovementTypes.SaleReturn, movement.MovementType);
        Assert.Equal(1.0000m, movement.QuantityDelta);
        Assert.Equal(returnedLine.Id, movement.SourceId);
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        Assert.Equal("10.0000", detail!.PaidAmount);
        Assert.Equal("10.0000", detail.RefundedAmount);
        Assert.Equal("0.0000", detail.OutstandingAmount);
    }

    [Fact]
    public async Task An_untracked_product_returns_without_a_stock_movement()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-FREE", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var posted = await ReadReturnAsync(await SendReturnAsync(
            client,
            sale.Id,
            Guid.NewGuid(),
            "Service annulé",
            [(sale.Lines[0].Id, "1.0000")],
            [("cash", "10.0000", null, null)]));

        Assert.False(Assert.Single(posted.Lines).TracksStock);
        Assert.Null(Assert.Single(posted.Lines).StockMovementId);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.StockMovements.CountAsync(movement => movement.SourceType == StockSourceTypes.SaleReturn && movement.ProductId == product.Id));
    }

    [Fact]
    public async Task Partial_returns_sum_to_the_line_and_a_further_return_is_rejected()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Partiel", null);
        var product = await CreateProductAsync(client, "AV-PART", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "2.0000", "10.0000", "20.0000", "20.0000", payment: null));
        var lineId = sale.Lines[0].Id;
        var first = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Moitié", [(lineId, "1.0000")], null));
        var second = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Reste", [(lineId, "1.0000")], null));
        var third = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Trop", [(lineId, "0.0001")], null);
        var duplicate = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Double", [(lineId, "0.0001"), (lineId, "0.0001")], null);

        Assert.Equal("10.0000", first.ReturnAmount);
        Assert.Equal("0.0000", first.RequiredRefund);
        Assert.Empty(first.Refunds);
        Assert.Equal("10.0000", second.ReturnAmount);
        Assert.Equal("0.0000", second.OutstandingAmount);
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);
        Assert.Equal("return.exceeds", await FieldAsync(third));
        Assert.Equal("lines.duplicate", await FieldAsync(duplicate));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(2, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(0, await database.SaleRefunds.CountAsync(item => item.SaleId == sale.Id));
    }

    [Fact]
    public async Task The_final_slice_takes_the_remaining_refundable_value()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Arrondi", null);
        var product = await CreateProductAsync(client, "AV-RND", false);
        var sale = await ReadSaleAsync(await SendPricedAsync(client, Guid.NewGuid(), new
        {
            customerId = customer.Id,
            total = "29.9999",
            discountAmount = "0.0001",
            discountKind = "amount",
            lines = new[]
            {
                new
                {
                    productId = product.Id,
                    quantity = "3.0000",
                    priceKind = "retail",
                    unitPrice = "10.0000",
                    lineDiscount = "0.0000",
                    discountKind = "none",
                    lineTotal = "30.0000",
                },
            },
        }));
        var lineId = Assert.Single(sale.Lines).Id;
        Assert.Equal("0.0001", Assert.Single(sale.Lines).AllocatedSaleDiscount);
        var first = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Premier", [(lineId, "1.0000")], null));
        var second = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Deuxième", [(lineId, "1.0000")], null));
        var third = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Dernier", [(lineId, "1.0000")], null));

        Assert.Equal("10.0000", first.Lines[0].Amount);
        Assert.Equal("10.0000", second.Lines[0].Amount);
        Assert.Equal("9.9999", third.Lines[0].Amount);
        Assert.Equal(29.9999m, decimal.Parse(first.Lines[0].Amount) + decimal.Parse(second.Lines[0].Amount) + decimal.Parse(third.Lines[0].Amount));
    }

    [Fact]
    public async Task A_half_unit_of_a_tenth_of_a_millime_rounds_the_first_slice_up()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-HALF", false, "0.0001");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "0.0001", "0.0001", "0.0001"));
        var lineId = sale.Lines[0].Id;
        var first = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Moitié", [(lineId, "0.5000")], [("cash", "0.0001", null, null)]));
        var second = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Reste", [(lineId, "0.5000")], null));

        Assert.Equal("0.0001", first.Lines[0].Amount);
        Assert.Equal("0.0001", first.RequiredRefund);
        Assert.Equal("0.0000", second.Lines[0].Amount);
        Assert.Equal("0.0000", second.RequiredRefund);
        Assert.Empty(second.Refunds);
    }

    [Fact]
    public async Task A_fully_discounted_line_returns_zero_and_creates_no_refund()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Remise", null);
        var gift = await CreateProductAsync(client, "AV-GIFT", false);
        var paid = await CreateProductAsync(client, "AV-PAID", false);
        var sale = await ReadSaleAsync(await SendPricedAsync(client, Guid.NewGuid(), new
        {
            customerId = customer.Id,
            total = "10.0000",
            discountAmount = "0.0000",
            discountKind = "none",
            lines = new object[]
            {
                new
                {
                    productId = gift.Id,
                    quantity = "1.0000",
                    priceKind = "retail",
                    unitPrice = "10.0000",
                    lineDiscount = "10.0000",
                    discountKind = "percent",
                    discountRate = "100.0000",
                    lineTotal = "0.0000",
                },
                new
                {
                    productId = paid.Id,
                    quantity = "1.0000",
                    priceKind = "retail",
                    unitPrice = "10.0000",
                    lineDiscount = "0.0000",
                    discountKind = "none",
                    lineTotal = "10.0000",
                },
            },
        }));
        var giftLine = sale.Lines.Single(line => line.ProductId == gift.Id);
        var posted = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Cadeau", [(giftLine.Id, "1.0000")], null));

        Assert.Equal("0.0000", posted.ReturnAmount);
        Assert.Equal("0.0000", posted.RequiredRefund);
        Assert.Empty(posted.Refunds);
        Assert.Equal("10.0000", posted.OutstandingAmount);
    }

    [Fact]
    public async Task A_partial_payment_refunds_only_the_amount_above_outstanding()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Acompte", null);
        var product = await CreateProductAsync(client, "AV-PAY", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: "4.0000"));
        var lineId = sale.Lines[0].Id;
        var inside = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Dans le solde", [(lineId, "0.3000")], null));
        var excess = await ReadReturnAsync(await SendReturnAsync(
            client,
            sale.Id,
            Guid.NewGuid(),
            "Au-delà",
            [(lineId, "0.4000")],
            [("cash", "1.0000", null, null)]));
        var over = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Trop rendu", [(lineId, "0.3000")], [("cash", "4.0000", null, null)]);

        Assert.Equal("3.0000", inside.ReturnAmount);
        Assert.Equal("0.0000", inside.RequiredRefund);
        Assert.Equal("3.0000", inside.OutstandingAmount);
        Assert.Equal("4.0000", inside.PaidAmount);
        Assert.Equal("4.0000", excess.ReturnAmount);
        Assert.Equal("1.0000", excess.RequiredRefund);
        Assert.Equal("1.0000", excess.RefundedAmount);
        Assert.Equal("0.0000", excess.OutstandingAmount);
        Assert.Equal("4.0000", excess.PaidAmount);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal("refund.over", await FieldAsync(over));
    }

    [Fact]
    public async Task An_unpaid_return_reduces_the_statement_by_one_net_event()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Impayé", "20.0000");
        var product = await CreateProductAsync(client, "AV-DUE", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: null));
        var posted = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Avoir", [(sale.Lines[0].Id, "0.4000")], null));
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        var statement = await client.GetFromJsonAsync<CustomerStatementResponse>($"/api/v1/customers/{customer.Id}/statement", JsonOptions);

        Assert.Equal("4.0000", posted.ReturnAmount);
        Assert.Equal("0.0000", posted.RequiredRefund);
        Assert.Equal("6.0000", account!.OutstandingAmount);
        var movement = Assert.Single(statement!.Lines, line => line.Kind == "return");
        Assert.Equal("-4.0000", movement.Amount);
        Assert.DoesNotContain(statement.Lines, line => line.Kind == "refund");
        Assert.Equal("6.0000", statement.OutstandingAmount);
    }

    [Fact]
    public async Task A_fully_refunded_return_is_one_zero_statement_event_and_preserves_the_credit_snapshot()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Soldé", "1.0000");
        var product = await CreateProductAsync(client, "AV-SNAP", false);
        var created = await SendSaleAsync(
            client,
            Guid.NewGuid(),
            product.Id,
            customer.Id,
            "1.0000",
            "10.0000",
            "10.0000",
            "10.0000",
            payment: null,
            creditOverride: true,
            creditOverrideReason: "Autorisé");
        var sale = await ReadSaleAsync(created);
        Assert.Equal(HttpStatusCode.Created, (await SendPaymentAsync(client, sale.Id, "10.0000")).StatusCode);
        var posted = await ReadReturnAsync(await SendReturnAsync(
            client,
            sale.Id,
            Guid.NewGuid(),
            "Remboursé",
            [(sale.Lines[0].Id, "1.0000")],
            [("cash", "6.0000", null, null), ("transfer", "4.0000", "VIR-9", null)]));
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        var statement = await client.GetFromJsonAsync<CustomerStatementResponse>($"/api/v1/customers/{customer.Id}/statement", JsonOptions);
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);

        Assert.Equal("10.0000", posted.RequiredRefund);
        Assert.Equal("0.0000", posted.NetAccountReduction);
        Assert.Equal(2, posted.Refunds.Count);
        Assert.Equal("10.0000", detail!.PaidAmount);
        Assert.Equal("10.0000", detail.RefundedAmount);
        Assert.Equal(sale.CreditLimitSnapshot, detail.CreditLimitSnapshot);
        Assert.Equal(sale.CreditOutstandingBefore, detail.CreditOutstandingBefore);
        Assert.Equal(sale.CreditProjectedOutstanding, detail.CreditProjectedOutstanding);
        Assert.True(detail.CreditLimitOverridden);
        var movement = Assert.Single(statement!.Lines, line => line.Kind == "return");
        Assert.Equal("0.0000", movement.Amount);
        Assert.Equal("0.0000", account!.OutstandingAmount);
        Assert.DoesNotContain(statement.Lines, line => line.Kind == "refund");
    }

    [Fact]
    public async Task Refund_tenders_must_match_the_required_amount_exactly()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-TEND", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var lineId = sale.Lines[0].Id;
        var duplicate = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Double caisse", [(lineId, "1.0000")], [("cash", "4.0000", null, null), ("cash", "6.0000", null, null)]);
        var other = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Autre", [(lineId, "1.0000")], [("other", "10.0000", null, null)]);
        var under = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Court", [(lineId, "1.0000")], [("card", "9.0000", null, null)]);
        var unexpected = await SendReturnAsync(client, Guid.NewGuid(), Guid.NewGuid(), "Inconnu", [(lineId, "1.0000")], null);
        var customer = await CreateCustomerAsync(client, "Sans rendu", null);
        var unpaid = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: null));
        var tenderOnZero = await SendReturnAsync(client, unpaid.Id, Guid.NewGuid(), "Pas de rendu", [(unpaid.Lines[0].Id, "1.0000")], [("cash", "1.0000", null, null)]);

        Assert.Equal("refunds.duplicate", await FieldAsync(duplicate));
        Assert.Equal("refund.method", await FieldAsync(other));
        Assert.Equal("refund.under", await FieldAsync(under));
        Assert.Equal(HttpStatusCode.NotFound, unexpected.StatusCode);
        Assert.Equal("refund.unexpected", await FieldAsync(tenderOnZero));
    }

    [Fact]
    public async Task An_inactive_customer_can_still_return_a_posted_sale()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Inactif", null);
        var product = await CreateProductAsync(client, "AV-OFF", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: "10.0000"));
        var deactivated = await client.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", new { type = "individual", name = "Inactif", isActive = false });
        var posted = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Après fermeture", [(sale.Lines[0].Id, "1.0000")], [("card", "10.0000", null, null)]));

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("10.0000", posted.RequiredRefund);
        Assert.Equal("card", Assert.Single(posted.Refunds).Method);
    }

    [Fact]
    public async Task The_same_key_replays_the_original_return()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-KEY", true);
        await OpenAsync(client, product.Id, "1.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var key = Guid.NewGuid();
        var first = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, key, "Idempotent", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]));
        var replay = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, key, "Idempotent", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]));
        var conflict = await SendReturnAsync(client, sale.Id, key, "Autre motif", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Number, replay.Number);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("sale.idempotency_conflict", await CodeAsync(conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(1.0000m, await database.StockBalances.Where(balance => balance.ProductId == product.Id).Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Concurrent_returns_of_the_same_quantity_leave_one_document_and_one_movement()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-RACE", true);
        await OpenAsync(client, product.Id, "1.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lockGate.Arm(async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        Task<HttpResponseMessage>? left = null;
        Task<HttpResponseMessage>? right = null;
        try
        {
            left = SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Premier concurrent", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            right = SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Second concurrent", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);
            await Task.Delay(200);
            Assert.False(right.IsCompleted);
            release.TrySetResult();
            var responses = await Task.WhenAll(left, right);
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.BadRequest], responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray());
            Assert.Equal("return.exceeds", await FieldAsync(responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest)));
        }
        finally
        {
            release.TrySetResult();
            _lockGate.Disarm();
            await DrainAsync(left);
            await DrainAsync(right);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(1, await database.SaleRefunds.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(1.0000m, await database.StockBalances.Where(balance => balance.ProductId == product.Id).Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task A_return_frees_credit_for_the_next_sale()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Crédit", "10.0000");
        var product = await CreateProductAsync(client, "AV-CR", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: null));
        var blocked = await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "0.4000", "10.0000", "4.0000", "4.0000", payment: null);
        await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Libère", [(sale.Lines[0].Id, "0.4000")], null));
        var next = await SendSaleAsync(client, Guid.NewGuid(), product.Id, customer.Id, "0.4000", "10.0000", "4.0000", "4.0000", payment: null);

        Assert.Equal("credit.limit_exceeded", await CodeAsync(blocked));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("10.0000", account!.OutstandingAmount);
    }

    [Fact]
    public async Task A_failed_return_rolls_back_the_document_number_and_stock()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-ROLL", true);
        await OpenAsync(client, product.Id, "1.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var before = await ReturnCounterAsync(database);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE pexilio.product SET "TrackStock" = FALSE WHERE "Id" = {product.Id}""");
        var rejected = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Doit échouer", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("stock.not_tracked", await CodeAsync(rejected));
        Assert.Equal(before, await ReturnCounterAsync(database));
        Assert.Equal(0, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(0, await database.SaleRefunds.CountAsync(item => item.SaleId == sale.Id));
        Assert.Equal(0m, await database.StockBalances.Where(balance => balance.ProductId == product.Id).Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Posted_returns_and_refunds_are_append_only_and_an_excessive_refund_is_rejected()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-LOCK", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var posted = await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Figé", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]));
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
            var userId = await database.Set<AppUser>().Select(user => user.Id).SingleAsync();
            var companyId = await database.Companies.Select(company => company.Id).SingleAsync();
            var update = await Assert.ThrowsAnyAsync<Exception>(() => database.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE pexilio.sale_return SET "Reason" = 'changed' WHERE "Id" = {posted.Id}"""));
            Assert.Contains("sale.immutable", update.ToString(), StringComparison.Ordinal);
            await using var extra = await database.Database.BeginTransactionAsync();
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO pexilio.sale_refund
                    ("Id", "CompanyId", "SaleId", "SaleReturnId", "Amount", "Method", "RefundedAtUtc", "RefundedByUserId")
                VALUES ({Guid.NewGuid()}, {companyId}, {sale.Id}, {posted.Id}, {1.0000m}, {"cash"}, {DateTimeOffset.UtcNow}, {userId})
                """);
            var commit = await Assert.ThrowsAnyAsync<Exception>(() => extra.CommitAsync());
            Assert.Contains("sale.payment_integrity", commit.ToString(), StringComparison.Ordinal);
        }

        await using var verify = _factory.Services.CreateAsyncScope();
        var verified = verify.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await verified.SaleRefunds.CountAsync(refund => refund.SaleId == sale.Id));
        Assert.Equal("Figé", await verified.SaleReturns.Where(item => item.Id == posted.Id).Select(item => item.Reason).SingleAsync());
    }

    [Fact]
    public async Task Administrator_with_sales_write_can_post_a_return()
    {
        using var client = await AdministratorClientAsync();
        var sale = await CreateReturnableSaleAsync(client, "AV-AUTH-ADMIN");

        var response = await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Retour administrateur", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
    }

    [Fact]
    public async Task Cashier_with_sales_write_can_post_a_return()
    {
        using var admin = await AdministratorClientAsync();
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier@example.com", password = "cashier-password-1" });
        var sale = await CreateReturnableSaleAsync(admin, "AV-AUTH-CASH");
        using var cashier = await LoginClientAsync("cashier@example.com", "cashier-password-1");

        var response = await SendReturnAsync(cashier, sale.Id, Guid.NewGuid(), "Retour caissier", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.SaleReturns.CountAsync(item => item.SaleId == sale.Id));
    }

    [Fact]
    public async Task A_user_without_sales_write_cannot_post_a_return()
    {
        using var admin = await AdministratorClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/users", new { email = "limited@example.com", password = "limited-password-1" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByEmailAsync("limited@example.com");
            var removed = await userManager.RemoveFromRoleAsync(user!, RoleNames.Cashier);
            Assert.True(removed.Succeeded);
            Assert.Empty(await userManager.GetRolesAsync(user!));
        }

        var sale = await CreateReturnableSaleAsync(admin, "AV-AUTH-NONE");
        using var limited = await LoginClientAsync("limited@example.com", "limited-password-1");
        var response = await SendReturnAsync(limited, sale.Id, Guid.NewGuid(), "Retour refusé", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(response));
        await using var verify = _factory.Services.CreateAsyncScope();
        var database = verify.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.SaleReturns.CountAsync());
    }

    [Fact]
    public async Task An_unauthenticated_return_is_rejected()
    {
        using var admin = await AdministratorClientAsync();
        var sale = await CreateReturnableSaleAsync(admin, "AV-AUTH-ANON");
        using var anonymous = _factory.CreateClient();

        var response = await SendReturnAsync(anonymous, sale.Id, Guid.NewGuid(), "Retour anonyme", [(sale.Lines[0].Id, "1.0000")], [("cash", "10.0000", null, null)]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.required", await CodeAsync(response));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.SaleReturns.CountAsync());
    }

    [Fact]
    public async Task A_walk_in_return_and_refund_leave_the_customer_statement_unchanged()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Compte isolé", null);
        var accountProduct = await CreateProductAsync(client, "AV-ACCT", false);
        await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), accountProduct.Id, customer.Id, "1.0000", "10.0000", "10.0000", "10.0000", payment: null));
        var beforeStatement = await StatementAsync(client, customer.Id);
        var beforeCustomer = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("10.0000", beforeCustomer!.OutstandingAmount);

        var walkInProduct = await CreateProductAsync(client, "AV-WALK", false);
        var walkIn = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), walkInProduct.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var posted = await ReadReturnAsync(await SendReturnAsync(
            client,
            walkIn.Id,
            Guid.NewGuid(),
            "Client de passage",
            [(walkIn.Lines[0].Id, "1.0000")],
            [("cash", "10.0000", null, null)]));
        var afterStatement = await StatementAsync(client, customer.Id);
        var afterCustomer = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);

        Assert.Equal(beforeStatement.OpeningBalance, afterStatement.OpeningBalance);
        Assert.Equal(beforeStatement.ClosingBalance, afterStatement.ClosingBalance);
        Assert.Equal(beforeStatement.OutstandingAmount, afterStatement.OutstandingAmount);
        Assert.Equal(
            beforeStatement.Lines.Select(line => (line.DocumentId, line.Kind, line.Amount, line.RunningBalance)).ToArray(),
            afterStatement.Lines.Select(line => (line.DocumentId, line.Kind, line.Amount, line.RunningBalance)).ToArray());
        Assert.Equal(beforeCustomer.OutstandingAmount, afterCustomer!.OutstandingAmount);
        Assert.DoesNotContain(afterStatement.Lines, line => line.DocumentId == walkIn.Id || line.DocumentId == posted.Id);

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        var storedSale = await database.Sales.AsNoTracking().SingleAsync(item => item.Id == walkIn.Id);
        Assert.Null(storedSale.CustomerId);
        var storedReturn = await database.SaleReturns.AsNoTracking().SingleAsync(item => item.Id == posted.Id);
        Assert.Equal(walkIn.Id, storedReturn.SaleId);
        var storedRefund = await database.SaleRefunds.AsNoTracking().SingleAsync(item => item.SaleReturnId == posted.Id);
        Assert.Equal(walkIn.Id, storedRefund.SaleId);
        Assert.Equal(10.0000m, storedRefund.Amount);
        Assert.Equal("cash", storedRefund.Method);
        Assert.Equal(1, await database.Customers.CountAsync());
        var linkedReturns = await database.SaleReturns.CountAsync(item =>
            database.Sales.Any(sale => sale.Id == item.SaleId && sale.CustomerId == customer.Id));
        Assert.Equal(0, linkedReturns);
    }

    [Fact]
    public async Task One_return_restores_stock_for_two_tracked_products()
    {
        using var client = await AdministratorClientAsync();
        var first = await CreateProductAsync(client, "AV-TWO-A", true);
        var second = await CreateProductAsync(client, "AV-TWO-B", true);
        await OpenAsync(client, first.Id, "5.0000");
        await OpenAsync(client, second.Id, "3.0000");
        var sale = await ReadSaleAsync(await SendPricedAsync(client, Guid.NewGuid(), new
        {
            total = "30.0000",
            lines = new object[]
            {
                new { productId = first.Id, quantity = "2.0000", unitPrice = "10.0000", lineTotal = "20.0000" },
                new { productId = second.Id, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" },
            },
            payments = new[] { new { method = "cash", amount = "30.0000" } },
        }));
        Assert.Equal(2, sale.Lines.Count);

        var posted = await ReadReturnAsync(await SendReturnAsync(
            client,
            sale.Id,
            Guid.NewGuid(),
            "Retour des deux articles",
            sale.Lines.Select(line => (line.Id, line.Quantity)).ToArray(),
            [("cash", "30.0000", null, null)]));

        Assert.Equal(2, posted.Lines.Count);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        var locationId = await database.StockLocations.Where(location => location.IsDefault).Select(location => location.Id).SingleAsync();
        Assert.Equal(5.0000m, await database.StockBalances.Where(balance => balance.ProductId == first.Id && balance.StockLocationId == locationId).Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(3.0000m, await database.StockBalances.Where(balance => balance.ProductId == second.Id && balance.StockLocationId == locationId).Select(balance => balance.Quantity).SingleAsync());
        var movements = await database.StockMovements.Where(movement => movement.SourceType == StockSourceTypes.SaleReturn).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(1, await database.SaleReturns.CountAsync());
        Assert.Equal(2, await database.SaleReturnLines.CountAsync(line => line.ReturnId == posted.Id));
        foreach (var line in posted.Lines)
        {
            var movement = Assert.Single(movements, item => item.ProductId == line.ProductId);
            Assert.Equal(line.Id, movement.SourceId);
            Assert.Equal(line.StockMovementId, movement.Id);
            Assert.Equal(StockMovementTypes.SaleReturn, movement.MovementType);
            Assert.Equal(StockSourceTypes.SaleReturn, movement.SourceType);
            Assert.Equal(locationId, movement.StockLocationId);
            Assert.Equal(decimal.Parse(line.Quantity, CultureInfo.InvariantCulture), movement.QuantityDelta);
        }

        Assert.Equal("O", await TriggerStateAsync(database, "stock_movement_matches_balance"));
        Assert.Equal("O", await TriggerStateAsync(database, "sale_return_line_matches_movement"));
        Assert.Equal("O", await TriggerStateAsync(database, "stock_movement_sale_return_source_matches_line"));
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

    private async Task<HttpClient> LoginClientAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<SaleResponse> CreateReturnableSaleAsync(HttpClient client, string sku)
    {
        var product = await CreateProductAsync(client, sku, false);
        return await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
    }

    private static async Task<CustomerStatementResponse> StatementAsync(HttpClient client, Guid customerId) =>
        (await client.GetFromJsonAsync<CustomerStatementResponse>(
            $"/api/v1/customers/{customerId}/statement?from=2026-01-01&to=2026-12-31",
            JsonOptions))!;

    private static async Task<string> TriggerStateAsync(PexilioDbContext database, string triggerName)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tgenabled::text
            FROM pg_trigger AS t
            JOIN pg_class AS c ON c.oid = t.tgrelid
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = 'pexilio' AND t.tgname = @name AND NOT t.tgisinternal
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = triggerName;
        command.Parameters.Add(parameter);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Sale_detail_reports_zero_return_history_before_any_return()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-ZERO", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "2.0000", "10.0000", "20.0000", "20.0000"));
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        var line = Assert.Single(detail!.Lines);

        Assert.Equal("0.0000", line.ReturnedQuantity);
        Assert.Equal("2.0000", line.RemainingReturnableQuantity);
        Assert.Equal("0.0000", line.ReturnedAmount);
        Assert.Equal("20.0000", line.RemainingReturnableAmount);
        Assert.Equal(sale.PaidAmount, detail.PaidAmount);
        Assert.Equal(sale.RefundedAmount, detail.RefundedAmount);
        Assert.Equal(sale.OutstandingAmount, detail.OutstandingAmount);
    }

    [Fact]
    public async Task Sale_detail_aggregates_every_posted_return_onto_its_original_line()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Historique", null);
        var water = await CreateProductAsync(client, "AV-HIST-A", false);
        var flour = await CreateProductAsync(client, "AV-HIST-B", false, "5.0000");
        var sale = await ReadSaleAsync(await SendPricedAsync(client, Guid.NewGuid(), new
        {
            customerId = customer.Id,
            total = "40.0000",
            discountAmount = "0.0000",
            discountKind = "none",
            lines = new object[]
            {
                new { productId = water.Id, quantity = "3.0000", priceKind = "retail", unitPrice = "10.0000", lineDiscount = "0.0000", discountKind = "none", lineTotal = "30.0000" },
                new { productId = flour.Id, quantity = "2.0000", priceKind = "retail", unitPrice = "5.0000", lineDiscount = "0.0000", discountKind = "none", lineTotal = "10.0000" },
            },
        }));
        var waterLine = sale.Lines.Single(line => line.ProductId == water.Id);
        var flourLine = sale.Lines.Single(line => line.ProductId == flour.Id);

        await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Premier", [(waterLine.Id, "1.0000")], null));
        var once = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        Assert.Equal(2, once!.Lines.Count);
        AssertHistory(once.Lines.Single(line => line.Id == waterLine.Id), "1.0000", "2.0000", "10.0000", "20.0000");
        AssertHistory(once.Lines.Single(line => line.Id == flourLine.Id), "0.0000", "2.0000", "0.0000", "10.0000");
        Assert.Equal("0.0000", once.RefundedAmount);
        Assert.Equal(sale.PaidAmount, once.PaidAmount);

        await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Deuxième", [(waterLine.Id, "0.5000")], null));
        var twice = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        Assert.Equal(2, twice!.Lines.Count);
        AssertHistory(twice.Lines.Single(line => line.Id == waterLine.Id), "1.5000", "1.5000", "15.0000", "15.0000");

        await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Reste", [(waterLine.Id, "1.5000")], null));
        var done = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);
        var finished = done!.Lines.Single(line => line.Id == waterLine.Id);
        AssertHistory(finished, "3.0000", "0.0000", "30.0000", "0.0000");
        Assert.True(decimal.Parse(finished.ReturnedQuantity, CultureInfo.InvariantCulture) <= decimal.Parse(finished.Quantity, CultureInfo.InvariantCulture));
        Assert.True(decimal.Parse(finished.ReturnedAmount, CultureInfo.InvariantCulture) <= decimal.Parse(finished.LineTotal, CultureInfo.InvariantCulture) - decimal.Parse(finished.AllocatedSaleDiscount, CultureInfo.InvariantCulture));
        Assert.Equal(sale.PaidAmount, done.PaidAmount);
        Assert.Equal("0.0000", done.RefundedAmount);
    }

    [Fact]
    public async Task A_sale_rejects_two_lines_for_the_same_product()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-SAME", false);
        var response = await SendPricedAsync(client, Guid.NewGuid(), new
        {
            total = "20.0000",
            payments = new[] { new { method = "cash", amount = "20.0000" } },
            lines = new object[]
            {
                new { productId = product.Id, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" },
                new { productId = product.Id, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("lines.duplicate", await FieldAsync(response));
    }

    [Fact]
    public async Task A_fully_discounted_line_reports_no_remaining_returnable_amount()
    {
        using var client = await AdministratorClientAsync();
        var customer = await CreateCustomerAsync(client, "Historique remise", null);
        var gift = await CreateProductAsync(client, "AV-HIST-GIFT", false);
        var paid = await CreateProductAsync(client, "AV-HIST-PAID", false);
        var sale = await ReadSaleAsync(await SendPricedAsync(client, Guid.NewGuid(), new
        {
            customerId = customer.Id,
            total = "10.0000",
            discountAmount = "0.0000",
            discountKind = "none",
            lines = new object[]
            {
                new { productId = gift.Id, quantity = "1.0000", priceKind = "retail", unitPrice = "10.0000", lineDiscount = "10.0000", discountKind = "percent", discountRate = "100.0000", lineTotal = "0.0000" },
                new { productId = paid.Id, quantity = "1.0000", priceKind = "retail", unitPrice = "10.0000", lineDiscount = "0.0000", discountKind = "none", lineTotal = "10.0000" },
            },
        }));
        var giftLine = sale.Lines.Single(line => line.ProductId == gift.Id);
        AssertHistory(giftLine, "0.0000", "1.0000", "0.0000", "0.0000");

        await ReadReturnAsync(await SendReturnAsync(client, sale.Id, Guid.NewGuid(), "Cadeau", [(giftLine.Id, "1.0000")], null));
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);

        Assert.Equal(2, detail!.Lines.Count);
        AssertHistory(detail.Lines.Single(line => line.Id == giftLine.Id), "1.0000", "0.0000", "0.0000", "0.0000");
        AssertHistory(detail.Lines.Single(line => line.ProductId == paid.Id), "0.0000", "1.0000", "0.0000", "10.0000");
        Assert.Equal(sale.PaidAmount, detail.PaidAmount);
        Assert.Equal(sale.OutstandingAmount, detail.OutstandingAmount);
        Assert.Equal("0.0000", detail.RefundedAmount);
    }

    [Fact]
    public async Task Sale_detail_keeps_payment_balances_after_a_refund()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "AV-HIST-REF", false);
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product.Id, null, "1.0000", "10.0000", "10.0000", "10.0000"));
        var posted = await ReadReturnAsync(await SendReturnAsync(
            client,
            sale.Id,
            Guid.NewGuid(),
            "Rendu",
            [(sale.Lines[0].Id, "1.0000")],
            [("cash", "10.0000", null, null)]));
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale.Id}", JsonOptions);

        AssertHistory(Assert.Single(detail!.Lines), "1.0000", "0.0000", "10.0000", "0.0000");
        Assert.Equal(posted.PaidAmount, detail.PaidAmount);
        Assert.Equal(posted.RefundedAmount, detail.RefundedAmount);
        Assert.Equal(posted.OutstandingAmount, detail.OutstandingAmount);
        Assert.Equal("10.0000", detail.PaidAmount);
        Assert.Equal("10.0000", detail.RefundedAmount);
        Assert.Equal("0.0000", detail.OutstandingAmount);
    }

    private static void AssertHistory(
        SaleLineResponse line,
        string returnedQuantity,
        string remainingQuantity,
        string returnedAmount,
        string remainingAmount)
    {
        Assert.Equal(returnedQuantity, line.ReturnedQuantity);
        Assert.Equal(remainingQuantity, line.RemainingReturnableQuantity);
        Assert.Equal(returnedAmount, line.ReturnedAmount);
        Assert.Equal(remainingAmount, line.RemainingReturnableAmount);
    }

    private static async Task<ProductResponse> CreateProductAsync(HttpClient client, string sku, bool trackStock, string unitPrice = "10.0000")
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku,
            name = "Eau",
            unit = "u",
            unitPrice,
            trackStock,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions))!;
    }

    private static async Task<CustomerResponse> CreateCustomerAsync(HttpClient client, string name, string? creditLimit)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customers")
        {
            Content = JsonContent.Create(new { type = "individual", name, creditLimit }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(JsonOptions))!;
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
        Guid productId,
        Guid? customerId,
        string quantity,
        string unitPrice,
        string lineTotal,
        string total,
        string? payment = "",
        bool creditOverride = false,
        string? creditOverrideReason = null)
    {
        var includePayment = payment is not null;
        if (payment == "")
        {
            payment = total;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(new
            {
                customerId,
                total,
                lines = new[]
                {
                    new { productId, quantity, unitPrice, lineTotal },
                },
                payments = includePayment ? new[] { new { method = "cash", amount = payment } } : null,
                creditOverride,
                creditOverrideReason,
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> SendPaymentAsync(HttpClient client, Guid saleId, string amount)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sales/{saleId}/payments")
        {
            Content = JsonContent.Create(new { method = "cash", amount }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> SendPricedAsync(HttpClient client, Guid key, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> SendReturnAsync(
        HttpClient client,
        Guid saleId,
        Guid key,
        string reason,
        (Guid SaleLineId, string Quantity)[] lines,
        (string Method, string Amount, string? Reference, string? Notes)[]? refunds,
        string? clientAmount = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sales/{saleId}/returns")
        {
            Content = JsonContent.Create(new
            {
                reason,
                lines = lines.Select(line => new { saleLineId = line.SaleLineId, quantity = line.Quantity, amount = clientAmount }),
                refunds = refunds?.Select(refund => new
                {
                    method = refund.Method,
                    amount = refund.Amount,
                    reference = refund.Reference,
                    notes = refund.Notes,
                }),
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static async Task<SaleResponse> ReadSaleAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions))!;
    }

    private static async Task<SaleReturnPostResponse> ReadReturnAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaleReturnPostResponse>(JsonOptions))!;
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

    private static async Task<long> ReturnCounterAsync(PexilioDbContext database) =>
        await database.DocumentCounters
            .Where(counter => counter.Series == SaleNumbers.ReturnSeries)
            .Select(counter => (long?)counter.LastValue)
            .SingleOrDefaultAsync() ?? 0L;

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

    private sealed class SaleLockGate : ISaleProductLock
    {
        private Func<CancellationToken, ValueTask>? _hold;

        public void Arm(Func<CancellationToken, ValueTask> hold) => _hold = hold;

        public void Disarm() => _hold = null;

        public ValueTask AfterProductLockAsync(CancellationToken cancellationToken) =>
            _hold is null ? ValueTask.CompletedTask : _hold(cancellationToken);
    }

    private sealed record ProblemBody(string? Code, Dictionary<string, string[]>? Errors);
}
