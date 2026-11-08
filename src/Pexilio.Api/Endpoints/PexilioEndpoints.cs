using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Logging;
using Pexilio.Application;
using Pexilio.Domain;
using Pexilio.Domain.Accounts;
using Pexilio.Domain.Company;

namespace Pexilio.Api.Endpoints;

public static class PexilioEndpoints
{
    public static WebApplication MapPexilioEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/setup/status", async (ISetupService setup, CancellationToken cancellationToken) =>
            Results.Ok(new { complete = await setup.IsCompleteAsync(cancellationToken) }));

        app.MapPost("/api/v1/setup", async (SetupRequest request, ISetupService setup, CancellationToken cancellationToken) =>
        {
            var result = await setup.SetupAsync(request, cancellationToken);
            return ToResult(result, created => Results.Created($"/api/v1/company/{created.CompanyId}", created));
        });

        app.MapPost("/api/v1/auth/login", async (LoginRequest request, ISessionService sessions, CancellationToken cancellationToken) =>
            ToResult(await sessions.LoginAsync(request, cancellationToken), Results.Ok));

        app.MapPost("/api/v1/auth/refresh", async (RefreshRequest request, ISessionService sessions, CancellationToken cancellationToken) =>
            ToResult(await sessions.RefreshAsync(request, cancellationToken), Results.Ok));

        app.MapPost("/api/v1/auth/logout", async (RefreshRequest request, ISessionService sessions, CancellationToken cancellationToken) =>
        {
            await sessions.LogoutAsync(request, cancellationToken);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/company", async (ICompanyService companies, CancellationToken cancellationToken) =>
        {
            var company = await companies.GetAsync(cancellationToken);
            return company is null
                ? Problem(404, ErrorCodes.CompanyMissing)
                : Results.Ok(company);
        }).RequireAuthorization();

        app.MapPatch("/api/v1/company", async (CompanyInput request, ICompanyService companies, CancellationToken cancellationToken) =>
            ToResult(await companies.UpdateAsync(request, cancellationToken), Results.Ok))
            .RequireAuthorization("Administrator");

        app.MapPost("/api/v1/users", async (LoginRequest request, ICashierService cashiers, CancellationToken cancellationToken) =>
            ToResult(await cashiers.CreateAsync(request, cancellationToken), userId => Results.Created($"/api/v1/users/{userId}", new { userId })))
            .RequireAuthorization("Administrator");

        app.MapGet("/api/v1/categories", async (ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.ListCategoriesAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("products.read");

        app.MapPost("/api/v1/categories", async (CategoryRequest request, ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.CreateCategoryAsync(request, cancellationToken), created => Results.Created($"/api/v1/categories/{created.Id}", created)))
            .RequireAuthorization("products.write");

        app.MapPatch("/api/v1/categories/{id:guid}", async (Guid id, CategoryRequest request, ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.UpdateCategoryAsync(id, request, cancellationToken), Results.Ok))
            .RequireAuthorization("products.write");

        app.MapGet("/api/v1/products", async (ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.ListProductsAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("products.read");

        app.MapPost("/api/v1/products", async (ProductRequest request, ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.CreateProductAsync(request, cancellationToken), created => Results.Created($"/api/v1/products/{created.Id}", created)))
            .RequireAuthorization("products.write");

        app.MapPatch("/api/v1/products/{id:guid}", async (Guid id, ProductRequest request, ICatalogService catalog, CancellationToken cancellationToken) =>
            ToResult(await catalog.UpdateProductAsync(id, request, cancellationToken), Results.Ok))
            .RequireAuthorization("products.write");

        app.MapGet("/api/v1/stock/locations", async (IStockService stock, CancellationToken cancellationToken) =>
            ToResult(await stock.ListLocationsAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("stock.read");

        app.MapGet("/api/v1/stock/balances", async (IStockService stock, CancellationToken cancellationToken) =>
            ToResult(await stock.ListBalancesAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("stock.read");

        app.MapGet("/api/v1/stock/movements", async (Guid? productId, IStockService stock, CancellationToken cancellationToken) =>
            ToResult(await stock.ListMovementsAsync(productId, cancellationToken), Results.Ok))
            .RequireAuthorization("stock.read");

        app.MapPost("/api/v1/stock/opening", async (StockQuantityRequest request, HttpRequest http, ClaimsPrincipal user, IStockService stock, CancellationToken cancellationToken) =>
            await PostStockAsync(http, user, (key, userId) => stock.OpenAsync(request, key, userId, cancellationToken)))
            .RequireAuthorization("stock.adjust");

        app.MapPost("/api/v1/stock/receipts", async (StockQuantityRequest request, HttpRequest http, ClaimsPrincipal user, IStockService stock, CancellationToken cancellationToken) =>
            await PostStockAsync(http, user, (key, userId) => stock.ReceiveAsync(request, key, userId, cancellationToken)))
            .RequireAuthorization("stock.adjust");

        app.MapPost("/api/v1/stock/adjustments", async (StockQuantityRequest request, HttpRequest http, ClaimsPrincipal user, IStockService stock, CancellationToken cancellationToken) =>
            await PostStockAsync(http, user, (key, userId) => stock.AdjustAsync(request, key, userId, cancellationToken)))
            .RequireAuthorization("stock.adjust");

        app.MapPatch("/api/v1/stock/balances/{productId:guid}/minimum", async (Guid productId, MinimumStockRequest request, IStockService stock, CancellationToken cancellationToken) =>
            ToResult(await stock.SetMinimumAsync(productId, request, cancellationToken), Results.Ok))
            .RequireAuthorization("stock.adjust");

        app.MapGet("/api/v1/suppliers", async (IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.ListSuppliersAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.read");

        app.MapGet("/api/v1/suppliers/{id:guid}", async (Guid id, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.GetSupplierAsync(id, cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.read");

        app.MapPost("/api/v1/suppliers", async (SupplierRequest request, HttpRequest http, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            await PostIdempotentAsync(http, key => purchasing.CreateSupplierAsync(request, key, cancellationToken), created => $"/api/v1/suppliers/{created.Id}"))
            .RequireAuthorization("purchases.write");

        app.MapPut("/api/v1/suppliers/{id:guid}", async (Guid id, SupplierRequest request, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.UpdateSupplierAsync(id, request, cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.write");

        app.MapGet("/api/v1/purchases", async (string? status, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.ListPurchasesAsync(status, cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.read");

        app.MapGet("/api/v1/purchases/{id:guid}", async (Guid id, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.GetPurchaseAsync(id, cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.read");

        app.MapPost("/api/v1/purchases", async (PurchaseDraftRequest request, HttpRequest http, ClaimsPrincipal user, IPurchasingService purchasing, CancellationToken cancellationToken) =>
        {
            if (!TryUserId(user, out var userId))
            {
                return Problem(401, ErrorCodes.AuthenticationRequired);
            }

            return await PostIdempotentAsync(
                http,
                key => purchasing.CreatePurchaseAsync(request, key, userId, cancellationToken),
                created => $"/api/v1/purchases/{created.Id}");
        }).RequireAuthorization("purchases.write");

        app.MapPut("/api/v1/purchases/{id:guid}", async (Guid id, PurchaseDraftRequest request, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.UpdatePurchaseAsync(id, request, cancellationToken), Results.Ok))
            .RequireAuthorization("purchases.write");

        app.MapDelete("/api/v1/purchases/{id:guid}", async (Guid id, uint? version, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            ToResult(await purchasing.DeletePurchaseAsync(id, version, cancellationToken), _ => Results.NoContent()))
            .RequireAuthorization("purchases.write");

        app.MapPost("/api/v1/purchases/{id:guid}/order", async (Guid id, PurchaseVersionRequest request, HttpRequest http, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            await PostIdempotentOkAsync(http, key => purchasing.OrderPurchaseAsync(id, request, key, cancellationToken)))
            .RequireAuthorization("purchases.write");

        app.MapPost("/api/v1/purchases/{id:guid}/receipts", async (Guid id, PurchaseReceiptRequest request, HttpRequest http, ClaimsPrincipal user, IPurchasingService purchasing, CancellationToken cancellationToken) =>
        {
            if (!TryUserId(user, out var userId))
            {
                return Problem(401, ErrorCodes.AuthenticationRequired);
            }

            return await PostIdempotentAsync(
                http,
                key => purchasing.ReceivePurchaseAsync(id, request, key, userId, cancellationToken),
                created => $"/api/v1/purchases/{id}/receipts/{created.Id}");
        }).RequireAuthorization("purchases.write");

        app.MapPost("/api/v1/purchases/{id:guid}/cancel", async (Guid id, PurchaseVersionRequest request, HttpRequest http, IPurchasingService purchasing, CancellationToken cancellationToken) =>
            await PostIdempotentOkAsync(http, key => purchasing.CancelPurchaseAsync(id, request, key, cancellationToken)))
            .RequireAuthorization("purchases.write");

        app.MapGet("/api/v1/customers", async (string? q, ICustomerService customers, CancellationToken cancellationToken) =>
            ToResult(await customers.ListAsync(q, cancellationToken), Results.Ok))
            .RequireAuthorization("customers.read");

        app.MapGet("/api/v1/customers/{id:guid}", async (Guid id, ICustomerService customers, CancellationToken cancellationToken) =>
            ToResult(await customers.GetAsync(id, cancellationToken), Results.Ok))
            .RequireAuthorization("customers.read");

        app.MapGet("/api/v1/customers/{id:guid}/statement", async (
            Guid id,
            string? from,
            string? to,
            ICustomerService customers,
            CancellationToken cancellationToken) =>
            ToResult(await customers.GetStatementAsync(id, from, to, cancellationToken), Results.Ok))
            .RequireAuthorization("customers.read");

        app.MapPost("/api/v1/customers", async (CustomerRequest request, HttpRequest http, ClaimsPrincipal user, ICustomerService customers, CancellationToken cancellationToken) =>
            await PostIdempotentAsync(
                http,
                key => customers.CreateAsync(request, key, user.IsInRole(RoleNames.Administrator), cancellationToken),
                created => $"/api/v1/customers/{created.Id}"))
            .RequireAuthorization("customers.write");

        app.MapPut("/api/v1/customers/{id:guid}", async (Guid id, CustomerRequest request, ClaimsPrincipal user, ICustomerService customers, CancellationToken cancellationToken) =>
            ToResult(await customers.UpdateAsync(id, request, user.IsInRole(RoleNames.Administrator), cancellationToken), Results.Ok))
            .RequireAuthorization("customers.write");

        app.MapGet("/api/v1/sales", async (ISalesService sales, CancellationToken cancellationToken) =>
            ToResult(await sales.ListAsync(cancellationToken), Results.Ok))
            .RequireAuthorization("sales.read");

        app.MapGet("/api/v1/sales/{id:guid}", async (Guid id, ISalesService sales, CancellationToken cancellationToken) =>
            ToResult(await sales.GetAsync(id, cancellationToken), Results.Ok))
            .RequireAuthorization("sales.read");

        app.MapPost("/api/v1/sales", async (
            SaleFinalizeRequest request,
            HttpRequest http,
            ClaimsPrincipal user,
            ISalesService sales,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!TryUserId(user, out var userId))
            {
                return Problem(401, ErrorCodes.AuthenticationRequired);
            }

            try
            {
                return await PostIdempotentAsync(
                    http,
                    key => sales.FinalizeCashSaleAsync(request, key, userId, user.IsInRole(RoleNames.Administrator), cancellationToken),
                    created => $"/api/v1/sales/{created.Id}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("Pexilio.Api.Sales").LogError(exception, "Cash sale finalization failed.");
                return Problem(500, "request.failed");
            }
        }).RequireAuthorization("sales.write");

        app.MapPost("/api/v1/sales/{id:guid}/returns", async (
            Guid id,
            SaleReturnPostRequest request,
            HttpRequest http,
            ClaimsPrincipal user,
            ISalesService sales,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!TryUserId(user, out var userId))
            {
                return Problem(401, ErrorCodes.AuthenticationRequired);
            }

            try
            {
                return await PostIdempotentAsync(
                    http,
                    key => sales.PostReturnAsync(id, request, key, userId, cancellationToken),
                    created => $"/api/v1/sales/{created.SaleId}/returns/{created.Id}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("Pexilio.Api.Sales").LogError(exception, "Sale return failed.");
                return Problem(500, "request.failed");
            }
        }).RequireAuthorization("sales.write");

        app.MapPost("/api/v1/sales/{id:guid}/payments", async (
            Guid id,
            SalePaymentPostRequest request,
            HttpRequest http,
            ClaimsPrincipal user,
            ISalesService sales,
            CancellationToken cancellationToken) =>
        {
            if (!TryUserId(user, out var userId))
            {
                return Problem(401, ErrorCodes.AuthenticationRequired);
            }

            return await PostIdempotentAsync(
                http,
                key => sales.AddPaymentAsync(id, request, key, userId, cancellationToken),
                created => $"/api/v1/sales/{created.SaleId}");
        }).RequireAuthorization("sales.write");

        return app;
    }

    private static async Task<IResult> PostIdempotentOkAsync<T>(
        HttpRequest http,
        Func<Guid, Task<Result<T>>> post)
    {
        if (!TryIdempotencyKey(http, out var key, out var error))
        {
            return error;
        }

        return ToResult(await post(key), Results.Ok);
    }

    private static async Task<IResult> PostIdempotentAsync<T>(
        HttpRequest http,
        Func<Guid, Task<Result<T>>> post,
        Func<T, string> location)
    {
        if (!TryIdempotencyKey(http, out var key, out var error))
        {
            return error;
        }

        var result = await post(key);
        return ToResult(result, created => Results.Created(location(created), created));
    }

    private static async Task<IResult> PostStockAsync(
        HttpRequest http,
        ClaimsPrincipal user,
        Func<Guid, Guid, Task<Result<StockMovementResponse>>> post)
    {
        if (!TryUserId(user, out var userId))
        {
            return Problem(401, ErrorCodes.AuthenticationRequired);
        }

        if (!TryIdempotencyKey(http, out var key, out var error))
        {
            return error;
        }

        var result = await post(key, userId);
        return ToResult(result, created => Results.Created($"/api/v1/stock/movements/{created.Id}", created));
    }

    private static bool TryUserId(ClaimsPrincipal user, out Guid userId)
    {
        var value = user.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out userId);
    }

    private static bool TryIdempotencyKey(HttpRequest http, out Guid key, out IResult error)
    {
        key = Guid.Empty;
        if (!http.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            error = Problem(400, ErrorCodes.ValidationFailed, [new FieldError("idempotencyKey", "idempotencyKey.required")]);
            return false;
        }

        if (!Guid.TryParse(values.ToString(), out key))
        {
            error = Problem(400, ErrorCodes.ValidationFailed, [new FieldError("idempotencyKey", "idempotencyKey.invalid")]);
            return false;
        }

        if (key == Guid.Empty)
        {
            error = Problem(400, ErrorCodes.ValidationFailed, [new FieldError("idempotencyKey", "idempotencyKey.empty")]);
            return false;
        }

        error = Results.Empty;
        return true;
    }

    private static IResult ToResult<T>(Result<T> result, Func<T, IResult> success)
    {
        if (result.Error is null && result.Value is not null)
        {
            return success(result.Value);
        }

        var error = result.Error ?? OperationFailure.Of(500, "request.failed");
        return Problem(error.StatusCode, error.Code, error.Errors, error.Details);
    }

    private static IResult Problem(
        int statusCode,
        string code,
        IReadOnlyList<FieldError>? errors = null,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (errors is { Count: > 0 })
        {
            extensions["errors"] = errors
                .GroupBy(error => error.Field)
                .ToDictionary(group => group.Key, group => group.Select(error => error.Code).ToArray());
        }

        if (details is not null)
        {
            foreach (var (name, value) in details)
            {
                extensions[name] = value;
            }
        }

        return Results.Problem(title: code, statusCode: statusCode, extensions: extensions);
    }
}

public sealed class ProblemAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        var forbidden = authorizeResult.Forbidden;
        var status = forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        var code = forbidden ? ErrorCodes.Forbidden : ErrorCodes.AuthenticationRequired;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { title = code, status, code });
    }
}
