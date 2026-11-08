using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pexilio.Application;
using Pexilio.Domain.Purchasing;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class PurchasingApiTests : IAsyncLifetime
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
    public async Task Draft_writes_do_not_change_stock_or_the_selling_price()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P1", true, true, "19.5000");
        Assert.Equal(HttpStatusCode.Created, (await PostStockAsync(client, product.Id, "10.0000")).StatusCode);
        var supplier = await CreateSupplierAsync(client, "Atlas");

        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "2.5000", "4.2500", "BL-10");
        var updated = await PutPurchaseAsync(client, created, supplier.Id, product.Id, "3.0000", "4.2500", "BL-10");
        var deleted = await client.DeleteAsync($"/api/v1/purchases/{updated.Id}?version={updated.Version}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(1, await database.StockMovements.CountAsync());
        Assert.Equal(10.0000m, await database.StockBalances.Select(balance => balance.Quantity).SingleAsync());
        Assert.Equal(19.5000m, await database.Products.Select(item => item.UnitPrice).SingleAsync());
        Assert.Equal(0, await database.Purchases.CountAsync());
        Assert.Equal(0, await database.PurchaseLines.CountAsync());
    }

    [Fact]
    public async Task Create_stores_exact_cost_snapshots_and_replay_does_not_create_a_second_draft()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P2", true, true, "19.5000");
        var supplier = await CreateSupplierAsync(client, "Atlas", "123456789");
        var key = Guid.NewGuid();

        var first = await SendPurchaseAsync(client, supplier.Id, product.Id, "1.2500", "3.5000", "fa 12", key);
        var replay = await SendPurchaseAsync(client, supplier.Id, product.Id, "1.2500", "3.5000", "fa 12", key);
        var conflict = await SendPurchaseAsync(client, supplier.Id, product.Id, "9.0000", "3.5000", "fa 12", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);
        var repeated = await replay.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);
        Assert.Equal(created!.Id, repeated!.Id);
        Assert.Equal("1.2500", created.Lines.Single().QuantityOrdered);
        Assert.Equal("3.5000", created.Lines.Single().UnitCost);
        Assert.Equal("19.5000", created.Lines.Single().SellingPrice);
        Assert.Equal("EAU-P2", created.Lines.Single().ProductSku);
        Assert.Equal("Atlas", created.SupplierName);
        Assert.Equal("123456789", created.SupplierTaxId);
        Assert.Equal("fa 12", created.SupplierDocumentRef);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("purchase.idempotency_conflict", await CodeAsync(conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Purchases.CountAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Concurrent_creates_with_one_key_store_a_single_draft()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P3", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var key = Guid.NewGuid();
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            SendPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null, key),
            SendPurchaseAsync(other, supplier.Id, product.Id, "1.0000", "2.0000", null, key));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions)));
        Assert.Equal(bodies[0]!.Id, bodies[1]!.Id);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Purchases.CountAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Stale_version_rejects_the_second_edit_and_leaves_the_first()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P4", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(
            SendPutAsync(client, created, supplier.Id, product.Id, "2.0000", "2.0000", null),
            SendPutAsync(other, created, supplier.Id, product.Id, "4.0000", "2.0000", null));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("purchase.conflict", await CodeAsync(rejected));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.PurchaseLines.CountAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Cashier_can_read_purchases_and_cannot_write_them()
    {
        using var admin = await AdministratorClientAsync();
        var supplier = await CreateSupplierAsync(admin, "Atlas");
        await admin.PostAsJsonAsync("/api/v1/users", new { email = "cashier.buy@example.com", password = "cashier-password-1" });
        using var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = "cashier.buy@example.com", password = "cashier-password-1" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenPair>(JsonOptions);
        using var cashier = _factory.CreateClient();
        cashier.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var listed = await cashier.GetAsync("/api/v1/suppliers");
        var purchases = await cashier.GetAsync("/api/v1/purchases");
        var created = await SendSupplierAsync(cashier, "Nord", Guid.NewGuid());
        var draft = await SendPurchaseAsync(cashier, supplier.Id, Guid.NewGuid(), "1.0000", "1.0000", null, Guid.NewGuid());
        var editedSupplier = await cashier.PutAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { name = "Nord", isActive = true });
        var edited = await cashier.PutAsJsonAsync($"/api/v1/purchases/{Guid.NewGuid()}", new { supplierId = supplier.Id, version = 1 });
        var deleted = await cashier.DeleteAsync($"/api/v1/purchases/{Guid.NewGuid()}");
        var unauthenticated = await anonymous.GetAsync("/api/v1/purchases");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, purchases.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(created));
        Assert.Equal(HttpStatusCode.Forbidden, draft.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, editedSupplier.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edited.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal("Atlas", await database.Suppliers.Select(item => item.Name).SingleAsync());
    }

    [Fact]
    public async Task Validation_rejects_inactive_or_untracked_products_and_a_repeated_supplier_reference()
    {
        using var client = await AdministratorClientAsync();
        var active = await CreateProductAsync(client, "EAU-P5", true, true, "10.0000");
        var inactive = await CreateProductAsync(client, "EAU-P6", false, true, "10.0000");
        var service = await CreateProductAsync(client, "SRV-P1", true, false, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var paused = await CreateSupplierAsync(client, "Pause");
        var pausedUpdate = await client.PutAsJsonAsync($"/api/v1/suppliers/{paused.Id}", new { name = "Pause", isActive = false });
        Assert.Equal(HttpStatusCode.OK, pausedUpdate.StatusCode);
        await CreateSupplierAsync(client, "Atlas Nord");
        var first = await PostPurchaseAsync(client, supplier.Id, active.Id, "1.0000", "2.0000", "BL-1");

        var inactiveSupplier = await SendPurchaseAsync(client, paused.Id, active.Id, "1.0000", "2.0000", null, Guid.NewGuid());
        var inactiveLine = await SendPurchaseAsync(client, supplier.Id, inactive.Id, "1.0000", "2.0000", null, Guid.NewGuid());
        var untrackedLine = await SendPurchaseAsync(client, supplier.Id, service.Id, "1.0000", "2.0000", null, Guid.NewGuid());
        var repeatedRef = await SendPurchaseAsync(client, supplier.Id, active.Id, "1.0000", "2.0000", "bl-1", Guid.NewGuid());
        var blankRef = await SendPurchaseAsync(client, supplier.Id, active.Id, "1.0000", "2.0000", "   ", Guid.NewGuid());
        var badRef = await SendPurchaseAsync(client, supplier.Id, active.Id, "1.0000", "2.0000", "BL@2", Guid.NewGuid());

        Assert.Equal("suppliers.inactive", await CodeAsync(inactiveSupplier));
        Assert.Equal("purchase.product_inactive", await CodeAsync(inactiveLine));
        Assert.Equal("stock.not_tracked", await CodeAsync(untrackedLine));
        Assert.Equal("purchase.supplier_document_taken", await CodeAsync(repeatedRef));
        Assert.Equal(HttpStatusCode.Created, blankRef.StatusCode);
        var blank = await blankRef.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);
        Assert.Null(blank!.SupplierDocumentRef);
        Assert.Equal(HttpStatusCode.BadRequest, badRef.StatusCode);
        var moved = await SendPutAsync(client, first, paused.Id, active.Id, "1.0000", "2.0000", "BL-1");
        var inactiveEdit = await SendPutAsync(client, first, supplier.Id, inactive.Id, "1.0000", "2.0000", "BL-1");
        Assert.Equal("suppliers.inactive", await CodeAsync(moved));
        Assert.Equal("purchase.product_inactive", await CodeAsync(inactiveEdit));
        var unchanged = await client.GetFromJsonAsync<PurchaseResponse>($"/api/v1/purchases/{first.Id}", JsonOptions);
        Assert.Equal(PurchaseStatuses.Draft, unchanged!.Status);
        Assert.Equal("BL-1", unchanged.SupplierDocumentRef);
        Assert.Equal(active.Id, unchanged.Lines.Single().ProductId);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
        Assert.Equal(10.0000m, (await database.Products.SingleAsync(item => item.Sku == "EAU-P5")).UnitPrice);
    }

    [Fact]
    public async Task Replay_after_delete_does_not_return_or_recreate_the_purchase()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P10", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var key = Guid.NewGuid();
        var first = await SendPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.5000", "BL-9", key);
        var created = await first.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/purchases/{created!.Id}?version={created.Version}")).StatusCode);

        var replay = await SendPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.5000", "BL-9", key);
        var different = await SendPurchaseAsync(client, supplier.Id, product.Id, "3.0000", "2.5000", "BL-9", key);
        var body = await replay.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemBody>(body, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal("purchases.not_found", problem?.Code);
        Assert.DoesNotContain(created.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.Number, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\":\"draft\"", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("purchase.idempotency_conflict", await CodeAsync(different));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/purchases/{created.Id}")).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(0, await database.Purchases.CountAsync());
        Assert.Equal(0, await database.PurchaseLines.CountAsync());
        Assert.Equal(1, await database.PurchaseIdempotencies.CountAsync(item => item.Operation == PurchaseOperations.PurchaseCreate));
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
    }

    [Fact]
    public async Task A_request_cannot_store_a_status_other_than_draft()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P11", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases")
        {
            Content = JsonContent.Create(new
            {
                supplierId = supplier.Id,
                status = PurchaseStatuses.Received,
                lines = new[] { new { productId = product.Id, quantity = "1.0000", unitCost = "2.0000" } },
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var createdResponse = await client.SendAsync(message);
        var created = await createdResponse.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);
        var edited = await client.PutAsJsonAsync($"/api/v1/purchases/{created!.Id}", new
        {
            supplierId = supplier.Id,
            status = PurchaseStatuses.Ordered,
            version = created.Version,
            lines = new[] { new { productId = product.Id, quantity = "1.0000", unitCost = "2.0000" } },
        });
        var after = await edited.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal(PurchaseStatuses.Draft, created.Status);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(PurchaseStatuses.Draft, after!.Status);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(PurchaseStatuses.Draft, await database.Purchases.Select(purchase => purchase.Status).SingleAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
    }

    [Fact]
    public async Task A_renamed_supplier_or_product_does_not_change_a_saved_draft_until_the_draft_is_saved_again()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P7", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas", "111");
        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        var supplierUpdate = await client.PutAsJsonAsync($"/api/v1/suppliers/{supplier.Id}", new { name = "Atlas Sud", taxId = "222", isActive = true });
        var productUpdate = await client.PatchAsJsonAsync($"/api/v1/products/{product.Id}", new
        {
            sku = "EAU-NEW",
            name = "Eau nouvelle",
            unit = "carton",
            unitPrice = "10.0000",
            trackStock = true,
            isActive = true,
        });
        Assert.Equal(HttpStatusCode.OK, supplierUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, productUpdate.StatusCode);

        var before = await client.GetFromJsonAsync<PurchaseResponse>($"/api/v1/purchases/{created.Id}", JsonOptions);
        Assert.Equal("Atlas", before!.SupplierName);
        Assert.Equal("111", before.SupplierTaxId);
        Assert.Equal("EAU-P7", before.Lines.Single().ProductSku);
        Assert.Equal("Eau", before.Lines.Single().ProductName);

        var after = await PutPurchaseAsync(client, before, supplier.Id, product.Id, "1.0000", "2.0000", null);
        Assert.Equal("Atlas Sud", after.SupplierName);
        Assert.Equal("222", after.SupplierTaxId);
        Assert.Equal("EAU-NEW", after.Lines.Single().ProductSku);
        Assert.Equal("Eau nouvelle", after.Lines.Single().ProductName);
        Assert.Equal("carton", after.Lines.Single().ProductUnit);
    }

    [Fact]
    public async Task Purchase_numbers_are_not_reused_after_a_draft_is_deleted()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P8", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var before = await SequenceStateAsync();
        var invalid = await SendPurchaseAsync(client, supplier.Id, product.Id, "0.0000", "1.0000", null, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(before, await SequenceStateAsync());

        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        Assert.StartsWith("A-", created.Number);
        await client.DeleteAsync($"/api/v1/purchases/{created.Id}?version={created.Version}");
        var next = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        Assert.NotEqual(created.Number, next.Number);
    }

    [Fact]
    public async Task An_ordered_purchase_cannot_be_edited_or_deleted()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P9", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
            await database.Purchases.Where(purchase => purchase.Id == created.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(purchase => purchase.Status, PurchaseStatuses.Ordered));
        }

        var updated = await SendPutAsync(client, created with { Version = created.Version + 1 }, supplier.Id, product.Id, "2.0000", "2.0000", null);
        var deleted = await client.DeleteAsync($"/api/v1/purchases/{created.Id}?version={created.Version}");

        Assert.Equal(HttpStatusCode.Conflict, updated.StatusCode);
        Assert.Equal("purchase.not_draft", await CodeAsync(updated));
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("purchase.not_draft", await CodeAsync(deleted));
        await using var check = _factory.Services.CreateAsyncScope();
        var stored = check.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(PurchaseStatuses.Ordered, await stored.Purchases.Select(purchase => purchase.Status).SingleAsync());
        Assert.Equal(0, await stored.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Stale_version_cannot_delete_a_newer_draft()
    {
        using var client = await AdministratorClientAsync();
        var product = await CreateProductAsync(client, "EAU-P12", true, true, "10.0000");
        var supplier = await CreateSupplierAsync(client, "Atlas");
        var created = await PostPurchaseAsync(client, supplier.Id, product.Id, "1.0000", "2.0000", null);
        var updated = await PutPurchaseAsync(client, created, supplier.Id, product.Id, "2.0000", "2.0000", null);
        Assert.NotEqual(created.Version, updated.Version);

        var stale = await client.DeleteAsync($"/api/v1/purchases/{created.Id}?version={created.Version}");
        var current = await client.GetFromJsonAsync<PurchaseResponse>($"/api/v1/purchases/{created.Id}", JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("purchase.conflict", await CodeAsync(stale));
        Assert.Equal(updated.Version, current!.Version);
        Assert.Equal("2.0000", current.Lines.Single().QuantityOrdered);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(1, await database.Purchases.CountAsync());
        Assert.Equal(1, await database.PurchaseLines.CountAsync());
        Assert.Equal(0, await database.StockMovements.CountAsync());
        Assert.Equal(0, await database.StockBalances.CountAsync());
    }

    [Fact]
    public async Task An_empty_idempotency_key_is_rejected()
    {
        using var client = await AdministratorClientAsync();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases")
        {
            Content = JsonContent.Create(new { supplierId = Guid.NewGuid(), lines = Array.Empty<object>() }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.Empty.ToString("D"));
        var response = await client.SendAsync(message);
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", body?.Code);
        Assert.NotNull(body?.Errors);
        Assert.Equal("idempotencyKey.empty", Assert.Single(body.Errors["idempotencyKey"]));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
        Assert.Equal(0, await database.Purchases.CountAsync());
        Assert.Equal(0, await database.PurchaseIdempotencies.CountAsync());
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

    private static async Task<ProductResponse> CreateProductAsync(
        HttpClient client,
        string sku,
        bool isActive,
        bool trackStock,
        string unitPrice)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku,
            name = "Eau",
            unit = "u",
            unitPrice,
            trackStock,
            isActive,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>(JsonOptions))!;
    }

    private static async Task<SupplierResponse> CreateSupplierAsync(HttpClient client, string name, string? taxId = null)
    {
        var response = await SendSupplierAsync(client, name, Guid.NewGuid(), taxId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SupplierResponse>(JsonOptions))!;
    }

    private static async Task<HttpResponseMessage> SendSupplierAsync(HttpClient client, string name, Guid key, string? taxId = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/suppliers")
        {
            Content = JsonContent.Create(new { name, taxId, isActive = true }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return await client.SendAsync(message);
    }

    private static async Task<PurchaseResponse> PostPurchaseAsync(
        HttpClient client,
        Guid supplierId,
        Guid productId,
        string quantity,
        string unitCost,
        string? documentRef)
    {
        var response = await SendPurchaseAsync(client, supplierId, productId, quantity, unitCost, documentRef, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> SendPurchaseAsync(
        HttpClient client,
        Guid supplierId,
        Guid productId,
        string quantity,
        string unitCost,
        string? documentRef,
        Guid key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchases")
        {
            Content = JsonContent.Create(new
            {
                supplierId,
                supplierDocumentRef = documentRef,
                lines = new[] { new { productId, quantity, unitCost } },
            }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        return client.SendAsync(message);
    }

    private static async Task<PurchaseResponse> PutPurchaseAsync(
        HttpClient client,
        PurchaseResponse current,
        Guid supplierId,
        Guid productId,
        string quantity,
        string unitCost,
        string? documentRef)
    {
        var response = await SendPutAsync(client, current, supplierId, productId, quantity, unitCost, documentRef);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PurchaseResponse>(JsonOptions))!;
    }

    private static Task<HttpResponseMessage> SendPutAsync(
        HttpClient client,
        PurchaseResponse current,
        Guid supplierId,
        Guid productId,
        string quantity,
        string unitCost,
        string? documentRef) =>
        client.PutAsJsonAsync($"/api/v1/purchases/{current.Id}", new
        {
            supplierId,
            supplierDocumentRef = documentRef,
            version = current.Version,
            lines = new[] { new { productId, quantity, unitCost } },
        });

    private static async Task<HttpResponseMessage> PostStockAsync(HttpClient client, Guid productId, string quantity)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/opening")
        {
            Content = JsonContent.Create(new { productId, quantity, reason = "Inventaire" }),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return await client.SendAsync(message);
    }

    private async Task<string> SequenceStateAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_value::text || ':' || is_called::text FROM pexilio.purchase_number_seq";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        return body?.Code;
    }

    private static async Task<string> CurrentDatabaseAsync(PexilioDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ProblemBody(string? Code, Dictionary<string, string[]>? Errors);
}
