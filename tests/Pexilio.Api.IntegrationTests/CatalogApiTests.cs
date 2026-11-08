using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pexilio.Application;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class CatalogApiTests : IAsyncLifetime
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
    public async Task Unit_price_survives_a_round_trip_as_a_string()
    {
        using var client = await AdministratorClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/products", ProductBody("SKU-1", "19.5000"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = await created.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions);

        var listed = await client.GetFromJsonAsync<List<ProductResponse>>("/api/v1/products", JsonOptions);
        Assert.NotNull(listed);
        Assert.Equal("19.5000", product!.UnitPrice);
        Assert.Equal("19.5000", listed.Single().UnitPrice);
        Assert.Equal("0.19", listed[0].TaxRate);
        Assert.True(listed[0].TrackStock);

        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(19.5000m, await database.Products.Select(item => item.UnitPrice).SingleAsync());
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
    }

    [Fact]
    public async Task Duplicate_sku_is_rejected_without_a_second_product()
    {
        using var client = await AdministratorClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/products", ProductBody("abc", "1.0000"))).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/v1/products", ProductBody("ABC", "2.0000"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("products.sku_taken", await CodeAsync(duplicate));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Products.CountAsync());
    }

    [Fact]
    public async Task Category_cycle_is_rejected()
    {
        using var client = await AdministratorClientAsync();
        var root = await ReadCreatedAsync(await client.PostAsJsonAsync("/api/v1/categories", new { name = "Boissons" }));
        var child = await ReadCreatedAsync(await client.PostAsJsonAsync("/api/v1/categories", new { name = "Jus", parentId = root.Id }));

        var cycle = await client.PatchAsJsonAsync($"/api/v1/categories/{root.Id}", new { name = "Boissons", parentId = child.Id });

        Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode);
        Assert.Equal("categories.cycle", await CodeAsync(cycle));
    }

    [Fact]
    public async Task Cashier_can_read_products_and_cannot_change_them()
    {
        using var admin = await AdministratorClientAsync();
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier@example.com", password = "cashier-password-1" });
        using var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "cashier@example.com", password = "cashier-password-1" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        using var cashier = _factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var listed = await cashier.GetAsync("/api/v1/products");
        var created = await cashier.PostAsJsonAsync("/api/v1/products", ProductBody("SKU-2", "3.0000"));
        var unauthenticated = await anonymous.PostAsJsonAsync("/api/v1/products", ProductBody("SKU-3", "4.0000"));

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(created));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task A_price_with_more_than_four_decimals_is_rejected()
    {
        using var client = await AdministratorClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/products", ProductBody("SKU-4", "1.23456"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", await CodeAsync(response));
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
        Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@example.com", password = "correct-horse-battery" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static object ProductBody(string sku, string unitPrice) => new
    {
        sku,
        name = "Eau",
        unit = "u",
        unitPrice,
        trackStock = true,
        isActive = true,
    };

    private static async Task<CategoryResponse> ReadCreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CategoryResponse>(JsonOptions))!;
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
