using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Pexilio.Infrastructure.Persistence;

namespace Pexilio.Infrastructure.Customers;

internal sealed record CustomerAccountRow(
    Guid Id,
    string Type,
    string Name,
    string? Phone,
    string? Address,
    string? Notes,
    string? Rc,
    string? Nif,
    string? Nis,
    string? Ai,
    decimal? CreditLimit,
    bool IsActive,
    decimal Outstanding);

internal sealed record CustomerStatementLineRow(
    DateTimeOffset OccurredAt,
    string Kind,
    Guid DocumentId,
    string? DocumentNumber,
    decimal Amount,
    decimal RunningBalance,
    bool? CreditLimitOverridden,
    string? CreditOverrideReason,
    decimal? CreditLimitSnapshot,
    decimal? CreditOutstandingBefore,
    decimal? CreditProjectedOutstanding);

internal sealed record CustomerStatementRow(
    Guid CustomerId,
    string CustomerName,
    decimal Opening,
    decimal Closing,
    decimal Outstanding,
    IReadOnlyList<CustomerStatementLineRow> Lines);

internal static class CustomerAccounts
{
    public static async Task<IReadOnlyList<CustomerAccountRow>> ReadCustomersAsync(
        PexilioDbContext database,
        Guid companyId,
        string nameKey,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        await using var command = await OpenAsync(database, CustomerSql, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter("company", companyId));
        command.Parameters.Add(new NpgsqlParameter("key", nameKey));
        command.Parameters.Add(new NpgsqlParameter("customer", customerId ?? (object)DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CustomerAccountRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CustomerAccountRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                Text(reader, 3),
                Text(reader, 4),
                Text(reader, 5),
                Text(reader, 6),
                Text(reader, 7),
                Text(reader, 8),
                Text(reader, 9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.GetBoolean(11),
                reader.GetDecimal(12)));
        }

        return rows;
    }

