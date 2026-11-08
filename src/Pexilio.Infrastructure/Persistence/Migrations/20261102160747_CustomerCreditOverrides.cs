using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pexilio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerCreditOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreditLimitOverridden",
                schema: "pexilio",
                table: "sale",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditLimitSnapshot",
                schema: "pexilio",
                table: "sale",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditOutstandingBefore",
                schema: "pexilio",
                table: "sale",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreditOverrideAtUtc",
                schema: "pexilio",
                table: "sale",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreditOverrideReason",
                schema: "pexilio",
                table: "sale",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditProjectedOutstanding",
                schema: "pexilio",
                table: "sale",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale",
                column: "CreditOverrideByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_sale_credit_override",
                schema: "pexilio",
                table: "sale",
                sql: "(NOT \"CreditLimitOverridden\"\n    AND \"CreditOverrideReason\" IS NULL\n    AND \"CreditOverrideByUserId\" IS NULL\n    AND \"CreditOverrideAtUtc\" IS NULL\n    AND \"CreditLimitSnapshot\" IS NULL\n    AND \"CreditOutstandingBefore\" IS NULL\n    AND \"CreditProjectedOutstanding\" IS NULL)\nOR (\"CreditLimitOverridden\"\n    AND \"CustomerId\" IS NOT NULL\n    AND \"CreditOverrideReason\" IS NOT NULL\n    AND btrim(\"CreditOverrideReason\") = \"CreditOverrideReason\"\n    AND char_length(\"CreditOverrideReason\") BETWEEN 1 AND 200\n    AND \"CreditOverrideByUserId\" IS NOT NULL\n    AND \"CreditOverrideByUserId\" = \"CreatedByUserId\"\n    AND \"CreditOverrideAtUtc\" IS NOT NULL\n    AND \"CreditLimitSnapshot\" IS NOT NULL\n    AND \"CreditLimitSnapshot\" >= 0\n    AND \"CreditOutstandingBefore\" IS NOT NULL\n    AND \"CreditOutstandingBefore\" >= 0\n    AND \"CreditProjectedOutstanding\" IS NOT NULL\n    AND \"CreditProjectedOutstanding\" > \"CreditLimitSnapshot\")");

            migrationBuilder.AddForeignKey(
                name: "FK_sale_AspNetUsers_CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale",
                column: "CreditOverrideByUserId",
                principalSchema: "pexilio",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Checked at commit of the sale insert only. Later payments, returns, and credit-limit
            // edits do not revalidate this historical snapshot.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION pexilio.sale_credit_override_snapshot()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                  configured numeric(19,4);
                  prior numeric(19,4);
                  paid numeric(19,4);
                  returned numeric(19,4);
                  projected numeric(19,4);
                BEGIN
                  IF NOT NEW."CreditLimitOverridden" THEN
                    RETURN NULL;
                  END IF;

                  SELECT c."CreditLimit"
                  INTO configured
                  FROM pexilio.customer c
                  WHERE c."Id" = NEW."CustomerId"
                    AND c."CompanyId" = NEW."CompanyId";

                  IF NOT FOUND OR configured IS DISTINCT FROM NEW."CreditLimitSnapshot" THEN
                    RAISE EXCEPTION 'sale.credit_override_integrity' USING ERRCODE = '23514';
                  END IF;

                  SELECT (
                      COALESCE((
                          SELECT SUM(s."Total")
                          FROM pexilio.sale s
                          WHERE s."CompanyId" = NEW."CompanyId"
                            AND s."CustomerId" = NEW."CustomerId"
                            AND s."Id" <> NEW."Id"
                      ), 0)
                      - COALESCE((
                          SELECT SUM(rl."Amount")
                          FROM pexilio.sale_return_line rl
                          JOIN pexilio.sale s ON s."Id" = rl."SaleId" AND s."CompanyId" = rl."CompanyId"
                          WHERE s."CompanyId" = NEW."CompanyId"
                            AND s."CustomerId" = NEW."CustomerId"
                            AND s."Id" <> NEW."Id"
                      ), 0)
                      - COALESCE((
                          SELECT SUM(p."Amount")
                          FROM pexilio.sale_payment p
                          JOIN pexilio.sale s ON s."Id" = p."SaleId" AND s."CompanyId" = p."CompanyId"
                          WHERE s."CompanyId" = NEW."CompanyId"
                            AND s."CustomerId" = NEW."CustomerId"
                            AND s."Id" <> NEW."Id"
                      ), 0)
                  )::numeric(19,4)
                  INTO prior;

                  SELECT COALESCE(SUM("Amount"), 0)::numeric(19,4)
                  INTO paid
                  FROM pexilio.sale_payment
                  WHERE "SaleId" = NEW."Id" AND "CompanyId" = NEW."CompanyId";

                  SELECT COALESCE(SUM("Amount"), 0)::numeric(19,4)
                  INTO returned
                  FROM pexilio.sale_return_line
                  WHERE "SaleId" = NEW."Id" AND "CompanyId" = NEW."CompanyId";

                  projected := (prior + NEW."Total" - paid)::numeric(19,4);

                  IF returned <> 0
                     OR paid >= NEW."Total"
                     OR prior IS DISTINCT FROM NEW."CreditOutstandingBefore"
                     OR projected IS DISTINCT FROM NEW."CreditProjectedOutstanding"
                     OR projected <= NEW."CreditLimitSnapshot" THEN
                    RAISE EXCEPTION 'sale.credit_override_integrity' USING ERRCODE = '23514';
                  END IF;

                  RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER sale_credit_override_snapshot
                AFTER INSERT ON pexilio.sale
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (NEW."CreditLimitOverridden")
                EXECUTE FUNCTION pexilio.sale_credit_override_snapshot();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS sale_credit_override_snapshot ON pexilio.sale;
                DROP FUNCTION IF EXISTS pexilio.sale_credit_override_snapshot();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_sale_AspNetUsers_CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropIndex(
                name: "IX_sale_CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sale_credit_override",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditLimitOverridden",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditLimitSnapshot",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditOutstandingBefore",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditOverrideAtUtc",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditOverrideByUserId",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditOverrideReason",
                schema: "pexilio",
                table: "sale");

            migrationBuilder.DropColumn(
                name: "CreditProjectedOutstanding",
                schema: "pexilio",
                table: "sale");
        }
    }
}
