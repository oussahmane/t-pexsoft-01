import { describe, expect, it } from "vitest";
import { messageKeys, translate } from "./i18n";
import {
  clearPending,
  estimateLineAmount,
  loadPending,
  reasonIssue,
  refundQuote,
  returnBlockReason,
  returnFailure,
  returnQuantityIssue,
  returnRows,
  savePending,
  tenderAllocation,
  type ReturnableSale,
} from "./saleReturns";
import { memoryStorage } from "./test/memoryStorage";

const sale: ReturnableSale = {
  id: "sale-1",
  number: "V-000020",
  status: "posted",
  total: "40.0000",
  paidAmount: "25.0000",
  refundedAmount: "0.0000",
  outstandingAmount: "15.0000",
  customerId: "c1",
  customerName: "Amin",
  lines: [
    { id: "line-1", productSku: "EAU-1", productName: "Eau", quantity: "3.0000", unitPrice: "10.0000", lineTotal: "30.0000", allocatedSaleDiscount: "0.0000", tracksStock: true, returnedQuantity: "1.0000", remainingReturnableQuantity: "2.0000", returnedAmount: "10.0000", remainingReturnableAmount: "20.0000" },
    { id: "line-2", productSku: "FAR-1", productName: "Farine", quantity: "2.0000", unitPrice: "5.0000", lineTotal: "10.0000", allocatedSaleDiscount: "0.0000", tracksStock: false, returnedQuantity: "0.0000", remainingReturnableQuantity: "2.0000", returnedAmount: "0.0000", remainingReturnableAmount: "10.0000" },
  ],
};

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

