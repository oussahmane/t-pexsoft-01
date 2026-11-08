using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pexilio.Application;
using Pexilio.Domain;
using Pexilio.Domain.Catalog;
using Pexilio.Domain.Company;
using Pexilio.Domain.Customers;
using Pexilio.Domain.Sales;
using Pexilio.Domain.Stock;
using Pexilio.Infrastructure.Customers;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Infrastructure.Sales;

public sealed class SalesService(
    PexilioDbContext database,
    TimeProvider time,
    IStockService stock,
    ISaleProductLock productLock,
    ILogger<SalesService> logger) : ISalesService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<IReadOnlyList<SaleSummaryResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        var company = await database.Companies.SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return Result<IReadOnlyList<SaleSummaryResponse>>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var sales = await database.Sales.AsNoTracking()
            .Where(sale => sale.CompanyId == company.Id)
            .OrderByDescending(sale => sale.CreatedAtUtc)
            .ThenByDescending(sale => sale.Number)
            .Select(sale => new
            {
                sale.Id,
                sale.Number,
                sale.Total,
                sale.CreatedAtUtc,
                sale.CustomerId,
                sale.CustomerName,
                sale.CreditLimitOverridden,
                sale.CreditOverrideReason,
                sale.CreditLimitSnapshot,
                sale.CreditOutstandingBefore,
                sale.CreditProjectedOutstanding,
                Paid = sale.Payments.Sum(payment => (decimal?)payment.Amount) ?? 0m,
                Returned = database.SaleReturnLines.Where(line => line.SaleId == sale.Id).Sum(line => (decimal?)line.Amount) ?? 0m,
                Refunded = database.SaleRefunds.Where(refund => refund.SaleId == sale.Id && refund.CompanyId == sale.CompanyId).Sum(refund => (decimal?)refund.Amount) ?? 0m,
            })
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<SaleSummaryResponse>>.Success(sales.Select(sale =>
        {
            var balance = Balance(sale.Total, sale.Returned, sale.Paid, sale.Refunded);
            return new SaleSummaryResponse(
                sale.Id,
                sale.Number,
                QuantityText.Format(sale.Total),
                sale.CreatedAtUtc,
                sale.CustomerId,
                sale.CustomerName,
                QuantityText.Format(balance.Paid),
                QuantityText.Format(sale.Refunded),
                QuantityText.Format(balance.Outstanding),
                balance.State,
                sale.CreditLimitOverridden,
                sale.CreditOverrideReason,
                FormatLimit(sale.CreditLimitSnapshot),
                FormatAmount(sale.CreditOutstandingBefore),
                FormatAmount(sale.CreditProjectedOutstanding));
        }).ToList());
    }

    public async Task<Result<SaleResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var company = await database.Companies.SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return Result<SaleResponse>.Fail(OperationFailure.Of(404, ErrorCodes.CompanyMissing));
        }

        var sale = await database.Sales.AsNoTracking()
            .Include(item => item.Lines)
            .Include(item => item.Payments)
            .SingleOrDefaultAsync(item => item.Id == id && item.CompanyId == company.Id, cancellationToken);
        if (sale is null)
        {
            return Result<SaleResponse>.Fail(OperationFailure.Of(404, ErrorCodes.SaleNotFound));
        }

        var returned = await ReturnedAmountAsync(company.Id, sale.Id, cancellationToken);
        var refunded = await RefundedAmountAsync(company.Id, sale.Id, cancellationToken);
        var returnedByLine = await ReturnedByLineAsync(company.Id, sale.Id, cancellationToken);
        return Result<SaleResponse>.Success(ToResponse(sale, returned, refunded, returnedByLine));
    }

    public async Task<Result<SaleResponse>> FinalizeCashSaleAsync(
        SaleFinalizeRequest request,
        Guid idempotencyKey,
        Guid userId,
        bool administrator,
        CancellationToken cancellationToken)
    {
        var requestedLines = request.Lines?.Select(line => new SaleLineInput(
            line.ProductId,
            line.Quantity,
            line.UnitPrice,
            line.LineTotal,
            line.PriceKind,
            line.LineDiscount,
            line.DiscountKind,
            line.DiscountRate)).ToArray();
        if (SaleCustomerLink.Problem(request.CustomerId, found: true, active: true) == SaleCustomerLink.Invalid)
        {
            return Result<SaleResponse>.Fail(OperationFailure.Validation([new FieldError("customerId", SaleCustomerLink.Invalid)]));
        }

        var requestedPayments = request.Payments?.Select(payment => (payment.Method, payment.Amount)).ToArray();
        if (!SaleValidator.TryCashSale(
                requestedLines,
                request.Total,
                requestedPayments,
                request.DiscountAmount,
                request.DiscountKind,
                request.DiscountRate,
                request.CustomerId is Guid,
                out var draft,
                out var errors))
        {
            return Result<SaleResponse>.Fail(OperationFailure.Validation(errors));
        }

        if (!CustomerCredit.TryPrepare(
                request.CustomerId is Guid,
                administrator,
                request.CreditOverride,
                request.CreditOverrideReason,
                out var creditReason,
                out var creditProblem))
        {
            var creditError = new FieldError(creditProblem.Field, creditProblem.FieldCode);
            return Result<SaleResponse>.Fail(creditProblem.Status == 403
                ? new OperationFailure(403, creditProblem.Code, [creditError])
                : OperationFailure.Validation([creditError]));
        }

        var hash = Fingerprint(
            SaleOperations.SaleFinalize,
            Canonical(draft, request.CustomerId, request.CreditOverride, creditReason));
        await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
        var company = await database.Companies.SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return await AbortAsync(tx, OperationFailure.Of(404, ErrorCodes.CompanyMissing), cancellationToken);
        }

        var claim = new SaleIdempotency
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id,
            Operation = SaleOperations.SaleFinalize,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            ResponseJson = "",
            CreatedAtUtc = time.GetUtcNow(),
        };
        database.SaleIdempotencies.Add(claim);
        var claimed = await SaveClaimAsync(cancellationToken);
        if (claimed == ClaimSave.Conflict)
        {
            database.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            return await ReplayAsync(company.Id, idempotencyKey, hash, cancellationToken);
        }

        if (claimed == ClaimSave.Failed)
        {
            return await AbortAsync(tx, UnexpectedFailure(), cancellationToken);
        }

        Customer? customer = null;
        if (request.CustomerId is Guid customerId)
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM pexilio.customer WHERE "CompanyId" = {company.Id} AND "Id" = {customerId} FOR UPDATE""",
                cancellationToken);
            customer = await database.Customers.SingleOrDefaultAsync(
                item => item.Id == customerId && item.CompanyId == company.Id,
                cancellationToken);
            var problem = SaleCustomerLink.Problem(customerId, customer is not null, customer?.IsActive == true);
            if (problem == ErrorCodes.CustomerNotFound)
            {
                return await AbortAsync(tx, OperationFailure.Of(404, ErrorCodes.CustomerNotFound), cancellationToken);
            }

            if (problem == ErrorCodes.CustomerInactive)
            {
                return await AbortAsync(tx, OperationFailure.Of(409, ErrorCodes.CustomerInactive), cancellationToken);
            }
        }

        var productIds = draft.Lines.Select(line => line.ProductId).OrderBy(id => id).ToArray();
        foreach (var productId in productIds)
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM pexilio.product WHERE "CompanyId" = {company.Id} AND "Id" = {productId} FOR UPDATE""",
                cancellationToken);
        }

        var products = await database.Products
            .Where(product => product.CompanyId == company.Id && productIds.Contains(product.Id))
            .ToListAsync(cancellationToken);
        var taxIds = products.Select(product => product.TaxRateId).Distinct().ToArray();
        var taxes = await database.TaxRates
            .Where(rate => rate.CompanyId == company.Id && taxIds.Contains(rate.Id))
            .ToDictionaryAsync(rate => rate.Id, cancellationToken);

        var staged = new List<PricedLine>(draft.Lines.Count);
        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines[index];
            var product = products.SingleOrDefault(item => item.Id == line.ProductId);
            if (product is null)
            {
                return await AbortAsync(tx, OperationFailure.Of(404, ErrorCodes.ProductNotFound), cancellationToken);
            }

            if (!product.IsActive)
            {
                return await AbortAsync(tx, OperationFailure.Of(409, ErrorCodes.SaleProductInactive), cancellationToken);
            }

            if (!taxes.TryGetValue(product.TaxRateId, out var tax))
            {
                return await AbortAsync(
                    tx,
                    OperationFailure.Validation([new FieldError("taxRateId", "taxRateId.unknown")]),
                    cancellationToken);
            }

            if (!TryPrice(draft.Extended, administrator, index + 1, line, product, tax, out var priced, out var priceError))
            {
                return await AbortAsync(tx, OperationFailure.Validation([priceError]), cancellationToken);
            }

            staged.Add(priced);
        }

        var subtotal = staged.Sum(line => line.LineTotal);
        if (subtotal - draft.DiscountAmount != draft.Total)
        {
            return await AbortAsync(
                tx,
                OperationFailure.Validation([new FieldError("total", "total.mismatch")]),
                cancellationToken);
        }

        if (!SaleRules.TryDiscountAmount(subtotal, draft.DiscountKind, draft.DiscountRate, draft.DiscountAmount, out _))
        {
            return await AbortAsync(
                tx,
                OperationFailure.Validation([new FieldError("discountAmount", "discount.mismatch")]),
                cancellationToken);
        }

        if (!SaleRules.TryAllocate(staged.Select(line => line.LineTotal).ToArray(), draft.DiscountAmount, out var allocations))
        {
            return await AbortAsync(
                tx,
                OperationFailure.Validation([new FieldError("discountAmount", "discount.allocation")]),
                cancellationToken);
        }

        var overrides = new bool[staged.Count];
        for (var index = 0; index < staged.Count; index++)
        {
            var line = staged[index];
            var net = line.LineTotal - allocations[index];
            if (net < 0m)
            {
                return await AbortAsync(
                    tx,
                    OperationFailure.Validation([new FieldError("discountAmount", "discount.negative")]),
                    cancellationToken);
            }

            var floorUnit = line.Product.MinimumUnitPrice ?? line.ListUnitPrice;
            var belowFloor = SaleRules.NetBelowFloor(net, line.Quantity, floorUnit);
            if (!administrator && belowFloor)
            {
                return await AbortAsync(
                    tx,
                    OperationFailure.Validation([new FieldError("discount", "discount.below_minimum")]),
                    cancellationToken);
            }

            overrides[index] = administrator && belowFloor;
        }

        CreditOverrideSnapshot? creditSnapshot = null;
        if (customer is not null)
        {
            var outstanding = await CustomerAccounts.ReadOutstandingAsync(
                database,
                company.Id,
                customer.Id,
                cancellationToken);
            var initialPayment = draft.Payments.Sum(payment => payment.Amount);
            var decision = CustomerCredit.Decide(
                customer.CreditLimit,
                outstanding,
                draft.Total,
                initialPayment,
                request.CreditOverride,
                creditReason);
            switch (decision.Outcome)
            {
                case CreditOutcome.Integrity:
                    return await AbortAsync(tx, OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity), cancellationToken);
                case CreditOutcome.NotApplicable:
                    return await AbortAsync(
                        tx,
                        OperationFailure.Validation([new FieldError("creditOverride", CustomerCredit.NotApplicable)]),
                        cancellationToken);
                case CreditOutcome.ReasonRequired:
                    return await AbortAsync(
                        tx,
                        OperationFailure.Validation([new FieldError("creditOverrideReason", CustomerCredit.ReasonRequired)]),
                        cancellationToken);
                case CreditOutcome.Exceeded:
                    return await AbortAsync(
                        tx,
                        CreditRejected(customer.CreditLimit!.Value, outstanding, decision.ProjectedOutstanding, draft.Total, initialPayment),
                        cancellationToken);
                case CreditOutcome.Override:
                    creditSnapshot = decision.Snapshot;
                    break;
            }
        }

        await productLock.AfterProductLockAsync(cancellationToken);

        var location = await database.StockLocations.SingleOrDefaultAsync(
            item => item.CompanyId == company.Id && item.IsDefault,
            cancellationToken);
        foreach (var line in staged.Where(item => item.Product.TrackStock).OrderBy(item => item.Product.Id))
        {
            if (location is null)
            {
                return await AbortAsync(tx, Insufficient(0m, line.Quantity), cancellationToken);
            }

            await LockStockAsync(location.Id, line.Product.Id, cancellationToken);
            var balance = await database.StockBalances.SingleOrDefaultAsync(
                item => item.StockLocationId == location.Id && item.ProductId == line.Product.Id,
                cancellationToken);
            var available = balance?.Quantity ?? 0m;
            if (location.PreventNegativeStock && available < line.Quantity)
            {
                return await AbortAsync(tx, Insufficient(available, line.Quantity), cancellationToken);
            }
        }

        var saleId = Guid.CreateVersion7();
        var createdAt = time.GetUtcNow();
        var number = await NextNumberAsync("pexilio.sale_number_seq", SaleNumbers.Format, cancellationToken);
        var lines = new List<SaleLine>(staged.Count);
        for (var index = 0; index < staged.Count; index++)
        {
            var line = staged[index];
            lines.Add(new SaleLine
            {
                Id = Guid.CreateVersion7(),
                SaleId = saleId,
                CompanyId = company.Id,
                ProductId = line.Product.Id,
                LineNumber = line.LineNumber,
                Quantity = line.Quantity,
                ListUnitPrice = line.ListUnitPrice,
                UnitPrice = line.UnitPrice,
                PriceKind = line.PriceKind,
                LineDiscount = line.LineDiscount,
                DiscountKind = line.DiscountKind,
                DiscountRate = line.DiscountRate,
                AllocatedSaleDiscount = allocations[index],
                BelowMinimumOverride = overrides[index],
                LineTotal = line.LineTotal,
                ProductSku = line.Product.Sku,
                ProductName = line.Product.Name,
                ProductUnit = line.Product.Unit,
                TracksStock = line.Product.TrackStock,
                TaxRateId = line.Tax.Id,
                TaxRate = line.Tax.Rate,
                StockMovementId = line.Product.TrackStock ? Guid.CreateVersion7() : null,
            });
        }

        var receiptNumber = draft.Payments.Count == 0
            ? null
            : await AllocateReceiptAsync(company.Id, cancellationToken);
        var sale = new Sale
        {
            Id = saleId,
            CompanyId = company.Id,
            Number = number,
            Status = SaleStatuses.Posted,
            Subtotal = subtotal,
            DiscountAmount = draft.DiscountAmount,
            DiscountKind = draft.DiscountKind,
            DiscountRate = draft.DiscountRate,
            Total = draft.Total,
            CreatedAtUtc = createdAt,
            CreatedByUserId = userId,
            CustomerId = customer?.Id,
            CustomerName = customer?.Name,
            CustomerType = customer?.Type,
            CreditLimitOverridden = creditSnapshot is not null,
            CreditOverrideReason = creditSnapshot?.Reason,
            CreditOverrideByUserId = creditSnapshot is null ? null : userId,
            CreditOverrideAtUtc = creditSnapshot is null ? null : createdAt,
            CreditLimitSnapshot = creditSnapshot?.Limit,
            CreditOutstandingBefore = creditSnapshot?.OutstandingBefore,
            CreditProjectedOutstanding = creditSnapshot?.ProjectedOutstanding,
            Lines = lines,
            Payments = draft.Payments.Select(payment => new SalePayment
            {
                Id = Guid.CreateVersion7(),
                SaleId = saleId,
                CompanyId = company.Id,
                Method = payment.Method,
                Amount = payment.Amount,
                ReceiptNumber = receiptNumber,
                PaidAtUtc = createdAt,
                ReceivedByUserId = userId,
            }).ToList(),
        };
        database.Sales.Add(sale);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or PostgresException)
        {
            return await FailWriteAsync(tx, exception, cancellationToken);
        }

        var reason = SaleReason(number);
        foreach (var line in lines.Where(item => item.TracksStock).OrderBy(item => item.ProductId))
        {
            Result<StockMovement> posted;
            try
            {
                posted = await stock.PostInCurrentTransactionAsync(
                    StockMovementTypes.Sale,
                    line.ProductId,
                    SaleRules.SaleDelta(line.Quantity),
                    reason,
                    null,
                    userId,
                    StockSourceTypes.Sale,
                    line.Id,
                    cancellationToken,
                    line.StockMovementId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailWriteAsync(tx, exception, cancellationToken);
            }

            if (posted.Error is not null || posted.Value is null)
            {
                return await AbortAsync(tx, posted.Error ?? OperationFailure.Of(409, ErrorCodes.StockReconciliation), cancellationToken);
            }
        }

        var response = ToResponse(sale, 0m);
        claim.SaleId = sale.Id;
        claim.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result<SaleResponse>.Success(response);
        }
        catch (Exception exception) when (exception is DbUpdateException or PostgresException)
        {
            return await FailWriteAsync(tx, exception, cancellationToken);
        }
    }

    private async Task<Result<SaleResponse>> ReplayAsync(
        Guid companyId,
        Guid idempotencyKey,
        string hash,
        CancellationToken cancellationToken)
    {
        var claim = await database.SaleIdempotencies.AsNoTracking().SingleOrDefaultAsync(
            item => item.CompanyId == companyId
                && item.Operation == SaleOperations.SaleFinalize
                && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (claim is null || claim.RequestHash != hash || claim.SaleId is not Guid saleId)
        {
            return Result<SaleResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SaleIdempotencyConflict));
        }

        var sale = await database.Sales.AsNoTracking()
            .Include(item => item.Lines)
            .Include(item => item.Payments)
            .SingleOrDefaultAsync(item => item.Id == saleId && item.CompanyId == companyId, cancellationToken);
        if (sale is null)
        {
            return Result<SaleResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SaleIdempotencyConflict));
        }

        var returned = await ReturnedAmountAsync(companyId, sale.Id, cancellationToken);
        var refunded = await RefundedAmountAsync(companyId, sale.Id, cancellationToken);
        var returnedByLine = await ReturnedByLineAsync(companyId, sale.Id, cancellationToken);
        return Result<SaleResponse>.Success(ToResponse(sale, returned, refunded, returnedByLine));
    }

    private async Task<ClaimSave> SaveClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return ClaimSave.Stored;
        }
        catch (DbUpdateException exception) when (IsIdempotencyConflict(exception))
        {
            return ClaimSave.Conflict;
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Cash sale finalization failed.");
            return ClaimSave.Failed;
        }
    }

    public async Task<Result<SalePaymentPostResponse>> AddPaymentAsync(
        Guid saleId,
        SalePaymentPostRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!CustomerPaymentValidator.TryNormalize(
                request.Method,
                request.Amount,
                request.Reference,
                request.Notes,
                out var draft,
                out var errors))
        {
            return Result<SalePaymentPostResponse>.Fail(OperationFailure.Validation(errors));
        }

        var hash = Fingerprint(SaleOperations.SalePayment, PaymentCanonical(saleId, draft));
        await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
        var company = await database.Companies.SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(404, ErrorCodes.CompanyMissing), cancellationToken);
        }

        var claim = new SaleIdempotency
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id,
            Operation = SaleOperations.SalePayment,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            ResponseJson = "",
            CreatedAtUtc = time.GetUtcNow(),
        };
        database.SaleIdempotencies.Add(claim);
        var claimed = await SaveClaimAsync(cancellationToken);
        if (claimed == ClaimSave.Conflict)
        {
            database.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            return await ReplayPaymentAsync(company.Id, idempotencyKey, hash, cancellationToken);
        }

        if (claimed == ClaimSave.Failed)
        {
            return await AbortPaymentAsync(tx, UnexpectedFailure(), cancellationToken);
        }

        var saleRef = await database.Sales.AsNoTracking()
            .Where(item => item.Id == saleId && item.CompanyId == company.Id)
            .Select(item => new { item.Id, item.CustomerId, item.Number, item.Total })
            .SingleOrDefaultAsync(cancellationToken);
        if (saleRef is null)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(404, ErrorCodes.SaleNotFound), cancellationToken);
        }

        if (saleRef.CustomerId is not Guid customerId)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(409, SaleCollection.CustomerRequired), cancellationToken);
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM pexilio.customer WHERE "CompanyId" = {company.Id} AND "Id" = {customerId} FOR UPDATE""",
            cancellationToken);
        var customerExists = await database.Customers.AsNoTracking()
            .AnyAsync(item => item.Id == customerId && item.CompanyId == company.Id, cancellationToken);
        if (!customerExists)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(404, ErrorCodes.CustomerNotFound), cancellationToken);
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM pexilio.sale WHERE "CompanyId" = {company.Id} AND "Id" = {saleId} FOR UPDATE""",
            cancellationToken);
        var locked = await database.Sales.AsNoTracking()
            .Where(item => item.Id == saleId && item.CompanyId == company.Id)
            .Select(item => new { item.Total })
            .SingleOrDefaultAsync(cancellationToken);
        if (locked is null)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(404, ErrorCodes.SaleNotFound), cancellationToken);
        }

        var paid = await database.SalePayments.AsNoTracking()
            .Where(item => item.SaleId == saleId && item.CompanyId == company.Id)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var returned = await ReturnedAmountAsync(company.Id, saleId, cancellationToken);
        var refunded = await RefundedAmountAsync(company.Id, saleId, cancellationToken);
        if (!SaleBalances.TryDerive(locked.Total, returned, paid, refunded, out var current))
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity), cancellationToken);
        }

        var problem = SaleCollection.Problem(customerLinked: true, current.Outstanding, draft.Amount);
        if (problem is not null)
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(409, problem), cancellationToken);
        }

        var receiptNumber = await AllocateReceiptAsync(company.Id, cancellationToken);
        var paidAt = time.GetUtcNow();
        var payment = new SalePayment
        {
            Id = Guid.CreateVersion7(),
            SaleId = saleId,
            CompanyId = company.Id,
            Method = draft.Method,
            Amount = draft.Amount,
            ReceiptNumber = receiptNumber,
            Reference = draft.Reference,
            Notes = draft.Notes,
            PaidAtUtc = paidAt,
            ReceivedByUserId = userId,
        };
        database.SalePayments.Add(payment);
        if (!SaleBalances.TryDerive(locked.Total, returned, paid + draft.Amount, refunded, out var updated))
        {
            return await AbortPaymentAsync(tx, OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity), cancellationToken);
        }

        var response = new SalePaymentPostResponse(
            saleId,
            saleRef.Number,
            QuantityText.Format(updated.Paid),
            QuantityText.Format(refunded),
            QuantityText.Format(updated.Outstanding),
            updated.State,
            new SalePaymentResponse(
                payment.Id,
                payment.Method,
                QuantityText.Format(payment.Amount),
                payment.PaidAtUtc,
                payment.ReceiptNumber,
                payment.Reference,
                payment.Notes,
                payment.ReceivedByUserId));
        claim.SaleId = saleId;
        claim.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result<SalePaymentPostResponse>.Success(response);
        }
        catch (Exception exception) when (exception is DbUpdateException or PostgresException)
        {
            var mapped = MapWrite(exception);
            if (mapped is null)
            {
                logger.LogError(exception, "Customer payment failed.");
                mapped = UnexpectedFailure();
            }

            return await AbortPaymentAsync(tx, mapped, cancellationToken);
        }
    }

    public async Task<Result<SaleReturnPostResponse>> PostReturnAsync(
        Guid saleId,
        SaleReturnPostRequest request,
        Guid idempotencyKey,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var requestedLines = request.Lines?.Select(line => (line.SaleLineId, line.Quantity)).ToArray();
        var requestedRefunds = request.Refunds?
            .Select(refund => (refund.Method, refund.Amount, refund.Reference, refund.Notes))
            .ToArray();
        if (!SaleReturnPosting.TryNormalize(
                request.Reason,
                requestedLines,
                requestedRefunds,
                out var draft,
                out var requestErrors))
        {
            return Result<SaleReturnPostResponse>.Fail(OperationFailure.Validation(requestErrors));
        }

        var hash = Fingerprint(SaleOperations.SaleReturn, ReturnCanonical(saleId, draft));
        await using var tx = await database.Database.BeginTransactionAsync(cancellationToken);
        var company = await database.Companies.SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return await AbortReturnAsync(tx, OperationFailure.Of(404, ErrorCodes.CompanyMissing), cancellationToken);
        }

        var claim = new SaleIdempotency
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id,
            Operation = SaleOperations.SaleReturn,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            ResponseJson = "",
            CreatedAtUtc = time.GetUtcNow(),
        };
        database.SaleIdempotencies.Add(claim);
        var claimed = await SaveClaimAsync(cancellationToken);
        if (claimed == ClaimSave.Conflict)
        {
            database.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            return await ReplayReturnAsync(company.Id, idempotencyKey, hash, cancellationToken);
        }

        if (claimed == ClaimSave.Failed)
        {
            return await AbortReturnAsync(tx, UnexpectedFailure(), cancellationToken);
        }

        var saleRef = await database.Sales.AsNoTracking()
            .Where(item => item.Id == saleId && item.CompanyId == company.Id)
            .Select(item => new { item.Id, item.CustomerId, item.Total })
            .SingleOrDefaultAsync(cancellationToken);
        if (saleRef is null)
        {
            return await AbortReturnAsync(tx, OperationFailure.Of(404, ErrorCodes.SaleNotFound), cancellationToken);
        }

        if (saleRef.CustomerId is Guid customerId)
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM pexilio.customer WHERE "CompanyId" = {company.Id} AND "Id" = {customerId} FOR UPDATE""",
                cancellationToken);
            var customerExists = await database.Customers.AsNoTracking()
                .AnyAsync(item => item.Id == customerId && item.CompanyId == company.Id, cancellationToken);
            if (!customerExists)
            {
                return await AbortReturnAsync(tx, OperationFailure.Of(404, ErrorCodes.CustomerNotFound), cancellationToken);
            }
        }

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM pexilio.sale WHERE "CompanyId" = {company.Id} AND "Id" = {saleId} FOR UPDATE""",
            cancellationToken);
        var locked = await database.Sales.AsNoTracking()
            .Where(item => item.Id == saleId && item.CompanyId == company.Id)
            .Select(item => new { item.Total })
            .SingleOrDefaultAsync(cancellationToken);
        if (locked is null)
        {
            return await AbortReturnAsync(tx, OperationFailure.Of(404, ErrorCodes.SaleNotFound), cancellationToken);
        }

        var saleLines = await database.SaleLines.AsNoTracking()
            .Where(line => line.SaleId == saleId && line.CompanyId == company.Id)
            .ToListAsync(cancellationToken);
        var productIds = saleLines
            .Where(line => draft.Lines.Any(requested => requested.SaleLineId == line.Id))
            .Select(line => line.ProductId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        foreach (var productId in productIds)
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM pexilio.product WHERE "CompanyId" = {company.Id} AND "Id" = {productId} FOR UPDATE""",
                cancellationToken);
        }

        await productLock.AfterProductLockAsync(cancellationToken);

        var previous = await database.SaleReturnLines.AsNoTracking()
            .Where(line => line.CompanyId == company.Id && line.SaleId == saleId)
            .GroupBy(line => line.SaleLineId)
            .Select(group => new
            {
                SaleLineId = group.Key,
                Quantity = group.Sum(line => line.Quantity),
                Amount = group.Sum(line => line.Amount),
            })
            .ToListAsync(cancellationToken);
        var returned = await ReturnedAmountAsync(company.Id, saleId, cancellationToken);
        var paid = await database.SalePayments.AsNoTracking()
            .Where(payment => payment.SaleId == saleId && payment.CompanyId == company.Id)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        var refunded = await RefundedAmountAsync(company.Id, saleId, cancellationToken);
        if (!SaleBalances.TryDerive(locked.Total, returned, paid, refunded, out var current))
        {
            return await AbortReturnAsync(tx, OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity), cancellationToken);
        }

        var lineErrors = new List<FieldError>();
        var inputs = new List<ReturnLineInput>(draft.Lines.Count);
        var seen = new HashSet<Guid>();
        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var requested = draft.Lines[index];
            var prefix = $"lines[{index}]";
            if (!seen.Add(requested.SaleLineId))
            {
                lineErrors.Add(new FieldError($"{prefix}.saleLineId", "lines.duplicate"));
                continue;
            }

            var saleLine = saleLines.SingleOrDefault(line => line.Id == requested.SaleLineId);
            if (saleLine is null)
            {
                lineErrors.Add(new FieldError($"{prefix}.saleLineId", "saleLine.unknown"));
                continue;
            }

            var prior = previous.SingleOrDefault(item => item.SaleLineId == saleLine.Id);
            inputs.Add(new ReturnLineInput(
                saleLine.Id,
                saleLine.Quantity,
                saleLine.LineTotal,
                saleLine.AllocatedSaleDiscount,
                prior?.Quantity ?? 0m,
                prior?.Amount ?? 0m,
                requested.Quantity));
        }

        IReadOnlyList<ReturnLineValuation> valuations = [];
        var returnAmount = 0m;
        if (lineErrors.Count == 0
            && !SaleReturnRules.TryValueLines(inputs, out valuations, out returnAmount, out var valueErrors))
        {
            lineErrors.AddRange(valueErrors);
        }

        if (lineErrors.Count > 0)
        {
            return await AbortReturnAsync(tx, OperationFailure.Validation(lineErrors), cancellationToken);
        }

        var unrefundedCollected = paid - refunded;
        if (!SaleRefundRules.TryQuote(returnAmount, current.Outstanding, unrefundedCollected, out var quote, out var quoteErrors))
        {
            return await AbortReturnAsync(tx, OperationFailure.Validation(quoteErrors), cancellationToken);
        }

        var tenderInputs = draft.Refunds.Select(tender => (tender.Method, tender.Amount)).ToArray();
        if (!SaleRefundRules.TryTenders(quote.RequiredRefund, tenderInputs, out var tenders, out var tenderErrors))
        {
            return await AbortReturnAsync(tx, OperationFailure.Validation(tenderErrors), cancellationToken);
        }

        if (!SaleBalances.TryDerive(
                locked.Total,
                returned + returnAmount,
                paid,
                refunded + quote.RequiredRefund,
                out var updated))
        {
            return await AbortReturnAsync(tx, OperationFailure.Of(409, ErrorCodes.SalePaymentIntegrity), cancellationToken);
        }

        var returnedAt = time.GetUtcNow();
        var returnId = Guid.CreateVersion7();
        var number = await AllocateSeriesAsync(
            company.Id,
            SaleNumbers.ReturnSeries,
            SaleNumbers.FormatReturn,
            cancellationToken);
        var postedLines = new List<SaleReturnLine>(valuations.Count);
        foreach (var valuation in valuations)
        {
            var saleLine = saleLines.Single(line => line.Id == valuation.SaleLineId);
            postedLines.Add(new SaleReturnLine
            {
                Id = Guid.CreateVersion7(),
                ReturnId = returnId,
                SaleId = saleId,
                SaleLineId = saleLine.Id,
                CompanyId = company.Id,
                ProductId = saleLine.ProductId,
                Quantity = valuation.Quantity,
                Amount = valuation.Amount,
                TracksStock = saleLine.TracksStock,
                StockMovementId = saleLine.TracksStock ? Guid.CreateVersion7() : null,
            });
        }

        var postedRefunds = new List<SaleRefund>(tenders.Count);
        for (var index = 0; index < tenders.Count; index++)
        {
            var tender = tenders[index];
            var source = draft.Refunds[index];
            postedRefunds.Add(new SaleRefund
            {
                Id = Guid.CreateVersion7(),
                CompanyId = company.Id,
                SaleId = saleId,
                SaleReturnId = returnId,
                Amount = tender.Amount,
                Method = tender.Method,
                Reference = source.Reference,
                Notes = source.Notes,
                RefundedAtUtc = returnedAt,
                RefundedByUserId = userId,
            });
        }

        var saleReturn = new SaleReturn
        {
            Id = returnId,
            CompanyId = company.Id,
            SaleId = saleId,
            Number = number,
            Reason = draft.Reason,
            ReturnedAtUtc = returnedAt,
            ReturnedByUserId = userId,
            Lines = postedLines,
            Refunds = postedRefunds,
        };
        database.SaleReturns.Add(saleReturn);
        var stockReason = ReturnReason(number);
        foreach (var line in postedLines.Where(line => line.TracksStock).OrderBy(line => line.ProductId))
        {
            Result<StockMovement> posted;
            try
            {
                posted = await stock.PostInCurrentTransactionAsync(
                    StockMovementTypes.SaleReturn,
                    line.ProductId,
                    line.Quantity,
                    stockReason,
                    null,
                    userId,
                    StockSourceTypes.SaleReturn,
                    line.Id,
                    cancellationToken,
                    line.StockMovementId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await FailReturnAsync(tx, exception, cancellationToken);
            }

            if (posted.Error is not null || posted.Value is null)
            {
                return await AbortReturnAsync(
                    tx,
                    posted.Error ?? OperationFailure.Of(409, ErrorCodes.StockReconciliation),
                    cancellationToken);
            }
        }

        var response = ToReturnResponse(saleReturn, quote, updated, refunded + quote.RequiredRefund);
        claim.SaleId = saleId;
        claim.ResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result<SaleReturnPostResponse>.Success(response);
        }
        catch (Exception exception) when (exception is DbUpdateException or PostgresException)
        {
            return await FailReturnAsync(tx, exception, cancellationToken);
        }
    }

    private async Task<Result<SaleReturnPostResponse>> ReplayReturnAsync(
        Guid companyId,
        Guid idempotencyKey,
        string hash,
        CancellationToken cancellationToken)
    {
        var claim = await database.SaleIdempotencies.AsNoTracking().SingleOrDefaultAsync(
            item => item.CompanyId == companyId
                && item.Operation == SaleOperations.SaleReturn
                && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (claim is null || claim.RequestHash != hash || string.IsNullOrEmpty(claim.ResponseJson))
        {
            return Result<SaleReturnPostResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SaleIdempotencyConflict));
        }

        var stored = JsonSerializer.Deserialize<SaleReturnPostResponse>(claim.ResponseJson, JsonOptions);
        return stored is null
            ? Result<SaleReturnPostResponse>.Fail(OperationFailure.Of(409, ErrorCodes.SaleIdempotencyConflict))
            : Result<SaleReturnPostResponse>.Success(stored);
    }

    private async Task<Result<SaleReturnPostResponse>> AbortReturnAsync(
        IDbContextTransaction tx,
        OperationFailure error,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        if (database.Database.CurrentTransaction is not null)
        {
            await tx.RollbackAsync(cancellationToken);
        }

        return Result<SaleReturnPostResponse>.Fail(error);
    }

    private async Task<Result<SaleReturnPostResponse>> FailReturnAsync(
        IDbContextTransaction tx,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var mapped = MapWrite(exception);
        if (mapped is null)
        {
            logger.LogError(exception, "Sale return failed.");
            mapped = UnexpectedFailure();
        }

        return await AbortReturnAsync(tx, mapped, cancellationToken);
    }

    private static string ReturnCanonical(Guid saleId, SaleReturnDraft draft)
    {
        var parts = new List<string> { saleId.ToString("D"), draft.Reason };
        foreach (var line in draft.Lines)
        {
            parts.Add(line.SaleLineId.ToString("D"));
            parts.Add(QuantityText.Format(line.Quantity));
        }

        foreach (var refund in draft.Refunds)
        {
            parts.Add(refund.Method ?? "");
            parts.Add(QuantityText.Format(refund.Amount));
            parts.Add(refund.Reference ?? "");
            parts.Add(refund.Notes ?? "");
        }

        return string.Join('\u001f', parts);
    }

    private static string ReturnReason(string number)
    {
        var reason = "Retour " + number;
        return reason.Length <= StockRules.ReasonMaxLength ? reason : reason[..StockRules.ReasonMaxLength];
    }

    private static SaleReturnPostResponse ToReturnResponse(
        SaleReturn saleReturn,
        SaleRefundQuote quote,
        SaleBalance balance,
        decimal refunded)
    {
        return new SaleReturnPostResponse(
            saleReturn.Id,
            saleReturn.SaleId,
            saleReturn.Number,
            saleReturn.Reason,
            saleReturn.ReturnedAtUtc,
            QuantityText.Format(quote.ReturnAmount),
            QuantityText.Format(quote.RequiredRefund),
            QuantityText.Format(quote.NetAccountReduction),
            QuantityText.Format(balance.Paid),
            QuantityText.Format(refunded),
            QuantityText.Format(balance.Outstanding),
            balance.State,
            saleReturn.Lines
                .Select(line => new SaleReturnLineResponse(
                    line.Id,
                    line.SaleLineId,
                    line.ProductId,
                    QuantityText.Format(line.Quantity),
                    QuantityText.Format(line.Amount),
                    line.TracksStock,
                    line.StockMovementId))
                .ToList(),
            saleReturn.Refunds
                .Select(refund => new SaleRefundResponse(
                    refund.Id,
                    refund.Method,
                    QuantityText.Format(refund.Amount),
                    refund.Reference,
                    refund.Notes,
                    refund.RefundedAtUtc,
                    refund.RefundedByUserId))
                .ToList());
    }

    private async Task<Result<SalePaymentPostResponse>> ReplayPaymentAsync(
        Guid companyId,
        Guid idempotencyKey,
        string hash,
        CancellationToken cancellationToken)
    {
        var claim = await database.SaleIdempotencies.AsNoTracking().SingleOrDefaultAsync(
            item => item.CompanyId == companyId
                && item.Operation == SaleOperations.SalePayment
                && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (claim is null || claim.RequestHash != hash || string.IsNullOrEmpty(claim.ResponseJson))
        {
            return Result<SalePaymentPostResponse>.Fail(OperationFailure.Of(409, ErrorCodes.PaymentIdempotencyConflict));
        }

        var stored = JsonSerializer.Deserialize<SalePaymentPostResponse>(claim.ResponseJson, JsonOptions);
        return stored is null
            ? Result<SalePaymentPostResponse>.Fail(OperationFailure.Of(409, ErrorCodes.PaymentIdempotencyConflict))
            : Result<SalePaymentPostResponse>.Success(stored);
    }

    private async Task<Result<SalePaymentPostResponse>> AbortPaymentAsync(
        IDbContextTransaction tx,
        OperationFailure error,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        if (database.Database.CurrentTransaction is not null)
        {
            await tx.RollbackAsync(cancellationToken);
        }

        return Result<SalePaymentPostResponse>.Fail(error);
    }

    private static string PaymentCanonical(Guid saleId, CustomerPaymentDraft draft)
    {
        return string.Join(
            '\u001f',
            saleId.ToString("D"),
            draft.Method,
            QuantityText.Format(draft.Amount),
            draft.Reference ?? "",
            draft.Notes ?? "");
    }

    private Task<string> AllocateReceiptAsync(Guid companyId, CancellationToken cancellationToken) =>
        AllocateSeriesAsync(companyId, PaymentReceiptNumbers.Series, PaymentReceiptNumbers.Format, cancellationToken);

    private async Task<string> AllocateSeriesAsync(
        Guid companyId,
        string series,
        Func<long, string> format,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO pexilio.document_counter ("CompanyId", "Series", "LastValue")
            VALUES (@company, @series, 1)
            ON CONFLICT ("CompanyId", "Series")
            DO UPDATE SET "LastValue" = pexilio.document_counter."LastValue" + 1
            RETURNING "LastValue"
            """;
        var companyParameter = command.CreateParameter();
        companyParameter.ParameterName = "company";
        companyParameter.Value = companyId;
        command.Parameters.Add(companyParameter);
        var seriesParameter = command.CreateParameter();
        seriesParameter.ParameterName = "series";
        seriesParameter.Value = series;
        command.Parameters.Add(seriesParameter);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return format(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private async Task<string> NextNumberAsync(
        string sequence,
        Func<long, string> format,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT nextval('" + sequence + "')";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return format(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private Task LockStockAsync(Guid locationId, Guid productId, CancellationToken cancellationToken)
    {
        var locationKey = locationId.ToString("D");
        var productKey = productId.ToString("D");
        return database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({locationKey}), hashtext({productKey}))",
            cancellationToken);
    }

    private async Task<Result<SaleResponse>> AbortAsync(
        IDbContextTransaction tx,
        OperationFailure error,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        if (database.Database.CurrentTransaction is not null)
        {
            await tx.RollbackAsync(cancellationToken);
        }

        return Result<SaleResponse>.Fail(error);
    }

    private async Task<decimal> ReturnedAmountAsync(Guid companyId, Guid saleId, CancellationToken cancellationToken) =>
        await database.SaleReturnLines.AsNoTracking()
            .Where(line => line.CompanyId == companyId && line.SaleId == saleId)
            .SumAsync(line => (decimal?)line.Amount, cancellationToken) ?? 0m;

    private async Task<IReadOnlyDictionary<Guid, SaleLineReturnTotals>> ReturnedByLineAsync(
        Guid companyId,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        var rows = await database.SaleReturnLines.AsNoTracking()
            .Where(line => line.CompanyId == companyId && line.SaleId == saleId)
            .GroupBy(line => line.SaleLineId)
            .Select(group => new
            {
                SaleLineId = group.Key,
                Quantity = group.Sum(line => line.Quantity),
                Amount = group.Sum(line => line.Amount),
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.SaleLineId,
            row => new SaleLineReturnTotals(row.Quantity, row.Amount));
    }

    private async Task<decimal> RefundedAmountAsync(Guid companyId, Guid saleId, CancellationToken cancellationToken) =>
        await database.SaleRefunds.AsNoTracking()
            .Where(refund => refund.CompanyId == companyId && refund.SaleId == saleId)
            .SumAsync(refund => (decimal?)refund.Amount, cancellationToken) ?? 0m;

    private static SaleBalance Balance(decimal total, decimal returned, decimal paid, decimal refunded = 0m)
    {
        if (!SaleBalances.TryDerive(total, returned, paid, refunded, out var balance))
        {
            throw new InvalidOperationException("The sale balance is inconsistent.");
        }

        return balance;
    }

    private static SaleResponse ToResponse(
        Sale sale,
        decimal returned,
        decimal refunded = 0m,
        IReadOnlyDictionary<Guid, SaleLineReturnTotals>? returnedByLine = null)
    {
        var byLine = returnedByLine ?? new Dictionary<Guid, SaleLineReturnTotals>();
        var balance = Balance(sale.Total, returned, sale.Payments.Sum(payment => payment.Amount), refunded);
        return new SaleResponse(
            sale.Id,
            sale.Number,
            sale.Status,
            QuantityText.Format(sale.Subtotal),
            QuantityText.Format(sale.DiscountAmount),
            sale.DiscountKind,
            sale.DiscountRate is decimal saleRate ? UnitPriceText.Format(saleRate) : null,
            QuantityText.Format(sale.Total),
            sale.CreatedAtUtc,
            sale.CreatedByUserId,
            sale.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line =>
                {
                    byLine.TryGetValue(line.Id, out var returnedLine);
                    var history = SaleLineReturnHistoryRules.Summarize(
                        line.Quantity,
                        line.LineTotal,
                        line.AllocatedSaleDiscount,
                        returnedLine.Quantity,
                        returnedLine.Amount);
                    return new SaleLineResponse(
                        line.Id,
                        line.LineNumber,
                        line.ProductId,
                        line.ProductSku,
                        line.ProductName,
                        line.ProductUnit,
                        QuantityText.Format(line.Quantity),
                        UnitPriceText.Format(line.ListUnitPrice),
                        UnitPriceText.Format(line.UnitPrice),
                        QuantityText.Format(line.LineTotal),
                        line.TracksStock,
                        line.StockMovementId,
                        line.TaxRate.ToString("0.000000", CultureInfo.InvariantCulture),
                        line.PriceKind,
                        QuantityText.Format(line.LineDiscount),
                        QuantityText.Format(line.AllocatedSaleDiscount),
                        line.DiscountKind,
                        line.DiscountRate is decimal lineRate ? UnitPriceText.Format(lineRate) : null,
                        line.BelowMinimumOverride,
                        QuantityText.Format(history.ReturnedQuantity),
                        QuantityText.Format(history.RemainingReturnableQuantity),
                        QuantityText.Format(history.ReturnedAmount),
                        QuantityText.Format(history.RemainingReturnableAmount));
                })
                .ToList(),
            sale.Payments
                .OrderBy(payment => payment.PaidAtUtc)
                .ThenBy(payment => payment.Id)
                .Select(payment => new SalePaymentResponse(
                    payment.Id,
                    payment.Method,
                    QuantityText.Format(payment.Amount),
                    payment.PaidAtUtc,
                    payment.ReceiptNumber,
                    payment.Reference,
                    payment.Notes,
                    payment.ReceivedByUserId))
                .ToList(),
            sale.CustomerId,
            sale.CustomerName,
            sale.CustomerType,
            QuantityText.Format(balance.Paid),
            QuantityText.Format(refunded),
            QuantityText.Format(balance.Outstanding),
            balance.State,
            sale.CreditLimitOverridden,
            sale.CreditOverrideReason,
            FormatLimit(sale.CreditLimitSnapshot),
            FormatAmount(sale.CreditOutstandingBefore),
            FormatAmount(sale.CreditProjectedOutstanding));
    }

    private static string? FormatLimit(decimal? amount) =>
        amount is decimal value ? UnitPriceText.Format(value) : null;

    private static string? FormatAmount(decimal? amount) =>
        amount is decimal value ? QuantityText.Format(value) : null;

    private static OperationFailure CreditRejected(
        decimal limit,
        decimal outstanding,
        decimal projected,
        decimal saleTotal,
        decimal initialPayment) =>
        OperationFailure.Of(
            409,
            ErrorCodes.CreditLimitExceeded,
            new Dictionary<string, string>
            {
                ["creditLimit"] = UnitPriceText.Format(limit),
                ["outstanding"] = QuantityText.Format(outstanding),
                ["projected"] = QuantityText.Format(projected),
                ["saleTotal"] = QuantityText.Format(saleTotal),
                ["initialPayment"] = QuantityText.Format(initialPayment),
            });

    private static string Canonical(SaleDraft draft, Guid? customerId, bool creditOverride, string? creditReason)
    {
        var parts = new List<string> { QuantityText.Format(draft.Total) };
        if (draft.Extended)
        {
            parts.Add(draft.DiscountKind);
            parts.Add(draft.DiscountRate is decimal rate ? UnitPriceText.Format(rate) : "");
            parts.Add(QuantityText.Format(draft.DiscountAmount));
        }

        foreach (var line in draft.Lines)
        {
            parts.Add(line.ProductId.ToString("D"));
            parts.Add(QuantityText.Format(line.Quantity));
            if (draft.Extended)
            {
                parts.Add(line.PriceKind);
            }

            parts.Add(UnitPriceText.Format(line.UnitPrice));
            if (draft.Extended)
            {
                parts.Add(line.DiscountKind);
                parts.Add(line.DiscountRate is decimal lineRate ? UnitPriceText.Format(lineRate) : "");
                parts.Add(QuantityText.Format(line.LineDiscount));
            }

            parts.Add(QuantityText.Format(line.LineTotal));
        }

        // An empty payment list adds nothing, so an unpaid customer sale does not disturb a
        // stored full-cash hash. A present tender still appends method and amount, and the
        // customer id is still appended only when the request has one.
        foreach (var payment in draft.Payments)
        {
            parts.Add(payment.Method);
            parts.Add(QuantityText.Format(payment.Amount));
        }

        if (customerId is Guid linked)
        {
            parts.Add(linked.ToString("D"));
        }

        // Default requests keep the historical fingerprint. An override flag or a reason is the
        // only addition, so a retry with a different decision cannot replay the original sale.
        if (creditOverride || creditReason is not null)
        {
            parts.Add(creditOverride ? "1" : "0");
            parts.Add(creditReason ?? "");
        }

        return string.Join('\u001f', parts);
    }

    private static string Fingerprint(string operation, string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(operation + '\u001f' + canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string SaleReason(string number)
    {
        var reason = "Vente " + number;
        return reason.Length <= StockRules.ReasonMaxLength ? reason : reason[..StockRules.ReasonMaxLength];
    }

    private static OperationFailure Insufficient(decimal available, decimal requested) =>
        OperationFailure.Of(
            409,
            ErrorCodes.StockInsufficient,
            new Dictionary<string, string>
            {
                ["available"] = QuantityText.Format(available),
                ["requested"] = QuantityText.Format(requested),
            });

    private async Task<Result<SaleResponse>> FailWriteAsync(
        IDbContextTransaction tx,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var mapped = MapWrite(exception);
        if (mapped is null)
        {
            logger.LogError(exception, "Cash sale finalization failed.");
            mapped = UnexpectedFailure();
        }

        return await AbortAsync(tx, mapped, cancellationToken);
    }

    private static OperationFailure UnexpectedFailure() =>
        OperationFailure.Of(500, "request.failed");

    private static OperationFailure? MapWrite(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not PostgresException postgres)
            {
                continue;
            }

            if (SaleWriteFailure.TryMap(postgres.MessageText, postgres.SqlState, postgres.ConstraintName, out var code))
            {
                return OperationFailure.Of(409, code);
            }
        }

        return null;
    }

    private static bool IsIdempotencyConflict(Exception exception) =>
        FindPostgres(exception) is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: not null } postgres
        && postgres.ConstraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase);

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private enum ClaimSave
    {
        Stored,
        Conflict,
        Failed,
    }

    private static bool TryPrice(
        bool extended,
        bool administrator,
        int lineNumber,
        SaleLineDraft line,
        Product product,
        TaxRate tax,
        out PricedLine priced,
        out FieldError error)
    {
        priced = new PricedLine(lineNumber, product, tax, line.Quantity, 0m, 0m, 0m, 0m, PriceKinds.Retail, DiscountKinds.None, null);
        error = new FieldError("lines", "validation.failed");
        var priceKind = extended ? line.PriceKind : PriceKinds.Retail;
        decimal listPrice;
        if (priceKind == PriceKinds.Wholesale)
        {
            if (product.WholesaleUnitPrice is not decimal wholesale)
            {
                error = new FieldError("priceKind", "price.wholesale_missing");
                return false;
            }

            listPrice = wholesale;
        }
        else
        {
            listPrice = product.UnitPrice;
        }

        var charged = line.UnitPrice;
        if (!extended || !administrator)
        {
            if (charged != listPrice)
            {
                error = new FieldError("unitPrice", "unitPrice.mismatch");
                return false;
            }

            charged = listPrice;
        }

        if (!SaleRules.TryGross(line.Quantity, charged, out var gross))
        {
            error = new FieldError("lineTotal", "line.scale");
            return false;
        }

        var kind = extended ? line.DiscountKind : DiscountKinds.None;
        var rate = extended ? line.DiscountRate : null;
        var submittedDiscount = extended ? line.LineDiscount : 0m;
        if (!SaleRules.TryDiscountAmount(gross, kind, rate, submittedDiscount, out var discount) || gross - discount != line.LineTotal)
        {
            error = new FieldError("lineTotal", "lineTotal.mismatch");
            return false;
        }

        priced = new PricedLine(
            lineNumber,
            product,
            tax,
            line.Quantity,
            listPrice,
            charged,
            discount,
            line.LineTotal,
            priceKind,
            kind,
            rate);
        return true;
    }

    private sealed record PricedLine(
        int LineNumber,
        Product Product,
        TaxRate Tax,
        decimal Quantity,
        decimal ListUnitPrice,
        decimal UnitPrice,
        decimal LineDiscount,
        decimal LineTotal,
        string PriceKind,
        string DiscountKind,
        decimal? DiscountRate);
}

public sealed class ImmediateSaleProductLock : ISaleProductLock
{
    public ValueTask AfterProductLockAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
