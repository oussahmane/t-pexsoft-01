using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pexilio.Application;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class StockApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HealthApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new HealthApiFactory(LocalEnvironment.TestConnectionString());
        await TestDatabase.PrepareAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Ledger_matches_the_balance_and_survives_a_new_api_process()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-2", true);

        var before = await client.GetFromJsonAsync<List<StockBalanceResponse>>("/api/v1/stock/balances", JsonOptions);
        Assert.NotNull(before);
        Assert.Equal("0.0000", before.Single().Quantity);
        Assert.False(before.Single().BelowMinimum);
        Assert.False(before.Single().AtMinimum);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
            Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
            Assert.Equal(0, await database.StockBalances.CountAsync());
            Assert.Equal(0, await database.StockMovements.CountAsync());
        }

        var locations = await client.GetFromJsonAsync<List<StockLocationResponse>>("/api/v1/stock/locations", JsonOptions);
        Assert.NotNull(locations);
        Assert.Equal("MAIN", locations.Single().Code);
        Assert.Equal("Principal", locations.Single().Name);
        Assert.True(locations.Single().IsDefault);
        Assert.True(locations.Single().PreventNegativeStock);

        var opening = await PostAsync(client, "/api/v1/stock/opening", product.Id, "10.0000", "Inventaire initial");
        var receipt = await PostAsync(client, "/api/v1/stock/receipts", product.Id, "2.5000", "Livraison");
        var adjustment = await PostAsync(client, "/api/v1/stock/adjustments", product.Id, "-1.2500", "Casse");
        Assert.Equal(HttpStatusCode.Created, opening.StatusCode);
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        Assert.Equal(HttpStatusCode.Created, adjustment.StatusCode);

        var secondOpening = await PostAsync(client, "/api/v1/stock/opening", product.Id, "4.0000", "Deuxième inventaire");
        Assert.Equal(HttpStatusCode.Conflict, secondOpening.StatusCode);
        Assert.Equal("stock.opening_exists", await CodeAsync(secondOpening));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
            var balance = await database.StockBalances.SingleAsync();
            var sum = await database.StockMovements.SumAsync(movement => movement.QuantityDelta);
            Assert.Equal(11.2500m, balance.Quantity);
            Assert.Equal(sum, balance.Quantity);
            Assert.Equal(11.2500m, await database.StockMovements.OrderByDescending(movement => movement.OccurredAtUtc).Select(movement => movement.BalanceAfter).FirstAsync());
        }

        await using var second = new HealthApiFactory(LocalEnvironment.TestConnectionString());
        using var restarted = await AdministratorClientAsync(second);
        var afterRestart = await restarted.GetFromJsonAsync<List<StockBalanceResponse>>("/api/v1/stock/balances", JsonOptions);
        Assert.NotNull(afterRestart);
        Assert.Equal("11.2500", afterRestart.Single().Quantity);
    }

    [Fact]
    public async Task Insufficient_stock_writes_nothing_and_does_not_consume_the_key()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-3", true);
        var key = Guid.NewGuid();

        var rejected = await PostAsync(client, "/api/v1/stock/adjustments", product.Id, "-1.0000", "Sortie", key);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("stock.insufficient", await CodeAsync(rejected));
        using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("0.0000", document.RootElement.GetProperty("available").GetString());
        Assert.Equal("-1.0000", document.RootElement.GetProperty("requested").GetString());

        var accepted = await PostAsync(client, "/api/v1/stock/receipts", product.Id, "1.0000", "Entrée", key);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.StockMovements.CountAsync());
        Assert.Equal(1.0000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Concurrent_receipts_both_apply_on_the_first_balance()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-4", true);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            PostAsync(client, "/api/v1/stock/receipts", product.Id, "1.0000", "Réception A"),
            PostAsync(other, "/api/v1/stock/receipts", product.Id, "1.0000", "Réception B"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(2, await database.StockMovements.CountAsync());
        Assert.Equal(2.0000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(2.0000m, await database.StockMovements.SumAsync(movement => movement.QuantityDelta));
    }

    [Fact]
    public async Task Competing_issues_of_the_last_unit_leave_zero()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-5", true);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/v1/stock/opening", product.Id, "1.0000", "Ouverture")).StatusCode);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            PostAsync(client, "/api/v1/stock/adjustments", product.Id, "-1.0000", "Sortie A"),
            PostAsync(other, "/api/v1/stock/adjustments", product.Id, "-1.0000", "Sortie B"));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("stock.insufficient", await CodeAsync(rejected));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(2, await database.StockMovements.CountAsync());
        Assert.Equal(0m, await database.StockMovements.SumAsync(movement => movement.QuantityDelta));
    }

    [Fact]
    public async Task Cashier_can_read_stock_and_cannot_change_it()
    {
        using var admin = await AdministratorClientAsync();
        var product = await CreateProductAsync(admin, "EAU-6", true);
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier.stock@example.com", password = "cashier-password-1" });
        using var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "cashier.stock@example.com", password = "cashier-password-1" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        using var cashier = _factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var listed = await cashier.GetAsync("/api/v1/stock/balances");
        var opening = await PostAsync(cashier, "/api/v1/stock/opening", product.Id, "1.0000", "Interdit");
        var unauthenticated = await anonymous.GetAsync("/api/v1/stock/balances");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, opening.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(opening));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Validation_rejects_bad_quantities_without_writing()
    {
        using var client = await AdministratorClientAsync();
        var tracked = await CreateProductAsync(client, "EAU-7", true);
        var untracked = await CreateProductAsync(client, "SRV-1", false);

        var missingReason = await PostAsync(client, "/api/v1/stock/receipts", tracked.Id, "1.0000", " ");
        var scale = await PostAsync(client, "/api/v1/stock/receipts", tracked.Id, "1.23456", "Trop précis");
        var zero = await PostAsync(client, "/api/v1/stock/adjustments", tracked.Id, "0.0000", "Nul");
        var negativeOpening = await PostAsync(client, "/api/v1/stock/opening", tracked.Id, "-1.0000", "Négatif");
        var negativeReceipt = await PostAsync(client, "/api/v1/stock/receipts", tracked.Id, "-1.0000", "Négatif");
        var notTracked = await PostAsync(client, "/api/v1/stock/opening", untracked.Id, "1.0000", "Service");

        Assert.Equal("validation.failed", await CodeAsync(missingReason));
        Assert.Equal("validation.failed", await CodeAsync(scale));
        Assert.Equal("validation.failed", await CodeAsync(zero));
        Assert.Equal("validation.failed", await CodeAsync(negativeOpening));
        Assert.Equal("validation.failed", await CodeAsync(negativeReceipt));
        Assert.Equal(HttpStatusCode.Conflict, notTracked.StatusCode);
        Assert.Equal("stock.not_tracked", await CodeAsync(notTracked));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
        Assert.Equal(0, await database.StockIdempotencies.CountAsync());
    }

    [Fact]
    public async Task Idempotency_replays_the_same_result_and_rejects_a_different_body()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-8", true);
        var key = Guid.NewGuid();

        var first = await PostAsync(client, "/api/v1/stock/receipts", product.Id, "1.5000", "Livraison", key);
        var replay = await PostAsync(client, "/api/v1/stock/receipts", product.Id, "1.5000", "Livraison", key);
        var conflict = await PostAsync(client, "/api/v1/stock/receipts", product.Id, "9.0000", "Autre livraison", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<StockMovementResponse>(JsonOptions);
        var repeated = await replay.Content.ReadFromJsonAsync<StockMovementResponse>(JsonOptions);
        Assert.Equal(created!.Id, repeated!.Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("stock.idempotency_conflict", await CodeAsync(conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.StockMovements.CountAsync());
        Assert.Equal(1.5000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Concurrent_identical_keys_create_one_movement()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-9", true);
        var key = Guid.NewGuid();
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            PostAsync(client, "/api/v1/stock/receipts", product.Id, "3.0000", "Double envoi", key),
            PostAsync(other, "/api/v1/stock/receipts", product.Id, "3.0000", "Double envoi", key));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<StockMovementResponse>(JsonOptions)));
        Assert.Equal(bodies[0]!.Id, bodies[1]!.Id);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.StockMovements.CountAsync());
        Assert.Equal(3.0000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Concurrent_reuse_of_a_key_with_a_different_body_creates_one_movement()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-10", true);
        var key = Guid.NewGuid();
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            PostAsync(client, "/api/v1/stock/receipts", product.Id, "2.0000", "Première", key),
            PostAsync(other, "/api/v1/stock/receipts", product.Id, "5.0000", "Deuxième", key));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("stock.idempotency_conflict", await CodeAsync(rejected));
        var created = await responses.Single(response => response.StatusCode == HttpStatusCode.Created)
            .Content.ReadFromJsonAsync<StockMovementResponse>(JsonOptions);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.StockMovements.CountAsync());
        Assert.Equal(decimal.Parse(created!.QuantityDelta, System.Globalization.CultureInfo.InvariantCulture), await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Minimum_warns_and_does_not_block_a_stock_change()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-11", true);

        var minimum = await client.PatchAsJsonAsync(
            $"/api/v1/stock/balances/{product.Id}/minimum",
            new { minimumQuantity = "5.0000" });
        Assert.Equal(HttpStatusCode.OK, minimum.StatusCode);
        var warned = await minimum.Content.ReadFromJsonAsync<StockBalanceResponse>(JsonOptions);
        Assert.Equal("0.0000", warned!.Quantity);
        Assert.True(warned.BelowMinimum);
        Assert.False(warned.AtMinimum);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/v1/stock/receipts", product.Id, "5.0000", "Jusqu'au minimum")).StatusCode);
        var atMinimum = await client.GetFromJsonAsync<List<StockBalanceResponse>>("/api/v1/stock/balances", JsonOptions);
        Assert.NotNull(atMinimum);
        Assert.False(atMinimum.Single().BelowMinimum);
        Assert.True(atMinimum.Single().AtMinimum);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/v1/stock/receipts", product.Id, "1.0000", "Au-dessus")).StatusCode);
        var above = await client.GetFromJsonAsync<List<StockBalanceResponse>>("/api/v1/stock/balances", JsonOptions);
        Assert.NotNull(above);
        Assert.Equal("6.0000", above.Single().Quantity);
        Assert.False(above.Single().BelowMinimum);
        Assert.False(above.Single().AtMinimum);
    }

    [Fact]
    public async Task Historical_movements_cannot_be_updated_or_deleted()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-12", true);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/v1/stock/opening", product.Id, "1.0000", "Ouverture")).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var id = await database.StockMovements.Select(movement => movement.Id).SingleAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE pexilio.stock_movement SET "Reason" = {"changed"} WHERE "Id" = {id}"""));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM pexilio.stock_movement WHERE "Id" = {id}"""));

        Assert.Contains("stock.movement_immutable", update.MessageText, StringComparison.Ordinal);
        Assert.Contains("stock.movement_immutable", delete.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await database.StockMovements.CountAsync());
    }

    private async Task<HttpClient> AdministratorClientAsync(HealthApiFactory? factory = null)
    {
        var host = factory ?? _factory;
        var client = host.CreateClient();
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

    private static async Task<ProductResponse> CreateProductAsync(HttpClient client, string sku, bool trackStock)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku,
            name = "Eau",
            unit = "u",
            unitPrice = "19.5000",
            trackStock,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        Guid productId,
        string quantity,
        string reason,
        Guid? key = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { productId, quantity, reason }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
        return await client.SendAsync(message);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        return body?.Code;
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

    private sealed record ProblemBody(string? Code);
}
