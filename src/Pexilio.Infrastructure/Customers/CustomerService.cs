using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pexilio.Application;
using Pexilio.Domain;
using Pexilio.Domain.Catalog;
using Pexilio.Domain.Company;
using Pexilio.Domain.Customers;
using Pexilio.Domain.Purchasing;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Infrastructure.Customers;

public sealed class CustomerService(PexilioDbContext database, TimeProvider time) : ICustomerService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<IReadOnlyList<CustomerResponse>>> ListAsync(string? query, CancellationToken cancellationToken)
    {
        var company = await CompanyAsync(cancellationToken);
        if (company is null)
        {
            return Result<IReadOnlyList<CustomerResponse>>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var key = NormalizeQuery(query);
        if (!string.IsNullOrWhiteSpace(query) && key.Length == 0)
        {
            return Result<IReadOnlyList<CustomerResponse>>.Success([]);
        }

        var listed = await CustomerAccounts.ReadCustomersAsync(database, company.Id, key, null, cancellationToken);
        return Result<IReadOnlyList<CustomerResponse>>.Success(listed.Select(ToResponse).ToList());
    }

    public async Task<Result<CustomerResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var company = await CompanyAsync(cancellationToken);
        if (company is null)
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var listed = await CustomerAccounts.ReadCustomersAsync(database, company.Id, "", id, cancellationToken);
        if (listed.Count == 0)
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CustomerNotFound));
        }

        if (listed[0].Outstanding < 0m)
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity));
        }

        return Result<CustomerResponse>.Success(ToResponse(listed[0]));
    }

    public async Task<Result<CustomerStatementResponse>> GetStatementAsync(
        Guid id,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        var company = await CompanyAsync(cancellationToken);
        if (company is null)
        {
            return Result<CustomerStatementResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        if (!CustomerStatementWindowRules.TryResolve(
                from,
                to,
                company.TimeZoneId,
                time.GetUtcNow(),
                out var window,
                out var error))
        {
            return Result<CustomerStatementResponse>.Fail(OperationFailure.Validation([error!]));
        }

        var statement = await CustomerAccounts.ReadStatementAsync(
            database,
            company.Id,
            id,
            window.FromUtc,
            window.ToUtc,
            cancellationToken);
        if (statement is null)
        {
            return Result<CustomerStatementResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CustomerNotFound));
        }

        if (statement.Opening < 0m || statement.Closing < 0m || statement.Outstanding < 0m)
        {
            return Result<CustomerStatementResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity));
        }

        return Result<CustomerStatementResponse>.Success(new CustomerStatementResponse(
            statement.CustomerId,
            statement.CustomerName,
            company.TimeZoneId,
            window.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            window.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            QuantityText.Format(statement.Opening),
            QuantityText.Format(statement.Closing),
            QuantityText.Format(statement.Outstanding),
            statement.Lines
                .Select(line => new CustomerStatementLineResponse(
                    line.OccurredAt,
                    line.Kind,
                    line.DocumentId,
                    line.DocumentNumber,
                    QuantityText.Format(line.Amount),
                    QuantityText.Format(line.RunningBalance),
                    line.CreditLimitOverridden,
                    line.CreditOverrideReason,
                    Money(line.CreditLimitSnapshot, limit: true),
                    Money(line.CreditOutstandingBefore, limit: false),
                    Money(line.CreditProjectedOutstanding, limit: false)))
                .ToArray()));
    }

    public async Task<Result<CustomerResponse>> CreateAsync(
        CustomerRequest request,
        Guid idempotencyKey,
        bool administrator,
        CancellationToken cancellationToken)
    {
        if (!CustomerValidator.TryCustomer(
                request.Type,
                request.Name,
                request.Phone,
                request.Address,
                request.Notes,
                request.Rc,
                request.Nif,
                request.Nis,
                request.Ai,
                request.CreditLimit,
                request.IsActive,
                out var requested,
                out var errors))
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Validation(errors));
        }

        if (!CustomerAccess.TryCreate(administrator, requested, out var draft, out var forbidden))
        {
            return Result<CustomerResponse>.Fail(new OperationFailure(403, ErrorCodes.Forbidden, forbidden));
        }

        var hash = Fingerprint(CustomerOperations.Create, Canonical(draft));
        await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
        var company = await CompanyAsync(cancellationToken);
        if (company is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var now = time.GetUtcNow();
        var claim = new CustomerIdempotency
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id,
            Operation = CustomerOperations.Create,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            ResponseJson = "",
            CreatedAtUtc = now,
        };
        database.CustomerIdempotencies.Add(claim);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyConflict(exception))
        {
            database.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            return await ReplayAsync(company.Id, idempotencyKey, hash, cancellationToken);
        }

        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id,
            Type = draft.Type,
            Name = draft.Name,
            NameKey = draft.NameKey,
            Phone = draft.Phone,
            Address = draft.Address,
            Notes = draft.Notes,
            Rc = draft.Rc,
            Nif = draft.Nif,
            Nis = draft.Nis,
            Ai = draft.Ai,
            CreditLimit = draft.CreditLimit,
            IsActive = draft.IsActive ?? true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        database.Customers.Add(customer);
        var response = ToResponse(customer, 0m);
        claim.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        await database.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Result<CustomerResponse>.Success(response);
    }

    public async Task<Result<CustomerResponse>> UpdateAsync(
        Guid id,
        CustomerRequest request,
        bool administrator,
        CancellationToken cancellationToken)
    {
        if (!CustomerValidator.TryCustomer(
                request.Type,
                request.Name,
                request.Phone,
                request.Address,
                request.Notes,
                request.Rc,
                request.Nif,
                request.Nis,
                request.Ai,
                request.CreditLimit,
                request.IsActive,
                out var requested,
                out var errors))
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Validation(errors));
        }

        await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
        var company = await CompanyAsync(cancellationToken);
        if (company is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var customer = await database.Customers.SingleOrDefaultAsync(
            item => item.Id == id && item.CompanyId == company.Id,
            cancellationToken);
        if (customer is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CustomerNotFound));
        }

        var current = new CustomerStored(
            customer.Type,
            customer.Rc,
            customer.Nif,
            customer.Nis,
            customer.Ai,
            customer.CreditLimit,
            customer.IsActive);
        if (!CustomerAccess.TryUpdate(administrator, requested, current, out var draft, out var forbidden))
        {
            await tx.RollbackAsync(cancellationToken);
            return Result<CustomerResponse>.Fail(new OperationFailure(403, ErrorCodes.Forbidden, forbidden));
        }

        customer.Type = draft.Type;
        customer.Name = draft.Name;
        customer.NameKey = draft.NameKey;
        customer.Phone = draft.Phone;
        customer.Address = draft.Address;
        customer.Notes = draft.Notes;
        customer.Rc = draft.Rc;
        customer.Nif = draft.Nif;
        customer.Nis = draft.Nis;
        customer.Ai = draft.Ai;
        customer.CreditLimit = draft.CreditLimit;
        customer.IsActive = draft.IsActive ?? customer.IsActive;
        customer.UpdatedAtUtc = time.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
        var balances = await CustomerAccounts.ReadCustomersAsync(database, company.Id, "", customer.Id, cancellationToken);
        if (balances.Count == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return Result<CustomerResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CustomerNotFound));
        }

        await tx.CommitAsync(cancellationToken);
        return Result<CustomerResponse>.Success(ToResponse(balances[0]));
    }

    private async Task<Result<CustomerResponse>> ReplayAsync(
        Guid companyId,
        Guid idempotencyKey,
        string hash,
        CancellationToken cancellationToken)
    {
        var claim = await database.CustomerIdempotencies.AsNoTracking().SingleOrDefaultAsync(
            item => item.CompanyId == companyId
                && item.Operation == CustomerOperations.Create
                && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (claim is null || claim.RequestHash != hash || string.IsNullOrEmpty(claim.ResponseJson))
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(409, ErrorCodes.CustomerIdempotencyConflict));
        }

        var stored = JsonSerializer.Deserialize<CustomerResponse>(claim.ResponseJson, JsonOptions);
        if (stored is null)
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(409, ErrorCodes.CustomerIdempotencyConflict));
        }

        var balances = await CustomerAccounts.ReadCustomersAsync(database, companyId, "", stored.Id, cancellationToken);
        if (balances.Count == 0)
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(409, ErrorCodes.CustomerIdempotencyConflict));
        }

        if (!CustomerReplay.TryWithCurrentOutstanding(stored, balances[0].Outstanding, out var current))
        {
            return Result<CustomerResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity));
        }

        return Result<CustomerResponse>.Success(current);
    }

    private Task<Company?> CompanyAsync(CancellationToken cancellationToken) =>
        database.Companies.SingleOrDefaultAsync(cancellationToken);

    private static string NormalizeQuery(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > CustomerValidator.NameMaxLength)
        {
            return "";
        }

        var key = PurchasingValidator.Key(trimmed);
        return key.Length > CustomerValidator.NameMaxLength ? "" : key;
    }

    private static string? Money(decimal? amount, bool limit) =>
        amount is decimal value
            ? limit ? UnitPriceText.Format(value) : QuantityText.Format(value)
            : null;

    private static CustomerResponse ToResponse(Customer customer, decimal outstanding) =>
        new(
            customer.Id,
            customer.Type,
            customer.Name,
            customer.Phone,
            customer.Address,
            customer.Notes,
            customer.Rc,
            customer.Nif,
            customer.Nis,
            customer.Ai,
            customer.CreditLimit is decimal limit ? UnitPriceText.Format(limit) : null,
            customer.IsActive,
            QuantityText.Format(outstanding));

    private static CustomerResponse ToResponse(CustomerAccountRow customer) =>
        new(
            customer.Id,
            customer.Type,
            customer.Name,
            customer.Phone,
            customer.Address,
            customer.Notes,
            customer.Rc,
            customer.Nif,
            customer.Nis,
            customer.Ai,
            customer.CreditLimit is decimal limit ? UnitPriceText.Format(limit) : null,
            customer.IsActive,
            QuantityText.Format(customer.Outstanding));

    private static string Canonical(CustomerDraft draft)
    {
        var parts = new[]
        {
            draft.Type,
            draft.NameKey,
            draft.Phone ?? "",
            draft.Address ?? "",
            draft.Notes ?? "",
            draft.Rc ?? "",
            draft.Nif ?? "",
            draft.Nis ?? "",
            draft.Ai ?? "",
            draft.CreditLimit is decimal limit ? UnitPriceText.Format(limit) : "",
            (draft.IsActive ?? true) ? "1" : "0",
        };
        return string.Join('\u001f', parts);
    }

    private static string Fingerprint(string operation, string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(operation + '\u001f' + canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool IsIdempotencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: not null } postgres
                && postgres.ConstraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
