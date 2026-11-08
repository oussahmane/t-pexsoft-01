using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pexilio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SaleRefundAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_refund",
                schema: "pexilio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RefundedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RefundedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_refund", x => x.Id);
                    table.CheckConstraint("CK_sale_refund_amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_sale_refund_method", "\"Method\" IN ('cash', 'transfer', 'card')");
                    table.ForeignKey(
                        name: "FK_sale_refund_AspNetUsers_RefundedByUserId",
                        column: x => x.RefundedByUserId,
                        principalSchema: "pexilio",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_refund_return",
                        columns: x => new { x.SaleReturnId, x.SaleId, x.CompanyId },
                        principalSchema: "pexilio",
                        principalTable: "sale_return",
                        principalColumns: new[] { "Id", "SaleId", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sale_refund_sale",
                        columns: x => new { x.SaleId, x.CompanyId },
                        principalSchema: "pexilio",
                        principalTable: "sale",
                        principalColumns: new[] { "Id", "CompanyId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_refund_company_return",
                schema: "pexilio",
                table: "sale_refund",
                columns: new[] { "CompanyId", "SaleReturnId", "SaleId" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_refund_RefundedByUserId",
                schema: "pexilio",
                table: "sale_refund",
                column: "RefundedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_refund_SaleId_CompanyId",
                schema: "pexilio",
                table: "sale_refund",
                columns: new[] { "SaleId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_refund_SaleReturnId_SaleId_CompanyId",
                schema: "pexilio",
                table: "sale_refund",
                columns: new[] { "SaleReturnId", "SaleId", "CompanyId" });

            // Checked at commit, after every return line and refund row in the transaction is visible.
            // Replacing the function keeps the existing deferred triggers. The credit snapshot trigger
            // still runs only on sale insert and does not rewrite stored snapshot columns.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION pexilio.sale_assert_outstanding(p_sale_id uuid)
                RETURNS void
                LANGUAGE plpgsql
                AS $$
                DECLARE
                  original numeric(19,4);
                  returned numeric(19,4);
                  paid numeric(19,4);
                  refunded numeric(19,4);
                BEGIN
                  SELECT "Total" INTO original FROM pexilio.sale WHERE "Id" = p_sale_id;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION 'sale.payment_integrity' USING ERRCODE = '23514';
                  END IF;

                  SELECT COALESCE(SUM("Amount"), 0) INTO returned
                  FROM pexilio.sale_return_line
                  WHERE "SaleId" = p_sale_id;

                  SELECT COALESCE(SUM("Amount"), 0) INTO paid
                  FROM pexilio.sale_payment
                  WHERE "SaleId" = p_sale_id;

                  SELECT COALESCE(SUM("Amount"), 0) INTO refunded
                  FROM pexilio.sale_refund
                  WHERE "SaleId" = p_sale_id;

                  IF returned > original
                     OR refunded > paid
                     OR (paid - refunded) > (original - returned) THEN
                    RAISE EXCEPTION 'sale.payment_integrity' USING ERRCODE = '23514';
                  END IF;
                END;
                $$;

                CREATE OR REPLACE FUNCTION pexilio.sale_credit_override_snapshot()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                  configured numeric(19,4);
                  prior numeric(19,4);
                  paid numeric(19,4);
                  returned numeric(19,4);
                  refunded numeric(19,4);
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
                      + COALESCE((
                          SELECT SUM(rf."Amount")
                          FROM pexilio.sale_refund rf
                          JOIN pexilio.sale s ON s."Id" = rf."SaleId" AND s."CompanyId" = rf."CompanyId"
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

                  SELECT COALESCE(SUM("Amount"), 0)::numeric(19,4)
                  INTO refunded
                  FROM pexilio.sale_refund
                  WHERE "SaleId" = NEW."Id" AND "CompanyId" = NEW."CompanyId";

                  projected := (prior + NEW."Total" - paid)::numeric(19,4);

                  IF returned <> 0
                     OR refunded <> 0
                     OR paid >= NEW."Total"
                     OR prior IS DISTINCT FROM NEW."CreditOutstandingBefore"
                     OR projected IS DISTINCT FROM NEW."CreditProjectedOutstanding"
                     OR projected <= NEW."CreditLimitSnapshot" THEN
                    RAISE EXCEPTION 'sale.credit_override_integrity' USING ERRCODE = '23514';
                  END IF;

                  RETURN NULL;
                END;
                $$;

                CREATE FUNCTION pexilio.sale_refund_within_return()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                  returned numeric(19,4);
                  refunded numeric(19,4);
                BEGIN
                  SELECT COALESCE(SUM("Amount"), 0) INTO returned
                  FROM pexilio.sale_return_line
                  WHERE "ReturnId" = NEW."SaleReturnId"
                    AND "SaleId" = NEW."SaleId"
                    AND "CompanyId" = NEW."CompanyId";

                  SELECT COALESCE(SUM("Amount"), 0) INTO refunded
                  FROM pexilio.sale_refund
                  WHERE "SaleReturnId" = NEW."SaleReturnId"
                    AND "SaleId" = NEW."SaleId"
                    AND "CompanyId" = NEW."CompanyId";

                  IF refunded > returned THEN
                    RAISE EXCEPTION 'sale.payment_integrity' USING ERRCODE = '23514';
                  END IF;
                  RETURN NULL;
                END;
                $$;

                CREATE TRIGGER sale_refund_append_only
                BEFORE UPDATE OR DELETE ON pexilio.sale_refund
                FOR EACH ROW EXECUTE FUNCTION pexilio.sale_document_immutable();
                CREATE TRIGGER sale_refund_append_only_truncate
                BEFORE TRUNCATE ON pexilio.sale_refund
                FOR EACH STATEMENT EXECUTE FUNCTION pexilio.sale_document_immutable();
                CREATE CONSTRAINT TRIGGER sale_outstanding_on_refund
                AFTER INSERT ON pexilio.sale_refund
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION pexilio.sale_outstanding_on_insert();
                CREATE CONSTRAINT TRIGGER sale_refund_within_return
                AFTER INSERT ON pexilio.sale_refund
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION pexilio.sale_refund_within_return();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS sale_refund_within_return ON pexilio.sale_refund;
                DROP TRIGGER IF EXISTS sale_outstanding_on_refund ON pexilio.sale_refund;
                DROP TRIGGER IF EXISTS sale_refund_append_only_truncate ON pexilio.sale_refund;
                DROP TRIGGER IF EXISTS sale_refund_append_only ON pexilio.sale_refund;
                DROP FUNCTION IF EXISTS pexilio.sale_refund_within_return();

                CREATE OR REPLACE FUNCTION pexilio.sale_assert_outstanding(p_sale_id uuid)
                RETURNS void
                LANGUAGE plpgsql
                AS $$
                DECLARE
                  original numeric(19,4);
                  returned numeric(19,4);
                  paid numeric(19,4);
                BEGIN
                  SELECT "Total" INTO original FROM pexilio.sale WHERE "Id" = p_sale_id;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION 'sale.payment_integrity' USING ERRCODE = '23514';
                  END IF;

                  SELECT COALESCE(SUM("Amount"), 0) INTO returned
                  FROM pexilio.sale_return_line
                  WHERE "SaleId" = p_sale_id;

                  SELECT COALESCE(SUM("Amount"), 0) INTO paid
                  FROM pexilio.sale_payment
                  WHERE "SaleId" = p_sale_id;

                  IF returned > original OR paid > original - returned THEN
                    RAISE EXCEPTION 'sale.payment_integrity' USING ERRCODE = '23514';
                  END IF;
                END;
                $$;

                CREATE OR REPLACE FUNCTION pexilio.sale_credit_override_snapshot()
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
                """);

            migrationBuilder.DropTable(
                name: "sale_refund",
                schema: "pexilio");
        }
    }
}
