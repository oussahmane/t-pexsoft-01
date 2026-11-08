using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pexilio.Application;
using Pexilio.Domain.Sales;
using Pexilio.Infrastructure.Auth;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class CustomerCreditApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ManualTimeProvider _clock = new();
    private readonly SaleLockGate _lockGate = new();
    private HealthApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _clock.MoveTo(DateTimeOffset.UtcNow);
        _factory = new HealthApiFactory(LocalEnvironment.TestConnectionString(), services =>
        {
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(TimeProvider)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<TimeProvider>(_clock);
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(AuthSettings)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(new AuthSettings
            {
                SigningKey = TestDatabase.SigningKey,
                AccessTokenLifetime = TimeSpan.FromDays(1),
            });
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
    public async Task A_customer_without_a_limit_can_buy_on_credit()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-NONE", false);
        var customer = await CreateCustomerAsync(client, "Nora", null);

        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));

        Assert.False(sale.CreditLimitOverridden);
        Assert.Null(sale.CreditOverrideReason);
        Assert.Null(sale.CreditLimitSnapshot);
        Assert.Equal("10.0000", sale.OutstandingAmount);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var stored = await database.Sales.SingleAsync();
        Assert.False(stored.CreditLimitOverridden);
        Assert.Null(stored.CreditOverrideReason);
        Assert.Null(stored.CreditOverrideByUserId);
        Assert.Null(stored.CreditOverrideAtUtc);
        Assert.Null(stored.CreditLimitSnapshot);
        Assert.Null(stored.CreditOutstandingBefore);
        Assert.Null(stored.CreditProjectedOutstanding);
    }

    [Fact]
    public async Task A_zero_limit_rejects_new_debt_and_allows_a_fully_paid_sale()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-ZERO", false);
        var customer = await CreateCustomerAsync(client, "Zero", "0.0000");

        var rejected = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null);
        var paid = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", "10.0000", false, null));

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("credit.limit_exceeded", await CodeAsync(rejected));
        Assert.False(paid.CreditLimitOverridden);
        Assert.Equal("0.0000", paid.OutstandingAmount);
    }

    [Fact]
    public async Task A_sale_exactly_at_the_limit_is_stored_without_an_override()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-EXACT", false);
        var customer = await CreateCustomerAsync(client, "Exact", "10.0000");

        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));

        Assert.False(sale.CreditLimitOverridden);
        Assert.Equal("10.0000", sale.OutstandingAmount);
        var listed = await client.GetFromJsonAsync<List<SaleSummaryResponse>>("/api/v1/sales", JsonOptions);
        Assert.False(Assert.Single(listed!).CreditLimitOverridden);
    }

    [Fact]
    public async Task One_ten_thousandth_over_the_limit_is_rejected()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-CENT", false, "10.0001");
        var customer = await CreateCustomerAsync(client, "Cent", "10.0000");

        var rejected = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0001", null, false, null, "10.0001");

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        Assert.Equal("credit.limit_exceeded", body?["code"]?.GetValue<string>());
        Assert.Equal("10.0000", body?["creditLimit"]?.GetValue<string>());
        Assert.Equal("0.0000", body?["outstanding"]?.GetValue<string>());
        Assert.Equal("10.0001", body?["projected"]?.GetValue<string>());
        Assert.Equal("10.0001", body?["saleTotal"]?.GetValue<string>());
        Assert.Equal("0.0000", body?["initialPayment"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_cashier_cannot_exceed_the_limit_or_send_an_override()
    {
        using var admin = await AdministratorClientAsync();
        var product = await CreateProductAsync(admin, "CR-CASH", false);
        var customer = await CreateCustomerAsync(admin, "Cashier", "0.0000");
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier.credit@example.com", password = "cashier-password-1" });
        using var cashier = await LoginAsync("cashier.credit@example.com", "cashier-password-1");

        var exceeded = await SendSaleAsync(cashier, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null);
        var flagged = await SendSaleAsync(cashier, Guid.NewGuid(), product, customer.Id, "10.0000", "10.0000", true, "Let it through");
        var explained = await SendSaleAsync(cashier, Guid.NewGuid(), product, customer.Id, "10.0000", "10.0000", false, "Please");

        Assert.Equal("credit.limit_exceeded", await CodeAsync(exceeded));
        Assert.Equal(HttpStatusCode.Forbidden, flagged.StatusCode);
        var flaggedBody = await flagged.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        Assert.Equal("auth.forbidden", flaggedBody?.Code);
        Assert.Equal("creditOverride.forbidden", flaggedBody?.Errors?.Values.FirstOrDefault()?.FirstOrDefault());
        Assert.Equal(HttpStatusCode.Forbidden, explained.StatusCode);
        Assert.Equal("creditOverride.forbidden", await FieldAsync(explained));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.Sales.CountAsync());
    }

    [Fact]
    public async Task An_administrator_override_requires_a_reason_and_records_the_snapshot()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-OVER", false);
        var customer = await CreateCustomerAsync(client, "Over", "0.0000");
        var key = Guid.NewGuid();

        var missing = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, true, " ");
        var tooLong = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, true, new string('a', 201));
        var created = await ReadSaleAsync(await SendSaleAsync(client, key, product, customer.Id, "10.0000", "4.0000", true, " Client historique "));
        var replay = await ReadSaleAsync(await SendSaleAsync(client, key, product, customer.Id, "10.0000", "4.0000", true, " Client historique "));
        var conflict = await SendSaleAsync(client, key, product, customer.Id, "10.0000", "4.0000", true, "A different reason");

        Assert.Equal("creditOverrideReason.required", await FieldAsync(missing));
        Assert.Equal("creditOverrideReason.length", await FieldAsync(tooLong));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("sale.idempotency_conflict", await CodeAsync(conflict));
        Assert.Equal(created.Id, replay.Id);
        Assert.True(created.CreditLimitOverridden);
        Assert.Equal("Client historique", created.CreditOverrideReason);
        Assert.Equal("0.0000", created.CreditLimitSnapshot);
        Assert.Equal("0.0000", created.CreditOutstandingBefore);
        Assert.Equal("6.0000", created.CreditProjectedOutstanding);
        Assert.Equal("6.0000", created.OutstandingAmount);
        Assert.Equal(created.CreditOverrideReason, replay.CreditOverrideReason);
        var detail = await client.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{created.Id}", JsonOptions);
        Assert.Equal("Client historique", detail!.CreditOverrideReason);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var stored = await database.Sales.SingleAsync();
        Assert.Equal(stored.CreatedByUserId, stored.CreditOverrideByUserId);
        Assert.Equal(stored.CreatedAtUtc, stored.CreditOverrideAtUtc);
        Assert.Equal(detail.CreatedAt, stored.CreatedAtUtc);
        Assert.Equal(0m, stored.CreditLimitSnapshot);
        Assert.Equal(0m, stored.CreditOutstandingBefore);
        Assert.Equal(6.0000m, stored.CreditProjectedOutstanding);
        Assert.Equal(0.0000m, (await database.Customers.SingleAsync()).CreditLimit);
        Assert.Equal(1, await database.SaleIdempotencies.CountAsync());
    }

    [Fact]
    public async Task An_override_is_rejected_when_the_sale_fits_or_has_no_customer()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-FIT", false);
        var customer = await CreateCustomerAsync(client, "Fit", "10.0000");

        var within = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, true, "Not needed");
        var walkIn = await SendSaleAsync(client, Guid.NewGuid(), product, null, "10.0000", "10.0000", true, "Walk in");
        var noted = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, "Unused note"));

        Assert.Equal("creditOverride.not_applicable", await FieldAsync(within));
        Assert.Equal("creditOverride.customer", await FieldAsync(walkIn));
        Assert.False(noted.CreditLimitOverridden);
        Assert.Null(noted.CreditOverrideReason);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Sales.CountAsync());
        Assert.False(await database.Sales.Select(sale => sale.CreditLimitOverridden).SingleAsync());
    }

    [Fact]
    public async Task A_reason_without_the_flag_does_not_authorize_an_over_limit_sale()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-FLAG", false);
        var customer = await CreateCustomerAsync(client, "Flag", "0.0000");

        var rejected = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, "Please allow this");

        Assert.Equal("credit.limit_exceeded", await CodeAsync(rejected));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.Sales.CountAsync());
    }

    [Fact]
    public async Task A_fully_paid_sale_is_allowed_when_debt_is_already_above_the_limit()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-FULL", false);
        var customer = await CreateCustomerAsync(client, "Full", "0.0000");
        await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, true, "Opening debt"));

        var paid = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", "10.0000", false, null));

        Assert.False(paid.CreditLimitOverridden);
        Assert.Equal("0.0000", paid.OutstandingAmount);
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("0.0000", account!.CreditLimit);
        Assert.Equal("10.0000", account.OutstandingAmount);
    }

    [Fact]
    public async Task A_payment_frees_credit_for_the_next_sale()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-PAY", false);
        var customer = await CreateCustomerAsync(client, "Pay", "10.0000");
        var first = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));
        var blocked = await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null);
        var payment = await PayAsync(client, first.Id, "6.0000");
        var second = await ReadSaleAsync(await SendSaleAsync(
            client,
            Guid.NewGuid(),
            product,
            customer.Id,
            "4.0000",
            null,
            false,
            null,
            quantity: "0.4000",
            lineTotal: "4.0000"));

        Assert.Equal("credit.limit_exceeded", await CodeAsync(blocked));
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        Assert.Equal("4.0000", second.OutstandingAmount);
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("8.0000", account!.OutstandingAmount);
    }

    [Fact]
    public async Task A_return_frees_credit_for_the_next_sale_and_the_statement_shows_only_the_sale_override()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-RET", false);
        var customer = await CreateCustomerAsync(client, "Return", "6.0000");
        var sale = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, true, "Authorized debt"));
        await ReturnAsync(sale.Id, 1.0000m, 5.0000m, "R-CREDIT-1");
        var payment = await PayAsync(client, sale.Id, "2.0000");
        var next = await ReadSaleAsync(await SendSaleAsync(
            client,
            Guid.NewGuid(),
            product,
            customer.Id,
            "2.0000",
            null,
            false,
            null,
            quantity: "0.2000",
            lineTotal: "2.0000"));

        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        Assert.False(next.CreditLimitOverridden);
        var day = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var statementResponse = await client.GetAsync(
            $"/api/v1/customers/{customer.Id}/statement?from={day.AddDays(-1):yyyy-MM-dd}&to={day.AddDays(2):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, statementResponse.StatusCode);
        var statement = JsonNode.Parse(await statementResponse.Content.ReadAsStringAsync())!.AsObject();
        var lines = statement["lines"]!.AsArray();
        var saleLine = lines.Single(line => line!["kind"]!.GetValue<string>() == "sale" && line["documentId"]!.GetValue<Guid>() == sale.Id);
        var paymentLine = lines.Single(line => line!["kind"]!.GetValue<string>() == "payment");
        var returnLine = lines.Single(line => line!["kind"]!.GetValue<string>() == "return");
        Assert.True(saleLine!["creditLimitOverridden"]!.GetValue<bool>());
        Assert.Equal("Authorized debt", saleLine["creditOverrideReason"]!.GetValue<string>());
        Assert.Equal("6.0000", saleLine["creditLimitSnapshot"]!.GetValue<string>());
        Assert.Equal("0.0000", saleLine["creditOutstandingBefore"]!.GetValue<string>());
        Assert.Equal("10.0000", saleLine["creditProjectedOutstanding"]!.GetValue<string>());
        Assert.Null(paymentLine!["creditLimitOverridden"]);
        Assert.Null(returnLine!["creditLimitOverridden"]);
        Assert.DoesNotContain("creditOverrideReason", paymentLine.AsObject());
        Assert.DoesNotContain("creditOverrideReason", returnLine.AsObject());
    }

    [Fact]
    public async Task Two_sales_cannot_both_pass_against_the_same_outstanding_balance()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-RACE", false);
        var customer = await CreateCustomerAsync(client, "Race", "15.0000");

        var responses = await Task.WhenAll(
            SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null),
            SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));
        var statuses = responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray();

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], statuses);
        Assert.Equal("credit.limit_exceeded", await CodeAsync(responses.Single(response => response.StatusCode == HttpStatusCode.Conflict)));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Sales.CountAsync());
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("10.0000", account!.OutstandingAmount);
    }

    [Fact]
    public async Task A_concurrent_payment_and_sale_cannot_leave_the_customer_over_the_limit()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-BOTH", false);
        var customer = await CreateCustomerAsync(client, "Both", "10.0000");
        var existing = await ReadSaleAsync(await SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));

        var responses = await Task.WhenAll(
            PayAsync(client, existing.Id, "10.0000"),
            SendSaleAsync(client, Guid.NewGuid(), product, customer.Id, "10.0000", null, false, null));

        Assert.Contains(responses, response => response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/payments", StringComparison.Ordinal) == true
            && response.StatusCode == HttpStatusCode.Created);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(10.0000m, (await database.Customers.SingleAsync()).CreditLimit);
        Assert.DoesNotContain(await database.Sales.Select(sale => sale.CreditLimitOverridden).ToListAsync(), overridden => overridden);
        var account = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        Assert.Equal("10.0000", account!.CreditLimit);
        var outstanding = decimal.Parse(account.OutstandingAmount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(outstanding is 0m or 10.0000m);
        var saleCount = await database.Sales.CountAsync();
        Assert.Equal(outstanding == 0m ? 1 : 2, saleCount);
    }

    [Fact]
    public async Task Different_customers_can_sell_the_same_product_concurrently()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-TWO", true);
        await OpenAsync(client, product.Id, "2.0000");
        var first = await CreateCustomerAsync(client, "First", "100.0000");
        var second = await CreateCustomerAsync(client, "Second", "100.0000");
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
            left = SendSaleAsync(client, Guid.NewGuid(), product, first.Id, "10.0000", null, false, null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            right = SendSaleAsync(client, Guid.NewGuid(), product, second.Id, "10.0000", null, false, null);
            await Task.Delay(200);
            Assert.False(right.IsCompleted);
            release.TrySetResult();
            var responses = await Task.WhenAll(left, right);
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
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
        Assert.Equal(2, await database.Sales.CountAsync());
        Assert.Equal(0m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task A_stock_failure_after_the_credit_check_rolls_the_sale_back()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "CR-STOCK", true);
        var customer = await CreateCustomerAsync(client, "Stock", "100.0000");
        var key = Guid.NewGuid();

        var rejected = await SendSaleAsync(client, key, product, customer.Id, "10.0000", null, false, null);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
            Assert.Equal(0, await database.Sales.CountAsync());
            Assert.Equal(0, await database.SaleIdempotencies.CountAsync());
            Assert.Equal(100.0000m, (await database.Customers.SingleAsync()).CreditLimit);
        }

        await OpenAsync(client, product.Id, "1.0000");
        var sold = await ReadSaleAsync(await SendSaleAsync(client, key, product, customer.Id, "10.0000", null, false, null));

        Assert.Equal("stock.insufficient", await CodeAsync(rejected));
        Assert.False(sold.CreditLimitOverridden);
        await using var check = _factory.Services.CreateAsyncScope();
        var persisted = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await persisted.Sales.CountAsync());
        Assert.Equal(1, await persisted.SaleIdempotencies.CountAsync());
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

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<ProductResponse> CreateProductAsync(
        HttpClient client,
        string sku,
        bool trackStock,
        string unitPrice = "10.0000")
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
        ProductResponse product,
        Guid? customerId,
        string total,
        string? payment,
        bool creditOverride,
        string? creditOverrideReason,
        string? unitPrice = null,
        string quantity = "1.0000",
        string? lineTotal = null)
    {
        var price = unitPrice ?? product.UnitPrice;
        var extended = lineTotal ?? total;
        object body = customerId is null
            ? new
            {
                total,
                lines = new[] { new { productId = product.Id, quantity, unitPrice = price, lineTotal = extended } },
                payments = new[] { new { method = "cash", amount = payment } },
                creditOverride,
                creditOverrideReason,
            }
            : payment is null
                ? new
                {
                    customerId,
                    total,
                    lines = new[] { new { productId = product.Id, quantity, unitPrice = price, lineTotal = extended } },
                    payments = Array.Empty<object>(),
                    creditOverride,
                    creditOverrideReason,
                }
                : new
                {
                    customerId,
                    total,
                    lines = new[] { new { productId = product.Id, quantity, unitPrice = price, lineTotal = extended } },
                    payments = new[] { new { method = "cash", amount = payment } },
                    creditOverride,
                    creditOverrideReason,
                };
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid saleId, string amount)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sales/{saleId}/payments")
        {
            Content = JsonContent.Create(new { method = "cash", amount }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return client.SendAsync(message);
    }

    private async Task ReturnAsync(Guid saleId, decimal quantity, decimal amount, string number)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var line = await database.SaleLines.AsNoTracking().SingleAsync(item => item.SaleId == saleId);
        var userId = await database.Set<AppUser>().Select(user => user.Id).SingleAsync();
        var returnId = Guid.NewGuid();
        database.SaleReturns.Add(new SaleReturn
        {
            Id = returnId,
            CompanyId = line.CompanyId,
            SaleId = line.SaleId,
            Number = number,
            Reason = "Retour",
            ReturnedAtUtc = _clock.GetUtcNow(),
            ReturnedByUserId = userId,
            Lines =
            [
                new SaleReturnLine
                {
                    Id = Guid.NewGuid(),
                    ReturnId = returnId,
                    SaleId = line.SaleId,
                    SaleLineId = line.Id,
                    CompanyId = line.CompanyId,
                    ProductId = line.ProductId,
                    Quantity = quantity,
                    Amount = amount,
                    TracksStock = line.TracksStock,
                },
            ],
        });
        await database.SaveChangesAsync();
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
        catch (Exception exception) when (exception is not OperationCanceledException)
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
