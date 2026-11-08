using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Pexilio.Application;
using Pexilio.Domain.Sales;
using Pexilio.Infrastructure.Auth;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class CustomerStatementApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset AlgiersNovemberStart = new(2026, 10, 31, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AlgiersNovemberEnd = new(2026, 11, 1, 23, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new();
    private HealthApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
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

            // The statement window spans more than one access-token lifetime. This test host
            // issues a long-lived token so moving the clock does not require a database privilege.
            services.AddSingleton(new AuthSettings
            {
                SigningKey = TestDatabase.SigningKey,
                AccessTokenLifetime = TimeSpan.FromDays(3650),
            });
        });
        await TestDatabase.PrepareAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task List_and_detail_return_the_derived_outstanding_amount()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client);
        var debtor = await CreateCustomerAsync(client, "Amin", "0551000001");
        var clear = await CreateCustomerAsync(client, "Lina", "0551000002");
        var sale = await SellAsync(client, product.Id, debtor.Id, null);
        await PayAsync(client, sale.Id, "4.0000");
        await ReturnAsync(sale.Id, 1.0000m, 1.5000m, "AV-000001", new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));

        var listed = await client.GetFromJsonAsync<List<CustomerResponse>>("/api/v1/customers", JsonOptions);
        var customers = listed ?? throw new InvalidOperationException("The customer list response was missing.");
        var detail = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{debtor.Id}", JsonOptions);
        var other = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{clear.Id}", JsonOptions);

        Assert.Equal("4.5000", customers.Single(customer => customer.Id == debtor.Id).OutstandingAmount);
        Assert.Equal("0.0000", customers.Single(customer => customer.Id == clear.Id).OutstandingAmount);
        Assert.Equal("4.5000", detail!.OutstandingAmount);
        Assert.Equal("Amin", detail.Name);
        Assert.Equal("0.0000", other!.OutstandingAmount);
    }

    [Fact]
    public async Task Multiple_payments_and_return_lines_do_not_multiply_the_balance()
    {
        using var client = await ClientAtAsync(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Nadia", "0551000010");
        var sale = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero));
        var first = await PayAsync(client, sale.Id, "1.0000");
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var second = await PayAsync(client, sale.Id, "2.0000");
        var earlyReturn = await ReturnAsync(sale.Id, 0.4000m, 0.2500m, "AV-000011", new DateTimeOffset(2026, 11, 1, 13, 0, 0, TimeSpan.Zero));
        var laterReturn = await ReturnAsync(sale.Id, 0.4000m, 0.7500m, "AV-000012", new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero));

        var detail = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");

        Assert.Equal("6.0000", detail!.OutstandingAmount);
        Assert.Equal("6.0000", statement.OutstandingAmount);
        Assert.Equal("6.0000", statement.ClosingBalance);
        Assert.Equal(
            [
                (sale.Id, "sale", "10.0000", "10.0000"),
                (first.Payment.Id, "payment", "-1.0000", "9.0000"),
                (second.Payment.Id, "payment", "-2.0000", "7.0000"),
                (earlyReturn, "return", "-0.2500", "6.7500"),
                (laterReturn, "return", "-0.7500", "6.0000"),
            ],
            statement.Lines.Select(line => (line.DocumentId, line.Kind, line.Amount, line.RunningBalance)).ToArray());
    }

    [Fact]
    public async Task Statement_projects_opening_running_closing_and_current_outstanding()
    {
        using var client = await ClientAtAsync(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero));
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Sara", "0551000020");
        var openingSale = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var periodSale = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var periodPayment = await PayAsync(client, periodSale.Id, "4.0000");
        var periodReturn = await ReturnAsync(periodSale.Id, 1.0000m, 1.5000m, "AV-000021", new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero));
        var laterPayment = await PayAsync(client, openingSale.Id, "2.0000");

        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");

        Assert.Equal("Africa/Algiers", statement.TimeZoneId);
        Assert.Equal("Sara", statement.CustomerName);
        Assert.Equal("10.0000", statement.OpeningBalance);
        Assert.Equal("14.5000", statement.ClosingBalance);
        Assert.Equal("12.5000", statement.OutstandingAmount);
        Assert.Equal(
            [
                (periodSale.Id, "sale", "10.0000", "20.0000"),
                (periodPayment.Payment.Id, "payment", "-4.0000", "16.0000"),
                (periodReturn, "return", "-1.5000", "14.5000"),
            ],
            statement.Lines.Select(line => (line.DocumentId, line.Kind, line.Amount, line.RunningBalance)).ToArray());
        Assert.DoesNotContain(statement.Lines, line => line.DocumentId == laterPayment.Payment.Id);
    }

    [Fact]
    public async Task Statement_bounds_are_inclusive_at_from_and_exclusive_at_to()
    {
        using var client = await ClientAtAsync(AlgiersNovemberStart);
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Karim", "0551000030");
        var included = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, AlgiersNovemberEnd);
        var excluded = await SellAsync(client, product.Id, customer.Id, null);

        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");

        Assert.Equal("0.0000", statement.OpeningBalance);
        Assert.Equal("10.0000", statement.ClosingBalance);
        Assert.Equal("20.0000", statement.OutstandingAmount);
        var line = Assert.Single(statement.Lines);
        Assert.Equal(included.Id, line.DocumentId);
        Assert.Equal("10.0000", line.RunningBalance);
        Assert.Equal(AlgiersNovemberStart, line.OccurredAt);
        Assert.Equal(TimeSpan.Zero, line.OccurredAt.Offset);
    }

    [Fact]
    public async Task Africa_Algiers_places_a_late_utc_instant_on_the_next_civil_day()
    {
        var previousInstant = new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero);
        var nextInstant = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        using var client = await ClientAtAsync(previousInstant);
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Yasmine", "0551000040");
        var previousDay = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, nextInstant);
        var nextCivilDay = await SellAsync(client, product.Id, customer.Id, null);

        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");

        Assert.Equal("Africa/Algiers", statement.TimeZoneId);
        Assert.Equal("2026-11-01", statement.From);
        Assert.Equal("2026-11-02", statement.To);
        Assert.Equal("10.0000", statement.OpeningBalance);
        Assert.Equal("20.0000", statement.ClosingBalance);
        Assert.Equal("20.0000", statement.OutstandingAmount);
        var line = Assert.Single(statement.Lines);
        Assert.Equal(nextCivilDay.Id, line.DocumentId);
        Assert.Equal(nextInstant, line.OccurredAt);
        Assert.Equal(TimeSpan.Zero, line.OccurredAt.Offset);
        Assert.DoesNotContain(statement.Lines, item => item.DocumentId == previousDay.Id);
    }

    [Fact]
    public async Task An_empty_history_and_an_equal_date_period_have_no_lines()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client);
        var empty = await CreateCustomerAsync(client, "Vide", "0551000050");
        var active = await CreateCustomerAsync(client, "Actif", "0551000051");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
        var localNow = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone);
        var monthStart = new DateOnly(localNow.Year, localNow.Month, 1);
        var untouched = await StatementAsync(client, empty.Id, null, null);
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await SellAsync(client, product.Id, active.Id, null);
        var equalDates = await StatementAsync(client, active.Id, "2026-11-01", "2026-11-01");

        Assert.Equal("Africa/Algiers", untouched.TimeZoneId);
        Assert.Equal(monthStart.ToString("yyyy-MM-dd"), untouched.From);
        Assert.Equal(monthStart.AddMonths(1).ToString("yyyy-MM-dd"), untouched.To);
        Assert.Equal("0.0000", untouched.OpeningBalance);
        Assert.Equal("0.0000", untouched.ClosingBalance);
        Assert.Equal("0.0000", untouched.OutstandingAmount);
        Assert.Empty(untouched.Lines);
        Assert.Equal("2026-11-01", equalDates.From);
        Assert.Equal("2026-11-01", equalDates.To);
        Assert.Equal("0.0000", equalDates.OpeningBalance);
        Assert.Equal("0.0000", equalDates.ClosingBalance);
        Assert.Equal("10.0000", equalDates.OutstandingAmount);
        Assert.Empty(equalDates.Lines);
    }

    [Fact]
    public async Task A_return_contributes_its_persisted_line_amount()
    {
        using var client = await ClientAtAsync(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Rami", "0551000060");
        var sale = await SellAsync(client, product.Id, customer.Id, null);
        var returnId = await ReturnAsync(sale.Id, 1.0000m, 3.2500m, "AV-000061", new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero));

        var detail = await client.GetFromJsonAsync<CustomerResponse>($"/api/v1/customers/{customer.Id}", JsonOptions);
        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");
        var returned = statement.Lines.Single(line => line.Kind == "return");

        Assert.Equal("6.7500", detail!.OutstandingAmount);
        Assert.Equal("6.7500", statement.OutstandingAmount);
        Assert.Equal(returnId, returned.DocumentId);
        Assert.Equal("-3.2500", returned.Amount);
        Assert.Equal("6.7500", returned.RunningBalance);
    }

    [Fact]
    public async Task A_walk_in_sale_is_excluded_from_the_customer_balance()
    {
        using var client = await ClientAtAsync(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Nora", "0551000070");
        var credit = await SellAsync(client, product.Id, customer.Id, null);
        await UseInstantAsync(client, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero));
        var walkIn = await SellAsync(client, product.Id, null, "10.0000");

        var listed = await client.GetFromJsonAsync<List<CustomerResponse>>("/api/v1/customers", JsonOptions);
        var customers = listed ?? throw new InvalidOperationException("The customer list response was missing.");
        var statement = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");

        Assert.Equal("10.0000", customers.Single(item => item.Id == customer.Id).OutstandingAmount);
        var line = Assert.Single(statement.Lines);
        Assert.Equal(credit.Id, line.DocumentId);
        Assert.Equal("sale", line.Kind);
        Assert.Equal("10.0000", statement.OutstandingAmount);
        Assert.DoesNotContain(statement.Lines, item => item.DocumentId == walkIn.Id);
    }

    [Fact]
    public async Task An_uncommitted_payment_stays_out_until_that_transaction_commits()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client);
        var customer = await CreateCustomerAsync(client, "Hamid", "0551000080");
        var saleAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var paymentAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await UseInstantAsync(client, saleAt);
        var sale = await SellAsync(client, product.Id, customer.Id, null);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var saleRow = await database.Sales.AsNoTracking().SingleAsync(item => item.Id == sale.Id);
        var userId = await database.Set<AppUser>().Select(user => user.Id).SingleAsync();
        var paymentId = Guid.NewGuid();

        await using var connection = await OpenTestAsync();
        await using var pending = await connection.BeginTransactionAsync();
        await InsertPaymentAsync(connection, pending, paymentId, saleRow.Id, saleRow.CompanyId, userId, 3.0000m, paymentAt);
        Assert.Equal(1L, await CountPaymentsAsync(connection, pending, paymentId));

        var during = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");
        Assert.Equal("10.0000", during.OutstandingAmount);
        Assert.Equal(sale.Id, Assert.Single(during.Lines).DocumentId);

        await pending.CommitAsync();

        var after = await StatementAsync(client, customer.Id, "2026-11-01", "2026-11-02");
        Assert.Equal("7.0000", after.OutstandingAmount);
        Assert.Equal(
            [
                (sale.Id, "sale", "10.0000", "10.0000"),
                (paymentId, "payment", "-3.0000", "7.0000"),
            ],
            after.Lines.Select(line => (line.DocumentId, line.Kind, line.Amount, line.RunningBalance)).ToArray());
        Assert.Equal(paymentAt, after.Lines[1].OccurredAt);
    }

    [Fact]
    public async Task Customer_create_replay_returns_the_live_balance_without_rewriting_the_stored_response()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client);
        var key = Guid.NewGuid();
        var body = new { type = "individual", name = "Sara", phone = "0551000090" };
        var created = await ReadCustomerAsync(await SendCustomerAsync(client, key, body));
        var sale = await SellAsync(client, product.Id, created.Id, null);

        var replay = await ReadCustomerAsync(await SendCustomerAsync(client, key, body));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var claim = await database.CustomerIdempotencies.SingleAsync();

        Assert.Equal(created.Id, replay.Id);
        Assert.Equal(sale.CustomerId, replay.Id);
        Assert.Equal("10.0000", replay.OutstandingAmount);
        Assert.Equal("Sara", replay.Name);
        Assert.Equal("0551000090", replay.Phone);
        var stored = JsonNode.Parse(claim.ResponseJson)!.AsObject();
        Assert.Equal("0.0000", stored["outstandingAmount"]!.GetValue<string>());

        stored.Remove("outstandingAmount");
        claim.ResponseJson = stored.ToJsonString(JsonOptions);
        await database.SaveChangesAsync();

        var again = await ReadCustomerAsync(await SendCustomerAsync(client, key, body));
        database.ChangeTracker.Clear();
        var preserved = await database.CustomerIdempotencies.AsNoTracking().SingleAsync();

        Assert.Equal("10.0000", again.OutstandingAmount);
        Assert.DoesNotContain("outstandingAmount", preserved.ResponseJson, StringComparison.Ordinal);
    }

    private async Task<HttpClient> ClientAtAsync(DateTimeOffset instant)
    {
        _clock.MoveTo(instant);
        return await AdministratorClientAsync();
    }

    private async Task UseInstantAsync(HttpClient client, DateTimeOffset instant)
    {
        _clock.MoveTo(instant);
        await AuthenticateAsync(client);
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
        if (setup.StatusCode != HttpStatusCode.Conflict)
        {
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        }

        await AuthenticateAsync(client);
        return client;
    }

    private async Task AuthenticateAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@example.com", password = "correct-horse-battery" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
    }

    private static async Task<ProductResponse> CreateProductAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku = "EAU-STMT",
            name = "Eau",
            unit = "u",
            unitPrice = "10.0000",
            trackStock = false,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions))!;
    }

    private static async Task<CustomerResponse> CreateCustomerAsync(HttpClient client, string name, string phone)
    {
        return await ReadCustomerAsync(await SendCustomerAsync(client, Guid.NewGuid(), new { type = "individual", name, phone }));
    }

    private static async Task<HttpResponseMessage> SendCustomerAsync(HttpClient client, Guid key, object body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customers")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(message);
    }

    private static async Task<CustomerResponse> ReadCustomerAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>(JsonOptions))!;
    }

    private static async Task<SaleResponse> SellAsync(HttpClient client, Guid productId, Guid? customerId, string? payment)
    {
        var line = new { productId, quantity = "1.0000", unitPrice = "10.0000", lineTotal = "10.0000" };
        object body = customerId is null
            ? new { total = "10.0000", lines = new[] { line }, payments = new[] { new { method = "cash", amount = payment } } }
            : payment is null
                ? new { customerId, total = "10.0000", lines = new[] { line }, payments = Array.Empty<object>() }
                : new { customerId, total = "10.0000", lines = new[] { line }, payments = new[] { new { method = "cash", amount = payment } } };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions))!;
    }

    private static async Task<SalePaymentPostResponse> PayAsync(HttpClient client, Guid saleId, string amount)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sales/{saleId}/payments")
        {
            Content = JsonContent.Create(new { method = "cash", amount }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SalePaymentPostResponse>(JsonOptions))!;
    }

    private async Task<Guid> ReturnAsync(Guid saleId, decimal quantity, decimal amount, string number, DateTimeOffset returnedAt)
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
            ReturnedAtUtc = returnedAt,
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
        return returnId;
    }

    private static async Task<CustomerStatementResponse> StatementAsync(HttpClient client, Guid customerId, string? from, string? to)
    {
        var suffix = from is null ? "" : $"?from={from}&to={to}";
        return (await client.GetFromJsonAsync<CustomerStatementResponse>($"/api/v1/customers/{customerId}/statement{suffix}", JsonOptions))!;
    }

    private static async Task InsertPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        Guid saleId,
        Guid companyId,
        Guid userId,
        decimal amount,
        DateTimeOffset paidAt)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pexilio.sale_payment
                ("Id", "SaleId", "CompanyId", "Method", "Amount", "PaidAtUtc", "ReceivedByUserId")
            VALUES (@id, @sale, @company, 'cash', @amount, @paid, @user)
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = paymentId });
        command.Parameters.Add(new NpgsqlParameter("sale", NpgsqlDbType.Uuid) { Value = saleId });
        command.Parameters.Add(new NpgsqlParameter("company", NpgsqlDbType.Uuid) { Value = companyId });
        command.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = amount });
        command.Parameters.Add(new NpgsqlParameter("paid", NpgsqlDbType.TimestampTz) { Value = paidAt });
        command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId });
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<long> CountPaymentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid paymentId)
    {
        await using var command = new NpgsqlCommand(
            """SELECT COUNT(*) FROM pexilio.sale_payment WHERE "Id" = @id""",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = paymentId });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<NpgsqlConnection> OpenTestAsync()
    {
        var connection = new NpgsqlConnection(LocalEnvironment.TestConnectionString());
        try
        {
            await connection.OpenAsync();
            await TestDatabase.EnsureConnectedDisposableAsync(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