    public static async Task<decimal> ReadOutstandingAsync(
        PexilioDbContext database,
        Guid companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = await OpenAsync(database, OutstandingSql, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter("company", companyId));
        command.Parameters.Add(new NpgsqlParameter("customer", customerId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is decimal outstanding ? outstanding : 0m;
    }

    public static async Task<CustomerStatementRow?> ReadStatementAsync(
        PexilioDbContext database,
        Guid companyId,
        Guid customerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        await using var command = await OpenAsync(database, StatementSql, cancellationToken);
        command.Parameters.Add(new NpgsqlParameter("company", companyId));
        command.Parameters.Add(new NpgsqlParameter("customer", customerId));
        command.Parameters.Add(new NpgsqlParameter("from_utc", fromUtc));
        command.Parameters.Add(new NpgsqlParameter("to_utc", toUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        CustomerStatementRow? statement = null;
        var lines = new List<CustomerStatementLineRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            statement ??= new CustomerStatementRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                lines);
            if (reader.IsDBNull(7))
            {
                continue;
            }

            var kind = reader.GetString(6);
            var saleEvent = kind == "sale";
            lines.Add(new CustomerStatementLineRow(
                reader.GetFieldValue<DateTimeOffset>(5),
                kind,
                reader.GetGuid(7),
                Text(reader, 8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                saleEvent ? (bool?)(!reader.IsDBNull(11) && reader.GetBoolean(11)) : null,
                saleEvent ? Text(reader, 12) : null,
                saleEvent && !reader.IsDBNull(13) ? (decimal?)reader.GetDecimal(13) : null,
                saleEvent && !reader.IsDBNull(14) ? (decimal?)reader.GetDecimal(14) : null,
                saleEvent && !reader.IsDBNull(15) ? (decimal?)reader.GetDecimal(15) : null));
        }

        return statement;
    }

    private static async Task<NpgsqlCommand> OpenAsync(
        PexilioDbContext database,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = database.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction,
        };
        return command;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private const string CustomerSql = """
        WITH sales AS (
            SELECT s."CustomerId" AS customer_id, SUM(s."Total")::numeric(19,4) AS amount
            FROM pexilio.sale s
            WHERE s."CompanyId" = @company AND s."CustomerId" IS NOT NULL
            GROUP BY s."CustomerId"
        ),
        payments AS (
            SELECT s."CustomerId" AS customer_id, SUM(p."Amount")::numeric(19,4) AS amount
            FROM pexilio.sale_payment p
            JOIN pexilio.sale s ON s."Id" = p."SaleId" AND s."CompanyId" = p."CompanyId"
            WHERE s."CompanyId" = @company AND s."CustomerId" IS NOT NULL
            GROUP BY s."CustomerId"
        ),
        returns AS (
            SELECT s."CustomerId" AS customer_id, SUM(rl."Amount")::numeric(19,4) AS amount
            FROM pexilio.sale_return_line rl
            JOIN pexilio.sale s ON s."Id" = rl."SaleId" AND s."CompanyId" = rl."CompanyId"
            WHERE s."CompanyId" = @company AND s."CustomerId" IS NOT NULL
            GROUP BY s."CustomerId"
        ),
        refunds AS (
            SELECT s."CustomerId" AS customer_id, SUM(rf."Amount")::numeric(19,4) AS amount
            FROM pexilio.sale_refund rf
            JOIN pexilio.sale s ON s."Id" = rf."SaleId" AND s."CompanyId" = rf."CompanyId"
            WHERE s."CompanyId" = @company AND s."CustomerId" IS NOT NULL
            GROUP BY s."CustomerId"
        )
        SELECT c."Id", c."Type", c."Name", c."Phone", c."Address", c."Notes",
               c."Rc", c."Nif", c."Nis", c."Ai", c."CreditLimit", c."IsActive",
               (COALESCE(sales.amount, 0) - COALESCE(returns.amount, 0) - COALESCE(payments.amount, 0) + COALESCE(refunds.amount, 0))::numeric(19,4)
        FROM pexilio.customer c
        LEFT JOIN sales ON sales.customer_id = c."Id"
        LEFT JOIN payments ON payments.customer_id = c."Id"
        LEFT JOIN returns ON returns.customer_id = c."Id"
        LEFT JOIN refunds ON refunds.customer_id = c."Id"
        WHERE c."CompanyId" = @company
          AND (@key = '' OR starts_with(c."NameKey", @key))
          AND (@customer IS NULL OR c."Id" = @customer)
        ORDER BY c."Name", c."Phone", c."Id"
        """;

    // Same signed total used by the customer list: sales - returns - payments + refunds.
    // Each source is aggregated on its own. Walk-in sales stay out through CustomerId.
    private const string OutstandingSql = """
        SELECT (
            COALESCE((
                SELECT SUM(s."Total")
                FROM pexilio.sale s
                WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            ), 0)
            - COALESCE((
                SELECT SUM(rl."Amount")
                FROM pexilio.sale_return_line rl
                JOIN pexilio.sale s ON s."Id" = rl."SaleId" AND s."CompanyId" = rl."CompanyId"
                WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            ), 0)
            - COALESCE((
                SELECT SUM(p."Amount")
                FROM pexilio.sale_payment p
                JOIN pexilio.sale s ON s."Id" = p."SaleId" AND s."CompanyId" = p."CompanyId"
                WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            ), 0)
            + COALESCE((
                SELECT SUM(rf."Amount")
                FROM pexilio.sale_refund rf
                JOIN pexilio.sale s ON s."Id" = rf."SaleId" AND s."CompanyId" = rf."CompanyId"
                WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            ), 0)
        )::numeric(19,4)
        """;

    // One net return event: -(return amount - refund amount). Refund tenders are summed by return
    // before the join, so a return and its refund never create two statement rows.
    // Kind order matches CustomerStatementMath: sale, return, payment, then document id text.
    private const string StatementSql = """
        WITH customer_row AS (
            SELECT "Id", "Name"
            FROM pexilio.customer
            WHERE "CompanyId" = @company AND "Id" = @customer
        ),
        return_documents AS (
            SELECT rl."ReturnId", rl."SaleId", rl."CompanyId", SUM(rl."Amount") AS amount
            FROM pexilio.sale_return_line rl
            WHERE rl."CompanyId" = @company
            GROUP BY rl."ReturnId", rl."SaleId", rl."CompanyId"
        ),
        refund_totals AS (
            SELECT rf."SaleReturnId", rf."SaleId", rf."CompanyId", SUM(rf."Amount") AS amount
            FROM pexilio.sale_refund rf
            WHERE rf."CompanyId" = @company
            GROUP BY rf."SaleReturnId", rf."SaleId", rf."CompanyId"
        ),
        events AS (
            SELECT s."CreatedAtUtc" AS occurred_at,
                   'sale'::text AS kind,
                   0 AS kind_rank,
                   s."Id" AS document_id,
                   s."Number" AS document_number,
                   s."Total" AS signed_amount,
                   s."CreditLimitOverridden" AS credit_overridden,
                   s."CreditOverrideReason" AS credit_reason,
                   s."CreditLimitSnapshot" AS credit_limit,
                   s."CreditOutstandingBefore" AS credit_before,
                   s."CreditProjectedOutstanding" AS credit_projected
            FROM pexilio.sale s
            WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            UNION ALL
            SELECT p."PaidAtUtc", 'payment', 2, p."Id", p."ReceiptNumber", -p."Amount",
                   NULL::boolean, NULL::text, NULL::numeric, NULL::numeric, NULL::numeric
            FROM pexilio.sale_payment p
            JOIN pexilio.sale s ON s."Id" = p."SaleId" AND s."CompanyId" = p."CompanyId"
            WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
            UNION ALL
            SELECT r."ReturnedAtUtc", 'return', 1, r."Id", r."Number",
                   -(rd.amount - COALESCE(rf.amount, 0)),
                   NULL::boolean, NULL::text, NULL::numeric, NULL::numeric, NULL::numeric
            FROM return_documents rd
            JOIN pexilio.sale_return r
              ON r."Id" = rd."ReturnId" AND r."SaleId" = rd."SaleId" AND r."CompanyId" = rd."CompanyId"
            JOIN pexilio.sale s
              ON s."Id" = rd."SaleId" AND s."CompanyId" = rd."CompanyId"
            LEFT JOIN refund_totals rf
              ON rf."SaleReturnId" = rd."ReturnId"
             AND rf."SaleId" = rd."SaleId"
             AND rf."CompanyId" = rd."CompanyId"
            WHERE s."CompanyId" = @company AND s."CustomerId" = @customer
        ),
        figures AS (
            SELECT
                COALESCE(SUM(signed_amount) FILTER (WHERE occurred_at < @from_utc), 0)::numeric(19,4) AS opening,
                COALESCE(SUM(signed_amount) FILTER (WHERE occurred_at < @to_utc), 0)::numeric(19,4) AS closing,
                COALESCE(SUM(signed_amount), 0)::numeric(19,4) AS outstanding
            FROM events
        ),
        period AS (
            SELECT occurred_at, kind, kind_rank, document_id, document_number,
                   signed_amount::numeric(19,4) AS signed_amount,
                   credit_overridden,
                   credit_reason,
                   credit_limit,
                   credit_before,
                   credit_projected,
                   SUM(signed_amount) OVER (
                       ORDER BY occurred_at, kind_rank, document_id::text
                       ROWS UNBOUNDED PRECEDING
                   )::numeric(19,4) AS running_delta
            FROM events
            WHERE occurred_at >= @from_utc AND occurred_at < @to_utc
        )
        SELECT customer_row."Id",
               customer_row."Name",
               figures.opening,
               figures.closing,
               figures.outstanding,
               period.occurred_at,
               period.kind,
               period.document_id,
               period.document_number,
               period.signed_amount,
               (figures.opening + period.running_delta)::numeric(19,4),
               period.credit_overridden,
               period.credit_reason,
               period.credit_limit,
               period.credit_before,
               period.credit_projected
        FROM customer_row
        CROSS JOIN figures
        LEFT JOIN period ON TRUE
        ORDER BY period.occurred_at NULLS FIRST, period.kind_rank NULLS FIRST, period.document_id::text NULLS FIRST
        """;
}
