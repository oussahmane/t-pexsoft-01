import { formatAmount, parseAmount } from "./money";
import type { RefundMethod, SaleReturnRequest } from "./salesClient";

export const REFUND_METHODS: RefundMethod[] = ["cash", "transfer", "card"];
export const REASON_MAX_LENGTH = 200;

const PENDING_KEY = "pexilio.saleReturns.pending.v1";

export type SaleBalanceFields = {
  total: string;
  paidAmount?: string;
  refundedAmount?: string;
  outstandingAmount?: string;
};

export type ReturnableSaleLine = {
  id: string;
  productSku: string;
  productName: string;
  quantity: string;
  unitPrice: string;
  lineTotal: string;
  allocatedSaleDiscount?: string;
  tracksStock?: boolean;
  returnedQuantity?: string;
  remainingReturnableQuantity?: string;
  returnedAmount?: string;
  remainingReturnableAmount?: string;
};

export type ReturnableSale = SaleBalanceFields & {
  id: string;
  number: string;
  status?: string;
  customerId?: string | null;
  customerName?: string | null;
  lines: ReturnableSaleLine[];
};

export type ReturnRow = {
  id: string;
  productName: string;
  productSku: string;
  tracksStock: boolean | null;
  quantity: string;
  unitPrice: string;
  previous: string | null;
  returnedAmount: string | null;
  remainingQuantity: string | null;
  remainingValue: string | null;
  cap: bigint;
  basisQuantity: bigint;
  basisValue: bigint;
};

export type PendingReturn = {
  key: string;
  request: SaleReturnRequest;
};

export type ReturnFailure = {
  message: string;
  uncertain: boolean;
};

type ProblemBody = {
  code?: string;
  errors?: Record<string, string[]>;
};

function scaled(value: string | undefined): bigint | null {
  return value === undefined ? null : parseAmount(value);
}

export function returnRows(sale: ReturnableSale): { rows: ReturnRow[]; authoritative: boolean } {
  const parsed = sale.lines.map((line) => ({
    line,
    returnedQuantity: scaled(line.returnedQuantity),
    remainingQuantity: scaled(line.remainingReturnableQuantity),
    returnedAmount: scaled(line.returnedAmount),
    remainingValue: scaled(line.remainingReturnableAmount),
  }));
  const authoritative =
    parsed.length > 0 &&
    parsed.every(
      (row) =>
        row.returnedQuantity !== null &&
        row.remainingQuantity !== null &&
        row.returnedAmount !== null &&
        row.remainingValue !== null &&
        row.returnedQuantity >= 0n &&
        row.remainingQuantity >= 0n &&
        row.returnedAmount >= 0n &&
        row.remainingValue >= 0n,
    );
  const rows = parsed.map((row) => ({
    id: row.line.id,
    productName: row.line.productName,
    productSku: row.line.productSku,
    tracksStock: row.line.tracksStock ?? null,
    quantity: row.line.quantity,
    unitPrice: row.line.unitPrice,
    previous: authoritative ? formatAmount(row.returnedQuantity as bigint) : null,
    returnedAmount: authoritative ? formatAmount(row.returnedAmount as bigint) : null,
    remainingQuantity: authoritative ? formatAmount(row.remainingQuantity as bigint) : null,
    remainingValue: authoritative ? formatAmount(row.remainingValue as bigint) : null,
    cap: authoritative ? (row.remainingQuantity as bigint) : 0n,
    basisQuantity: authoritative ? (row.remainingQuantity as bigint) : 0n,
    basisValue: authoritative ? (row.remainingValue as bigint) : 0n,
  }));
  return { rows, authoritative };
}

export function returnBlockReason(sale: ReturnableSale): string | null {
  const view = returnRows(sale);
  if (!view.authoritative) {
    return "returns.unavailable";
  }

  if (sale.status !== undefined && sale.status !== "posted") {
    return "returns.error.ineligible";
  }

  if (!view.rows.some((row) => row.cap > 0n)) {
    return "returns.nothingLeft";
  }

  return null;
}

export function returnEligible(sale: ReturnableSale): boolean {
  return returnBlockReason(sale) === null;
}

// Preview only: same half-away scaled proportion as SaleReturnRules on the server.
export function estimateLineAmount(remainingValue: bigint, remainingQuantity: bigint, quantity: bigint): bigint | null {
  if (remainingQuantity <= 0n || quantity <= 0n || quantity > remainingQuantity || remainingValue < 0n) {
    return null;
  }

  if (quantity === remainingQuantity) {
    return remainingValue;
  }

  const product = remainingValue * quantity;
  const quotient = product / remainingQuantity;
  const remainder = product % remainingQuantity;
  return remainder * 2n >= remainingQuantity ? quotient + 1n : quotient;
}

export function returnQuantityIssue(text: string, cap: bigint): string | null {
  const quantity = parseAmount(text);
  if (quantity === null) {
    return "returns.quantity.invalid";
  }

  if (quantity <= 0n) {
    return "returns.quantity.positive";
  }

  if (quantity > cap) {
    return "returns.quantity.exceeds";
  }

  return null;
}

