using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pexilio.Application;
using Pexilio.Infrastructure.Identity;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Api.IntegrationTests;

public sealed class Phase1ApiTests : IAsyncLifetime
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
    public async Task Migration_creates_phase1_tables_only_on_the_test_database()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var applied = await database.Database.GetAppliedMigrationsAsync();
        var tables = await TableNamesAsync(database);

        Assert.Contains(applied, id => id.Contains("InitialCreate", StringComparison.Ordinal));
        Assert.Contains(applied, id => id.Contains("CompanyAndAccounts", StringComparison.Ordinal));
        Assert.Contains("company", tables);
        Assert.Contains("tax_rate", tables);
        Assert.Contains("refresh_token", tables);
        Assert.Contains("AspNetUsers", tables);
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
    }

    [Fact]
    public async Task First_setup_persists_one_company_and_a_password_hash()
    {
        using var client = _factory.CreateClient();
        const string password = "correct-horse-battery";

        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", password));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        var company = await database.Companies.Include(item => item.TaxRates).SingleAsync();
        var user = await database.Set<AppUser>().SingleAsync();
        Assert.Equal("Pexilio Démo", company.LegalName);
        Assert.Equal("DZD", company.CurrencyCode);
        Assert.Equal(0.19m, company.TaxRates.Single(rate => rate.IsDefault).Rate);
        Assert.Null(company.Rc);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.DoesNotContain(password, user.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal);
        Assert.True(TestDatabaseNames.IsDisposable(await CurrentDatabaseAsync(database)));
    }

    [Fact]
    public async Task Second_setup_is_rejected_and_does_not_create_another_administrator()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", "correct-horse-battery"))).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/v1/setup", SetupBody("other@example.com", "another-long-password"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("setup.already_complete", await CodeAsync(duplicate));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Companies.CountAsync());
        Assert.Equal(1, await database.Set<AppUser>().CountAsync());
    }

    [Fact]
    public async Task Concurrent_setup_requests_create_one_company()
    {
        using var first = _factory.CreateClient();
        using var second = _factory.CreateClient();
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/v1/setup", SetupBody("one@example.com", "correct-horse-battery")),
            second.PostAsJsonAsync("/api/v1/setup", SetupBody("two@example.com", "another-long-password")));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(1, await database.Companies.CountAsync());
        Assert.Equal(1, await database.Set<AppUser>().CountAsync());
    }

    [Fact]
    public async Task Login_fails_for_a_wrong_password_and_locks_after_repeated_failures()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", "correct-horse-battery"));

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@example.com", password = "not-the-right-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("auth.invalid_credentials", await CodeAsync(wrong));

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@example.com", password = "still-not-the-password" });
        }

        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.Unauthorized, last.StatusCode);
        Assert.Equal("auth.locked_out", await CodeAsync(last));
    }

    [Fact]
    public async Task Login_refresh_reuse_and_logout_follow_the_token_rules()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", "correct-horse-battery"));
        var login = await ReadAsync<TokenPair>(await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "owner@example.com", password = "correct-horse-battery" }));
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.NotEqual(login.AccessToken, login.RefreshToken);

        var rotated = await ReadAsync<TokenPair>(await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken }));
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);

        var reused = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("auth.refresh_reused", await CodeAsync(reused));

        var revoked = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("auth.refresh_invalid", await CodeAsync(revoked));

        var again = await ReadAsync<TokenPair>(await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "owner@example.com", password = "correct-horse-battery" }));
        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = again.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = again.RefreshToken });
        Assert.Equal("auth.refresh_invalid", await CodeAsync(afterLogout));
        var secondLogout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = again.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, secondLogout.StatusCode);
    }

    [Fact]
    public async Task Cashier_cannot_change_the_company_or_create_an_administrator()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", "correct-horse-battery"));
        var admin = await ReadAsync<TokenPair>(await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "owner@example.com", password = "correct-horse-battery" }));

        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        var created = await adminClient.PostAsJsonAsync("/api/v1/users", new { email = "cashier@example.com", password = "cashier-password-1" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<CreatedUser>(JsonOptions);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var cashier = await userManager.FindByIdAsync(createdBody!.UserId.ToString());
            var roles = await userManager.GetRolesAsync(cashier!);
            Assert.Equal(["Cashier"], roles.OrderBy(role => role));
        }

        var cashierLogin = await ReadAsync<TokenPair>(await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "cashier@example.com", password = "cashier-password-1" }));
        using var cashierClient = _factory.CreateClient();
        cashierClient.DefaultRequestHeaders.Authorization = new("Bearer", cashierLogin.AccessToken);

        var patch = await cashierClient.PatchAsJsonAsync("/api/v1/company", CompanyBody());
        var create = await cashierClient.PostAsJsonAsync("/api/v1/users", new { email = "second@example.com", password = "another-long-password" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(patch));

        var anonymous = await client.GetAsync("/api/v1/company");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var updated = await adminClient.PatchAsJsonAsync("/api/v1/company", CompanyBody(currency: "EUR", rate: "0.05"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var company = await updated.Content.ReadFromJsonAsync<CompanyResponse>(JsonOptions);
        Assert.Equal("EUR", company!.CurrencyCode);
        Assert.Equal("0.05", company.DefaultTaxRate);
    }

    [Fact]
    public async Task Setup_rejects_a_short_password_without_writing_a_user()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupBody("owner@example.com", "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", await CodeAsync(response));
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PexilioDbContext>();
        Assert.Equal(0, await database.Set<AppUser>().CountAsync());
        Assert.Equal(0, await database.Companies.CountAsync());
    }

    private static object SetupBody(string email, string password) => new
    {
        legalName = "Pexilio Démo",
        defaultTaxRate = "0.19",
        email,
        password,
    };

    private static object CompanyBody(string currency = "DZD", string rate = "0.19") => new
    {
        legalName = "Pexilio Démo",
        currencyCode = currency,
        defaultTaxRate = rate,
    };

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ProblemBody>(JsonOptions);
        return body?.Code;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return body!;
    }

    private static async Task<List<string>> TableNamesAsync(PexilioDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'pexilio'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
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

    private sealed record CreatedUser(Guid UserId);
}