describe("sale return rules", () => {
  it("uses the server's returned and remaining quantities and amounts", () => {
    const { rows, authoritative } = returnRows(sale);
    expect(authoritative).toBe(true);
    expect(rows[0]).toMatchObject({
      previous: "1.0000",
      returnedAmount: "10.0000",
      remainingQuantity: "2.0000",
      remainingValue: "20.0000",
      cap: 20_000n,
      basisValue: 200_000n,
    });
    expect(rows[1]).toMatchObject({ previous: "0.0000", returnedAmount: "0.0000", remainingQuantity: "2.0000", remainingValue: "10.0000" });
    expect(returnBlockReason(sale)).toBeNull();
  });

  it("refuses an estimate when the server omits the return history", () => {
    const bare = {
      ...sale,
      lines: sale.lines.map((line) => ({
        id: line.id,
        productSku: line.productSku,
        productName: line.productName,
        quantity: line.quantity,
        unitPrice: line.unitPrice,
        lineTotal: line.lineTotal,
        allocatedSaleDiscount: line.allocatedSaleDiscount,
        tracksStock: line.tracksStock,
      })),
    };
    const { rows, authoritative } = returnRows(bare);
    expect(authoritative).toBe(false);
    expect(rows[0]).toMatchObject({ previous: null, returnedAmount: null, remainingQuantity: null, cap: 0n, basisQuantity: 0n, basisValue: 0n });
    expect(returnBlockReason(bare)).toBe("returns.unavailable");
    expect(estimateLineAmount(rows[0].basisValue, rows[0].basisQuantity, 10_000n)).toBeNull();
  });

  it("reports a fully returned sale from the remaining quantities", () => {
    const finished = {
      ...sale,
      lines: sale.lines.map((line) => ({
        ...line,
        returnedQuantity: line.quantity,
        remainingReturnableQuantity: "0.0000",
        returnedAmount: line.lineTotal,
        remainingReturnableAmount: "0.0000",
      })),
    };
    expect(returnBlockReason(finished)).toBe("returns.nothingLeft");
    expect(returnBlockReason({ ...sale, status: "draft" })).toBe("returns.error.ineligible");
  });

  it("estimates line amounts with the server's half-away proportion", () => {
    expect(estimateLineAmount(100_000n, 30_000n, 10_000n)).toBe(33_333n);
    expect(estimateLineAmount(100_000n, 30_000n, 20_000n)).toBe(66_667n);
    expect(estimateLineAmount(66_667n, 20_000n, 20_000n)).toBe(66_667n);
    expect(estimateLineAmount(100_000n, 30_000n, 40_000n)).toBeNull();
  });

  it("validates return quantities against the remaining quantity", () => {
    expect(returnQuantityIssue("0", 30_000n)).toBe("returns.quantity.positive");
    expect(returnQuantityIssue("3.5", 30_000n)).toBe("returns.quantity.exceeds");
    expect(returnQuantityIssue("1.00001", 30_000n)).toBe("returns.quantity.invalid");
    expect(returnQuantityIssue("1.5", 30_000n)).toBeNull();
  });

  it("requires a refund only above the outstanding amount", () => {
    expect(refundQuote(100_000n, sale)).toMatchObject({ requiredRefund: 0n, netReduction: 100_000n, collected: 250_000n });
    expect(refundQuote(300_000n, sale)).toMatchObject({ requiredRefund: 150_000n, netReduction: 150_000n, exceedsCollected: false });
    expect(refundQuote(300_000n, { ...sale, paidAmount: "10.0000", outstandingAmount: "0.0000", total: "10.0000" }).exceedsCollected).toBe(true);
  });

  it("requires an exact tender allocation without an other method", () => {
    const required = 150_000n;
    expect(tenderAllocation(required, { cash: "15", transfer: "", card: "" })).toMatchObject({ issue: null, refunds: [{ method: "cash", amount: "15.0000" }] });
    expect(tenderAllocation(required, { cash: "10", transfer: "", card: "" })).toMatchObject({ issue: "returns.tender.under", remaining: 50_000n });
    expect(tenderAllocation(required, { cash: "10", transfer: "6", card: "" }).issue).toBe("returns.tender.over");
    expect(tenderAllocation(required, { cash: "abc", transfer: "", card: "" }).issue).toBe("returns.tender.invalid");
    expect(tenderAllocation(0n, { cash: "5", transfer: "", card: "" })).toMatchObject({ issue: null, refunds: [] });
  });

  it("requires a reason of 1 to 200 characters", () => {
    expect(reasonIssue("   ")).toBe("returns.reason.required");
    expect(reasonIssue("x".repeat(201))).toBe("returns.reason.length");
    expect(reasonIssue(` ${"x".repeat(200)} `)).toBeNull();
  });

  it("maps backend responses to cashier messages and retry decisions", async () => {
    expect(await returnFailure(json({ code: "validation.failed", errors: { "lines[0].quantity": ["return.exceeds"] } }, 400))).toEqual({
      message: "returns.error.alreadyReturned",
      uncertain: false,
    });
    expect(await returnFailure(json({ code: "validation.failed", errors: { refund: ["refund.exceeds"] } }, 400))).toMatchObject({ message: "returns.error.collected" });
    expect(await returnFailure(json({ code: "validation.failed", errors: { refunds: ["refund.under"] } }, 400))).toMatchObject({ message: "returns.error.tender" });
    expect(await returnFailure(json({ code: "sale.not_found" }, 404))).toMatchObject({ message: "returns.error.ineligible", uncertain: false });
    expect(await returnFailure(json({ code: "sale.idempotency_conflict" }, 409))).toMatchObject({ message: "returns.error.idempotency" });
    expect(await returnFailure(json({ code: "auth.required" }, 401))).toEqual({ message: "returns.error.auth", uncertain: true });
    expect(await returnFailure(json({ code: "auth.forbidden" }, 403))).toEqual({ message: "returns.error.forbidden", uncertain: false });
    expect(await returnFailure(json({ code: "request.failed" }, 500))).toEqual({ message: "returns.error.network", uncertain: true });
    expect(await returnFailure(new Response("Npgsql exception", { status: 409 }))).toEqual({ message: "returns.error.rejected", uncertain: false });
  });

  it("keeps a pending operation per sale until it is cleared", () => {
    const storage = memoryStorage();
    const pending = { key: "key-1", request: { reason: "Abîmé", lines: [{ saleLineId: "line-1", quantity: "1.0000" }], refunds: [] } };
    savePending(storage, "api", "sale-1", pending);
    expect(loadPending(storage, "api", "sale-1")).toEqual(pending);
    expect(loadPending(storage, "api", "sale-2")).toBeNull();
    clearPending(storage, "api", "sale-1");
    expect(loadPending(storage, "api", "sale-1")).toBeNull();
  });

});

describe("return translations", () => {
  it("provides every return message in French, Arabic, and English", () => {
    const french = messageKeys("fr").filter((key) => key.startsWith("returns."));
    expect(french.length).toBeGreaterThan(50);
    for (const locale of ["ar", "en"] as const) {
      expect(messageKeys(locale).filter((key) => key.startsWith("returns.")).sort()).toEqual([...french].sort());
    }
    expect(translate("fr", "returns.title")).toBe("Retour sur vente");
    expect(translate("ar", "returns.title")).toBe("مرتجع على البيع");
    expect(translate("en", "returns.title")).toBe("Sale return");
  });
});