export function refundQuote(
  returnAmount: bigint,
  sale: SaleBalanceFields,
): { requiredRefund: bigint; netReduction: bigint; outstanding: bigint; collected: bigint | null; exceedsCollected: boolean } {
  const outstanding = parseAmount(sale.outstandingAmount ?? "0") ?? 0n;
  const paid = sale.paidAmount === undefined ? null : parseAmount(sale.paidAmount);
  const refunded = parseAmount(sale.refundedAmount ?? "0") ?? 0n;
  const collected = paid === null ? null : paid - refunded;
  const requiredRefund = returnAmount > outstanding ? returnAmount - outstanding : 0n;
  return {
    requiredRefund,
    netReduction: returnAmount - requiredRefund,
    outstanding,
    collected,
    exceedsCollected: collected !== null && requiredRefund > collected,
  };
}

export function tenderAllocation(
  requiredRefund: bigint,
  inputs: Record<RefundMethod, string>,
): { refunds: SaleReturnRequest["refunds"]; allocated: bigint; remaining: bigint; issue: string | null } {
  const refunds: SaleReturnRequest["refunds"] = [];
  let allocated = 0n;
  let invalid = false;
  for (const method of REFUND_METHODS) {
    const text = inputs[method].trim();
    if (text === "") {
      continue;
    }

    const amount = parseAmount(text);
    if (amount === null) {
      invalid = true;
      continue;
    }

    if (amount > 0n) {
      refunds.push({ method, amount: formatAmount(amount) });
      allocated += amount;
    }
  }

  const remaining = requiredRefund - allocated;
  const issue = invalid
    ? "returns.tender.invalid"
    : remaining > 0n
      ? "returns.tender.under"
      : remaining < 0n
        ? "returns.tender.over"
        : null;
  return { refunds: requiredRefund === 0n ? [] : refunds, allocated, remaining, issue: requiredRefund === 0n ? null : issue };
}

export function reasonIssue(text: string): string | null {
  const trimmed = text.trim();
  if (trimmed.length === 0) {
    return "returns.reason.required";
  }

  if (trimmed.length > REASON_MAX_LENGTH) {
    return "returns.reason.length";
  }

  return null;
}

const codeMessages: [string[], string][] = [
  [["return.exceeds"], "returns.error.alreadyReturned"],
  [["refund.exceeds"], "returns.error.collected"],
  [
    ["refund.under", "refund.over", "refund.unexpected", "refund.method", "refunds.duplicate", "refund.positive", "refund.sign", "refund.scale", "refund.integrity"],
    "returns.error.tender",
  ],
  [["sale.not_found", "saleLine.unknown", "customer.not_found", "company.missing"], "returns.error.ineligible"],
  [["sale.idempotency_conflict"], "returns.error.idempotency"],
  [["auth.forbidden"], "returns.error.forbidden"],
  [["reason.required"], "returns.reason.required"],
  [["reason.length"], "returns.reason.length"],
  [
    ["quantity.positive", "quantity.scale", "quantity.sign", "quantity.invalid", "quantity.required", "quantity.range", "lines.duplicate", "lines.required", "saleLine.required", "amount.mismatch"],
    "returns.error.quantity",
  ],
];

export async function returnFailure(response: Response): Promise<ReturnFailure> {
  if (response.status >= 500 || response.status === 408) {
    return { message: "returns.error.network", uncertain: true };
  }

  let problem: ProblemBody = {};
  try {
    problem = (await response.json()) as ProblemBody;
  } catch {
    problem = {};
  }

  if (response.status === 401 || problem.code === "auth.required") {
    return { message: "returns.error.auth", uncertain: true };
  }

  const codes = [...Object.values(problem.errors ?? {}).flat(), ...(problem.code ? [problem.code] : [])];
  for (const [known, message] of codeMessages) {
    if (codes.some((code) => known.includes(code))) {
      return { message, uncertain: false };
    }
  }

  return { message: response.status === 403 ? "returns.error.forbidden" : "returns.error.rejected", uncertain: false };
}

export function requestFingerprint(request: SaleReturnRequest): string {
  return JSON.stringify(request);
}

export function returnStorage(): Storage | null {
  try {
    return typeof window === "undefined" ? null : window.localStorage;
  } catch {
    return null;
  }
}

function readMap<T>(storage: Storage | null, key: string): Record<string, T> {
  if (!storage) {
    return {};
  }

  try {
    const parsed = JSON.parse(storage.getItem(key) ?? "{}") as unknown;
    return parsed && typeof parsed === "object" ? (parsed as Record<string, T>) : {};
  } catch {
    return {};
  }
}

function writeMap<T>(storage: Storage | null, key: string, value: Record<string, T>) {
  try {
    storage?.setItem(key, JSON.stringify(value));
  } catch {
    // Storage is full or unavailable; the in-memory state still holds the operation.
  }
}

function slot(scope: string, saleId: string): string {
  return `${scope}|${saleId}`;
}

export function loadPending(storage: Storage | null, scope: string, saleId: string): PendingReturn | null {
  return readMap<PendingReturn>(storage, PENDING_KEY)[slot(scope, saleId)] ?? null;
}

export function savePending(storage: Storage | null, scope: string, saleId: string, pending: PendingReturn) {
  const all = readMap<PendingReturn>(storage, PENDING_KEY);
  all[slot(scope, saleId)] = pending;
  writeMap(storage, PENDING_KEY, all);
}

export function clearPending(storage: Storage | null, scope: string, saleId: string) {
  const all = readMap<PendingReturn>(storage, PENDING_KEY);
  delete all[slot(scope, saleId)];
  writeMap(storage, PENDING_KEY, all);
}
