import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { direction, translate, type Locale, type MessageKey } from "./i18n";
import { exceedsAmount, formatAmount, parseAmount, subtractAmount, subtractToZero } from "./money";
import {
  addProduct,
  applyCatalog,
  filterProducts,
  quantityIssue,
  quoteSale,
  removeProduct,
  resolveScan,
  withQuantity,
  type CartLine,
  type CatalogProduct,
  type DiscountKind,
  type PriceKind,
} from "./posCart";
import { cashSaleRequest, finalizeCashSale, getSale, listSales, postSalePayment, type CashSaleRequest, type SalePaymentMethod } from "./salesClient";
import { listCustomers, type CustomerRow } from "./customersClient";
import { SaleReturnPanel } from "./SaleReturnPanel";
import { loadPending, returnBlockReason, returnEligible, returnStorage } from "./saleReturns";

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

type SaleSummary = {
  id: string;
  number: string;
  total: string;
  paidAmount?: string;
  refundedAmount?: string;
  outstandingAmount?: string;
};

type SaleDetail = {
  id: string;
  number: string;
  status?: string;
  subtotal: string;
  total: string;
  paidAmount?: string;
  refundedAmount?: string;
  outstandingAmount?: string;
  paymentState?: "unpaid" | "partial" | "paid";
  lines: {
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
  }[];
  payments: {
    id?: string;
    method: string;
    amount: string;
    receiptNumber?: string | null;
    reference?: string | null;
    notes?: string | null;
  }[];
  customerId?: string | null;
  customerName?: string | null;
};

type ProblemBody = {
  code?: string;
  errors?: Record<string, string[]>;
  available?: string;
  requested?: string;
};

type PosPanelProps = {
  apiBaseUrl: string;
  accessToken: string;
  fetcher: Fetcher;
  locale: Locale;
  administrator?: boolean;
  createKey?: () => string;
  storage?: Storage | null;
  returnTimeoutMs?: number;
  onSalesChanged?: (customerId: string | null) => void;
};

type Attempt = {
  key: string;
  fingerprint: string;
};

export function PosPanel({
  apiBaseUrl,
  accessToken,
  fetcher,
  locale,
  administrator = false,
  createKey = () => crypto.randomUUID(),
  storage = returnStorage(),
  returnTimeoutMs,
  onSalesChanged,
}: PosPanelProps) {
  const text = (key: string) => translate(locale, key);
  const headers = { authorization: `Bearer ${accessToken}` };
  const searchRef = useRef<HTMLInputElement>(null);
  const attempt = useRef<Attempt | null>(null);
  const paymentAttempt = useRef<Attempt | null>(null);
  const busy = useRef(false);
  const [products, setProducts] = useState<CatalogProduct[]>([]);
  const [balances, setBalances] = useState<Record<string, string>>({});
  const [lines, setLines] = useState<CartLine[]>([]);
  const [ticketKind, setTicketKind] = useState<DiscountKind>("none");
  const [ticketInput, setTicketInput] = useState("");
  const [query, setQuery] = useState("");
  const [notice, setNotice] = useState<string | null>(null);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [stockDetail, setStockDetail] = useState<{ available: string; requested: string } | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [receiptNumber, setReceiptNumber] = useState<string | null>(null);
  const [history, setHistory] = useState<SaleSummary[]>([]);
  const [detail, setDetail] = useState<SaleDetail | null>(null);
  const [paymentAmount, setPaymentAmount] = useState("");
  const [collectionMethod, setCollectionMethod] = useState<SalePaymentMethod>("cash");
  const [paymentReference, setPaymentReference] = useState("");
  const [paymentNotes, setPaymentNotes] = useState("");
  const [paymentError, setPaymentError] = useState<string | null>(null);
  const [paymentConfirming, setPaymentConfirming] = useState(false);
  const [paymentSubmitting, setPaymentSubmitting] = useState(false);
  const [loading, setLoading] = useState(true);
  const [historyError, setHistoryError] = useState(false);
  const [customerQuery, setCustomerQuery] = useState("");
  const [customerMatches, setCustomerMatches] = useState<CustomerRow[]>([]);
  const [customer, setCustomer] = useState<CustomerRow | null>(null);
  const [tenderInput, setTenderInput] = useState<string | null>(null);
  const [paymentMethod, setPaymentMethod] = useState<SalePaymentMethod>("cash");
  const [returningSaleId, setReturningSaleId] = useState<string | null>(null);
  const [returnNotice, setReturnNotice] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void reload(() => cancelled);
    return () => {
      cancelled = true;
    };
  }, [accessToken, apiBaseUrl, fetcher]);

  async function reload(cancelled: () => boolean = () => false) {
    const [productResponse, balanceResponse, saleResponse] = await Promise.all([
      fetcher(`${apiBaseUrl}/api/v1/products`, { headers }),
      fetcher(`${apiBaseUrl}/api/v1/stock/balances`, { headers }),
      listSales(fetcher, apiBaseUrl, accessToken),
    ]);
    if (cancelled()) {
      return;
    }

    if (productResponse.ok) {
      setProducts((await productResponse.json()) as CatalogProduct[]);
    }
    if (cancelled()) {
      return;
    }
    if (balanceResponse.ok) {
      const rows = (await balanceResponse.json()) as { productId: string; quantity: string }[];
      setBalances(Object.fromEntries(rows.map((row) => [row.productId, row.quantity])));
    }
    if (cancelled()) {
      return;
    }
    if (saleResponse.ok) {
      setHistory((await saleResponse.json()) as SaleSummary[]);
      setHistoryError(false);
    } else {
      setHistoryError(true);
    }
    setLoading(false);
  }

  const visible = filterProducts(products, query);
  const quoted = quoteSale(lines, ticketKind, ticketInput, administrator);
  const total = quoted.total;
  const tender = customer && total ? tenderDecision(total, tenderInput) : null;
  const ready = total !== null && quoted.lines.length > 0 && quoted.issue === null && (!tender || tender.issue === null);

  function forgetAttempt() {
    attempt.current = null;
  }

  function focusSearch() {
    searchRef.current?.focus();
  }

  function add(product: CatalogProduct) {
    const result = addProduct(lines, product);
    if ("error" in result) {
      setErrorCode(result.error);
      return;
    }

    forgetAttempt();
    setLines(result.lines);
    setQuery("");
    setNotice(null);
    setErrorCode(null);
    setReceiptNumber(null);
    setConfirming(false);
    focusSearch();
  }

  function scan(event: FormEvent) {
    event.preventDefault();
    const result = resolveScan(products, query);
    if (result.kind === "add") {
      add(result.product);
      return;
    }

    if (result.kind === "unknown") {
      setNotice("pos.unknown");
    } else if (result.kind === "inactive") {
      setNotice("sale.product_inactive");
    } else if (result.kind === "ambiguous") {
      setNotice("pos.ambiguous");
    }
    setErrorCode(null);
  }

  function setPriceKind(kind: PriceKind) {
    forgetAttempt();
    setLines((current) => current.map((line) => ({ ...line, priceKind: kind, priceOverride: false })));
    setConfirming(false);
  }

  function setLinePrice(productId: string, kind: PriceKind) {
    forgetAttempt();
    setLines((current) =>
      current.map((line) => (line.productId === productId ? { ...line, priceKind: kind, priceOverride: false } : line)),
    );
    setConfirming(false);
  }

  function setLineDiscount(productId: string, kind: DiscountKind, input: string) {
    forgetAttempt();
    setLines((current) =>
      current.map((line) => (line.productId === productId ? { ...line, discountKind: kind, discountInput: input } : line)),
    );
    setConfirming(false);
  }

  function setOverride(productId: string, unitPrice: string) {
    forgetAttempt();
    setLines((current) =>
      current.map((line) => (line.productId === productId ? { ...line, unitPrice, priceOverride: true } : line)),
    );
    setConfirming(false);
  }

  function changeQuantity(productId: string, quantity: string) {
    forgetAttempt();
    setLines(withQuantity(lines, productId, quantity));
    setConfirming(false);
    setErrorCode(null);
  }

  function step(line: CartLine, directionStep: "up" | "down") {
    const next = directionStep === "up" ? addProduct(lines, lineToProduct(line)) : decrease(lines, line);
    if (!next || "error" in next) {
      setErrorCode(next && "error" in next ? next.error : "quantity.positive");
      return;
    }

    forgetAttempt();
    setLines(next.lines);
    setConfirming(false);
    setErrorCode(null);
  }

  async function submit() {
    if (!ready || total === null || busy.current) {
      return;
    }

    const payload = salePayload(
      quoted.lines,
      total,
      ticketKind,
      ticketInput,
      quoted.discount ?? "0.0000",
      customer?.id ?? null,
      tender,
      paymentMethod,
    );
    if (!payload) {
      return;
    }

    const fingerprint = JSON.stringify(payload);
    const key = attempt.current?.key ?? createKey();
    attempt.current = { key, fingerprint };
    busy.current = true;
    setSubmitting(true);
    setErrorCode(null);
    setStockDetail(null);
    try {
      const response = await finalizeCashSale(fetcher, apiBaseUrl, accessToken, key, payload);
      if (!response.ok) {
        const problem = (await response.json()) as ProblemBody;
        const fieldCode = problem.errors ? Object.values(problem.errors)[0]?.[0] : undefined;
        const code = fieldCode ?? problem.code ?? "request.failed";
        setErrorCode(code);
        if (problem.available && problem.requested) {
          setStockDetail({ available: problem.available, requested: problem.requested });
        }
        if (code === "unitPrice.mismatch") {
          const productResponse = await fetcher(`${apiBaseUrl}/api/v1/products`, { headers });
          if (productResponse.ok) {
            const fresh = (await productResponse.json()) as CatalogProduct[];
            setProducts(fresh);
            setLines((current) => applyCatalog(current, fresh));
          }
        }
        return;
      }

      const sold = (await response.json()) as SaleDetail;
      setReceiptNumber(sold.number);
      setLines([]);
      setCustomer(null);
      setCustomerQuery("");
      setCustomerMatches([]);
      setTenderInput(null);
      setPaymentMethod("cash");
      setConfirming(false);
      setDetail(sold);
      attempt.current = null;
      await reload();
    } catch {
      setErrorCode("pos.network");
    } finally {
      busy.current = false;
      setSubmitting(false);
    }
  }

  function resetPaymentForm() {
    paymentAttempt.current = null;
    setPaymentAmount("");
    setCollectionMethod("cash");
    setPaymentReference("");
    setPaymentNotes("");
    setPaymentError(null);
    setPaymentConfirming(false);
  }

  async function submitPayment() {
    if (!detail?.customerId || !detail.outstandingAmount || paymentSubmitting) {
      return;
    }

    const issue = collectionIssue(detail.outstandingAmount, paymentAmount);
    if (issue || parseAmount(paymentAmount) === null) {
      setPaymentError(issue ?? "payment.invalid");
      return;
    }

    const payload = {
      method: collectionMethod,
      amount: formatAmount(parseAmount(paymentAmount) as bigint),
      ...(paymentReference.trim() ? { reference: paymentReference.trim() } : {}),
      ...(paymentNotes.trim() ? { notes: paymentNotes.trim() } : {}),
    };
    const fingerprint = JSON.stringify(payload);
    const key = paymentAttempt.current?.fingerprint === fingerprint ? paymentAttempt.current.key : createKey();
    paymentAttempt.current = { key, fingerprint };
    setPaymentSubmitting(true);
    setPaymentError(null);
    try {
      const response = await postSalePayment(fetcher, apiBaseUrl, accessToken, detail.id, key, payload);
      if (!response.ok) {
        const problem = (await response.json()) as ProblemBody;
        const fieldCode = problem.errors ? Object.values(problem.errors)[0]?.[0] : undefined;
        setPaymentError(fieldCode ?? problem.code ?? "request.failed");
        return;
      }

      const posted = (await response.json()) as {
        paidAmount: string;
        outstandingAmount: string;
        paymentState: SaleDetail["paymentState"];
        payment: SaleDetail["payments"][number];
      };
      setDetail({
        ...detail,
        paidAmount: posted.paidAmount,
        outstandingAmount: posted.outstandingAmount,
        paymentState: posted.paymentState,
        payments: [...detail.payments, posted.payment],
      });
      resetPaymentForm();
    } catch {
      setPaymentError("pos.network");
    } finally {
      setPaymentSubmitting(false);
    }
  }

  function hasPendingReturn(saleId: string): boolean {
    return loadPending(storage, apiBaseUrl, saleId) !== null;
  }

  function canReturn(sale: SaleDetail): boolean {
    return hasPendingReturn(sale.id) || returnEligible(sale);
  }

  async function openSale(saleId: string, startReturn = false) {
    setHistoryError(false);
    resetPaymentForm();
    setReturningSaleId(null);
    setReturnNotice(null);
    const response = await getSale(fetcher, apiBaseUrl, accessToken, saleId);
    if (!response.ok) {
      setHistoryError(true);
      return;
    }

    const opened = (await response.json()) as SaleDetail;
    setDetail(opened);
    if (startReturn) {
      if (canReturn(opened)) {
        setReturningSaleId(opened.id);
      } else {
        setReturnNotice(returnBlockReason(opened));
      }
    }
  }

  async function refreshSale(saleId: string) {
    const response = await getSale(fetcher, apiBaseUrl, accessToken, saleId);
    if (response.ok) {
      setDetail((await response.json()) as SaleDetail);
    }
  }

  async function afterReturn(saleId: string, customerId: string | null) {
    await Promise.all([refreshSale(saleId), reload()]);
    onSalesChanged?.(customerId);
  }

  useEffect(() => {
    const q = customerQuery.trim();
    if (!q) {
      setCustomerMatches([]);
      return;
    }

    let cancelled = false;
    void listCustomers(fetcher, apiBaseUrl, accessToken, q).then(async (response) => {
      if (!response.ok || cancelled) {
        return;
      }

      const rows = (await response.json()) as CustomerRow[];
      if (!cancelled) {
        setCustomerMatches(rows.filter((row) => row.isActive));
      }
    });
    return () => {
      cancelled = true;
    };
  }, [customerQuery, fetcher, apiBaseUrl, accessToken]);

  return (
    <section className="flex flex-col gap-4" data-testid="pos" dir={direction(locale)}>
      <h2 className="text-xl font-semibold">{text("pos.title")}</h2>
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="flex flex-col gap-3">
          <form className="flex flex-col gap-2" onSubmit={scan}>
            <label className="flex flex-col gap-1 text-sm" htmlFor="pos-search">
              {text("pos.search")}
            </label>
            <input
              id="pos-search"
              ref={searchRef}
              autoFocus
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
                setNotice(null);
              }}
              className="min-h-12 w-full border border-stone-400 bg-white px-3 text-lg"
            />
          </form>
          {loading ? <p>{text("pos.loading")}</p> : null}
          {!loading && products.length === 0 ? <p>{text("pos.emptyProducts")}</p> : null}
          {notice ? <p role="alert">{text(notice)}</p> : null}
          {!loading && query.trim() !== "" && visible.length === 0 ? <p>{text("pos.noMatch")}</p> : null}
          <ul className="flex flex-col gap-2">
            {visible.map((product) => (
              <li key={product.id}>
                <button
                  type="button"
                  disabled={!product.isActive}
                  className="flex min-h-12 w-full items-center justify-between gap-3 border border-stone-400 bg-white px-3 text-start disabled:text-stone-400"
                  onClick={() => add(product)}
                >
                  <span>
                    <span dir="ltr">{product.sku}</span>
                    {" · "}
                    {product.name}
                    {product.isActive ? "" : ` · ${text("purchasing.inactive")}`}
                  </span>
                  <span dir="ltr">{product.unitPrice}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
        <aside className="flex flex-col gap-3 border border-stone-300 bg-white p-3">
          <h3 className="text-lg font-semibold">{text("pos.cart")}</h3>
          {lines.length > 0 ? (
            <div className="flex gap-2">
              <button type="button" className="min-h-12 flex-1 border border-stone-500 bg-white px-3" onClick={() => setPriceKind("retail")}>
                {text("pos.retail")}
              </button>
              <button type="button" className="min-h-12 flex-1 border border-stone-500 bg-white px-3" onClick={() => setPriceKind("wholesale")}>
                {text("pos.wholesale")}
              </button>
            </div>
          ) : null}
          {lines.length === 0 ? <p>{text("pos.emptyCart")}</p> : null}
          <ul className="flex flex-col gap-3">
            {quoted.lines.map((line) => {
              const issue = quantityIssue(line.quantity, line.unitPrice);
              const available = balances[line.productId];
              const short = line.trackStock && available !== undefined && exceedsAmount(line.quantity, available);
              return (
                <li key={line.productId} className="flex flex-col gap-2 border-b border-stone-200 pb-3">
                  <div className="flex items-start justify-between gap-2">
                    <span>
                      {line.name}
                      {" · "}
                      <span dir="ltr">{line.sku}</span>
                    </span>
                    <span dir="ltr">{line.unitPrice}</span>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    <button type="button" className="min-h-12 min-w-12 border border-stone-500 bg-stone-50 text-lg" onClick={() => step(line, "down")}>
                      −
                      <span className="sr-only">{text("pos.decrease")} {line.name}</span>
                    </button>
                    <label className="flex min-w-28 flex-col gap-1 text-sm">
                      <span>{text("pos.quantity")}</span>
                      <input
                        aria-label={`${text("pos.quantity")} ${line.name}`}
                        dir="ltr"
                        inputMode="decimal"
                        value={line.quantity}
                        onChange={(event) => changeQuantity(line.productId, event.target.value)}
                        className="min-h-12 border border-stone-400 px-2 text-lg"
                      />
                    </label>
                    <button type="button" className="min-h-12 min-w-12 border border-stone-500 bg-stone-50 text-lg" onClick={() => step(line, "up")}>
                      +
                      <span className="sr-only">{text("pos.increase")} {line.name}</span>
                    </button>
                    <button
                      type="button"
                      className="min-h-12 border border-stone-500 bg-stone-50 px-3"
                      onClick={() => {
                        forgetAttempt();
                        setLines(removeProduct(lines, line.productId));
                        setConfirming(false);
                      }}
                    >
                      {text("pos.remove")} {line.name}
                    </button>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <button type="button" className="min-h-12 border border-stone-500 bg-white px-3" onClick={() => setLinePrice(line.productId, "retail")}>
                      {text("pos.retail")} {line.name}
                    </button>
                    <button type="button" className="min-h-12 border border-stone-500 bg-white px-3" onClick={() => setLinePrice(line.productId, "wholesale")}>
                      {text("pos.wholesale")} {line.name}
                    </button>
                  </div>
                  <label className="flex flex-col gap-1 text-sm">
                    <span>{text("pos.discount")}</span>
                    <select
                      aria-label={`${text("pos.discount")} ${line.name}`}
                      className="min-h-12 border border-stone-400 bg-white px-2"
                      value={line.discountKind}
                      onChange={(event) => setLineDiscount(line.productId, event.target.value as DiscountKind, line.discountInput)}
                    >
                      <option value="none">{text("pos.noDiscount")}</option>
                      <option value="amount">{text("pos.amount")}</option>
                      <option value="percent">{text("pos.percent")}</option>
                    </select>
                  </label>
                  {line.discountKind !== "none" ? (
                    <input
                      aria-label={`${text("pos.discountValue")} ${line.name}`}
                      dir="ltr"
                      inputMode="decimal"
                      value={line.discountInput}
                      onChange={(event) => setLineDiscount(line.productId, line.discountKind, event.target.value)}
                      className="min-h-12 border border-stone-400 px-2 text-lg"
                    />
                  ) : null}
                  {administrator ? (
                    <label className="flex flex-col gap-1 text-sm">
                      <span>{text("pos.overridePrice")}</span>
                      <input
                        aria-label={`${text("pos.overridePrice")} ${line.name}`}
                        dir="ltr"
                        inputMode="decimal"
                        value={line.unitPrice}
                        onChange={(event) => setOverride(line.productId, event.target.value)}
                        className="min-h-12 border border-stone-400 px-2 text-lg"
                      />
                    </label>
                  ) : null}
                  <p>
                    {text("pos.lineTotal")} <LtrValue>{line.lineTotal ?? "—"}</LtrValue>
                  </p>
                  {line.trackStock && available !== undefined ? (
                    <p>
                      {text("pos.available")} <LtrValue>{available}</LtrValue>
                    </p>
                  ) : null}
                  {issue ? <p role="alert">{text(issue)}</p> : null}
                  {short ? <p role="alert">{text("stock.insufficient")}</p> : null}
                </li>
              );
            })}
          </ul>
          <p>
            {text("pos.subtotal")} <LtrValue>{quoted.subtotal ?? "—"}</LtrValue>
          </p>
          <label className="flex flex-col gap-1 text-sm">
            <span>{text("pos.ticketDiscount")}</span>
            <select
              aria-label={text("pos.ticketDiscount")}
              className="min-h-12 border border-stone-400 bg-white px-2"
              value={ticketKind}
              onChange={(event) => {
                forgetAttempt();
                setTicketKind(event.target.value as DiscountKind);
                setConfirming(false);
              }}
            >
              <option value="none">{text("pos.noDiscount")}</option>
              <option value="amount">{text("pos.amount")}</option>
              <option value="percent">{text("pos.percent")}</option>
            </select>
          </label>
          {ticketKind !== "none" ? (
            <input
              aria-label={text("pos.ticketDiscountValue")}
              dir="ltr"
              inputMode="decimal"
              value={ticketInput}
              onChange={(event) => {
                forgetAttempt();
                setTicketInput(event.target.value);
                setConfirming(false);
              }}
              className="min-h-12 border border-stone-400 px-2 text-lg"
            />
          ) : null}
          <p>
            {text("pos.discount")} <LtrValue>{quoted.discount ?? "—"}</LtrValue>
          </p>
          <p className="text-lg font-semibold" data-testid="pos-total">
            {text("pos.total")} <LtrValue>{total ?? "—"}</LtrValue>
          </p>
          <div className="flex flex-col gap-2">
            <label className="flex flex-col gap-1 text-sm" htmlFor="pos-customer">
              {text("pos.customerOptional")}
            </label>
            {customer ? (
              <p>
                {text("pos.customer")} {customer.name}
                {customer.phone ? (
                  <>
                    {" · "}
                    <LtrValue>{customer.phone}</LtrValue>
                  </>
                ) : null}
                <button
                  type="button"
                  className="ms-2 border border-stone-400 bg-white px-2"
                  onClick={() => {
                    forgetAttempt();
                    setCustomer(null);
                    setCustomerQuery("");
                    setCustomerMatches([]);
                    setTenderInput(null);
                    setPaymentMethod("cash");
                    setConfirming(false);
                  }}
                >
                  {text("pos.clearCustomer")}
                </button>
              </p>
            ) : (
              <>
                <input
                  id="pos-customer"
                  value={customerQuery}
                  onChange={(event) => {
                    forgetAttempt();
                    setCustomerQuery(event.target.value);
                    setConfirming(false);
                  }}
                  className="min-h-12 w-full border border-stone-400 bg-white px-3"
                />
                {customerMatches.length > 0 ? (
                  <ul className="flex flex-col gap-1">
                    {customerMatches.map((match) => (
                      <li key={match.id}>
                        <button
                          type="button"
                          className="border border-stone-400 bg-white px-2 py-1 text-start"
                          onClick={() => {
                            forgetAttempt();
                            setCustomer(match);
                            setCustomerQuery("");
                            setCustomerMatches([]);
                            setTenderInput(null);
                            setPaymentMethod("cash");
                            setConfirming(false);
                          }}
                        >
                          {match.name}
                          {" · "}
                          <LtrValue>{match.phone || "—"}</LtrValue>
                          {" · "}
                          {text(match.type === "business" ? "customers.business" : "customers.individual")}
                        </button>
                      </li>
                    ))}
                  </ul>
                ) : null}
              </>
            )}
          </div>
          {customer && total && tender ? (
            <div className="flex flex-col gap-2">
              <label className="flex flex-col gap-1 text-sm">
                {text("pos.method")}
                <select
                  aria-label={text("pos.method")}
                  value={paymentMethod}
                  onChange={(event) => {
                    forgetAttempt();
                    setPaymentMethod(event.target.value as SalePaymentMethod);
                    setConfirming(false);
                    setErrorCode(null);
                  }}
                  className="min-h-12 border border-stone-400 bg-white px-2"
                >
                  <option value="cash">{text("pos.cash")}</option>
                  <option value="transfer">{text("pos.transfer")}</option>
                  <option value="card">{text("pos.card")}</option>
                  <option value="other">{text("pos.other")}</option>
                </select>
              </label>
              <label className="flex flex-col gap-1 text-sm">
                {text("pos.initialPayment")}
                <input
                  aria-label={text("pos.initialPayment")}
                  dir="ltr"
                  inputMode="decimal"
                  value={tenderInput ?? total}
                  onChange={(event) => {
                    forgetAttempt();
                    setTenderInput(event.target.value);
                    setConfirming(false);
                    setErrorCode(null);
                  }}
                  className="min-h-12 border border-stone-400 px-2 text-lg"
                />
              </label>
              <p data-testid="pos-outstanding">
                {text("pos.outstanding")} <LtrValue>{tender.outstanding ?? "—"}</LtrValue>
              </p>
            </div>
          ) : (
            <p>
              {text("pos.cash")} <LtrValue>{total ?? "—"}</LtrValue>
            </p>
          )}
          {receiptNumber ? (
            <p role="status">
              {text("pos.recorded")} <LtrValue>{receiptNumber}</LtrValue>
            </p>
          ) : null}
          {quoted.issue && !quoted.issue.startsWith("quantity.") && quoted.issue !== "line.scale" ? (
            <p role="alert">{text(quoted.issue)}</p>
          ) : null}
          {tender?.issue ? <p role="alert">{text(tender.issue)}</p> : null}
          {errorCode ? (
            <p role="alert">
              {text(errorCode)}
              {stockDetail ? (
                <>
                  {" "}
                  <LtrValue>
                    {stockDetail.available} / {stockDetail.requested}
                  </LtrValue>
                </>
              ) : null}
            </p>
          ) : null}
          {confirming ? (
            <div className="flex flex-col gap-2 border border-stone-400 p-3" role="region" aria-label={text("pos.confirmTitle")}>
              <h3 className="font-semibold">{text("pos.confirmTitle")}</h3>
              <p>
                {text("pos.total")} <LtrValue>{total ?? "—"}</LtrValue>
              </p>
              <p>{customer ? customer.name : text("pos.walkIn")}</p>
              {customer && tender ? (
                <p>
                  {text("pos.outstanding")} <LtrValue>{tender.outstanding ?? "—"}</LtrValue>
                </p>
              ) : null}
              <button
                type="button"
                disabled={!ready || submitting}
                className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60"
                onClick={() => void submit()}
              >
                {submitting ? text("pos.submitting") : errorCode ? text("pos.retry") : text("pos.confirm")}
              </button>
              <button
                type="button"
                className="min-h-12 border border-stone-500 bg-white px-3"
                onClick={() => setConfirming(false)}
              >
                {text("pos.back")}
              </button>
            </div>
          ) : (
            <button
              type="button"
              disabled={!ready || submitting}
              className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60"
              onClick={() => setConfirming(true)}
            >
              {text("pos.checkout")}
            </button>
          )}
        </aside>
      </div>
      <section className="flex flex-col gap-2">
        <h3 className="text-lg font-semibold">{text("pos.history")}</h3>
        {historyError ? <p role="alert">{text("pos.historyError")}</p> : null}
        {history.length === 0 && !historyError ? <p>{text("pos.emptyHistory")}</p> : null}
        <ul className="flex flex-col gap-2">
          {history.map((sale) => (
            <li key={sale.id} className="flex flex-wrap gap-2" data-testid={`sale-row-${sale.id}`}>
              <button type="button" className="min-h-12 border border-stone-400 bg-white px-3" onClick={() => void openSale(sale.id)}>
                <LtrValue>{sale.number}</LtrValue>
                {" · "}
                <LtrValue>{sale.total}</LtrValue>
              </button>
              <button type="button" className="min-h-12 border border-stone-500 bg-stone-50 px-3" onClick={() => void openSale(sale.id, true)}>
                {text("returns.action")}
              </button>
            </li>
          ))}
        </ul>
        {detail ? (
          <article className="flex flex-col gap-2 border border-stone-300 bg-white p-3" aria-label={detail.number}>
            <h4 className="font-semibold">
              <LtrValue>{detail.number}</LtrValue>
            </h4>
            {detail.customerName ? <p>{detail.customerName}</p> : null}
            {returnNotice ? <p role="alert">{text(returnNotice)}</p> : null}
            <ul className="flex flex-col gap-1">
              {detail.lines.map((line) => (
                <li key={line.id}>
                  {line.productName}
                  {" · "}
                  <LtrValue>{line.quantity}</LtrValue>
                  {" × "}
                  <LtrValue>{line.unitPrice}</LtrValue>
                  {" = "}
                  <LtrValue>{line.lineTotal}</LtrValue>
                </li>
              ))}
            </ul>
            <p>
              {text("pos.subtotal")} <LtrValue>{detail.subtotal}</LtrValue>
            </p>
            <p>
              {text("pos.total")} <LtrValue>{detail.total}</LtrValue>
            </p>
            {detail.paidAmount ? (
              <p>
                {text("pos.paid")} <LtrValue>{detail.paidAmount}</LtrValue>
              </p>
            ) : null}
            {detail.outstandingAmount ? (
              <p>
                {text("pos.outstanding")} <LtrValue>{detail.outstandingAmount}</LtrValue>
                {detail.paymentState ? ` · ${text(paymentStateKey(detail.paymentState))}` : null}
              </p>
            ) : null}
            {detail.payments.map((payment, index) => (
              <p key={payment.id ?? `${payment.method}-${index}`}>
                {payment.receiptNumber ? (
                  <>
                    {text("pos.receipt")} <LtrValue>{payment.receiptNumber}</LtrValue>
                    {" · "}
                  </>
                ) : null}
                {paymentLabel(payment.method, text)} <LtrValue>{payment.amount}</LtrValue>
                {payment.reference ? (
                  <>
                    {" · "}
                    <LtrValue>{payment.reference}</LtrValue>
                  </>
                ) : null}
              </p>
            ))}
            {detail.customerId && detail.outstandingAmount && parseAmount(detail.outstandingAmount) !== null && (parseAmount(detail.outstandingAmount) as bigint) > 0n ? (
              <form
                className="flex flex-col gap-2 border border-stone-400 p-3"
                onSubmit={(event) => {
                  event.preventDefault();
                  setPaymentConfirming(true);
                }}
              >
                <h4 className="font-semibold">{text("pos.pay")}</h4>
                <label className="flex flex-col gap-1 text-sm">
                  {text("pos.pay")}
                  <select
                    aria-label={text("pos.pay")}
                    value={collectionMethod}
                    onChange={(event) => {
                      paymentAttempt.current = null;
                      setCollectionMethod(event.target.value as SalePaymentMethod);
                      setPaymentError(null);
                      setPaymentConfirming(false);
                    }}
                    className="min-h-12 border border-stone-400 bg-white px-2"
                  >
                    <option value="cash">{text("pos.cash")}</option>
                    <option value="transfer">{text("pos.transfer")}</option>
                    <option value="card">{text("pos.card")}</option>
                    <option value="other">{text("pos.other")}</option>
                  </select>
                </label>
                <label className="flex flex-col gap-1 text-sm">
                  {text("pos.initialPayment")}
                  <input
                    aria-label={text("pos.recordPayment")}
                    dir="ltr"
                    inputMode="decimal"
                    value={paymentAmount}
                    onChange={(event) => {
                      paymentAttempt.current = null;
                      setPaymentAmount(event.target.value);
                      setPaymentError(null);
                      setPaymentConfirming(false);
                    }}
                    className="min-h-12 border border-stone-400 px-2"
                  />
                </label>
                <label className="flex flex-col gap-1 text-sm">
                  {text("pos.reference")}
                  <input
                    aria-label={text("pos.reference")}
                    dir="ltr"
                    value={paymentReference}
                    onChange={(event) => {
                      paymentAttempt.current = null;
                      setPaymentReference(event.target.value);
                      setPaymentError(null);
                      setPaymentConfirming(false);
                    }}
                    className="min-h-12 border border-stone-400 px-2"
                  />
                </label>
                <label className="flex flex-col gap-1 text-sm">
                  {text("pos.paymentNotes")}
                  <input
                    aria-label={text("pos.paymentNotes")}
                    value={paymentNotes}
                    onChange={(event) => {
                      paymentAttempt.current = null;
                      setPaymentNotes(event.target.value);
                      setPaymentError(null);
                      setPaymentConfirming(false);
                    }}
                    className="min-h-12 border border-stone-400 px-2"
                  />
                </label>
                {paymentError ? <p role="alert">{text(paymentError)}</p> : null}
                {!paymentError && paymentAmount.trim() !== "" && collectionIssue(detail.outstandingAmount, paymentAmount) ? (
                  <p role="alert">{text(collectionIssue(detail.outstandingAmount, paymentAmount) as MessageKey)}</p>
                ) : null}
                {paymentConfirming ? (
                  <button
                    type="button"
                    disabled={paymentSubmitting || collectionIssue(detail.outstandingAmount, paymentAmount) !== null}
                    className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60"
                    onClick={() => void submitPayment()}
                  >
                    {paymentSubmitting ? text("pos.submitting") : text("pos.payConfirm")}
                  </button>
                ) : (
                  <button
                    type="submit"
                    disabled={collectionIssue(detail.outstandingAmount, paymentAmount) !== null}
                    className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60"
                  >
                    {text("pos.recordPayment")}
                  </button>
                )}
              </form>
            ) : null}
            {returningSaleId === detail.id ? (
              <SaleReturnPanel
                key={detail.id}
                sale={detail}
                apiBaseUrl={apiBaseUrl}
                accessToken={accessToken}
                fetcher={fetcher}
                locale={locale}
                createKey={createKey}
                storage={storage}
                timeoutMs={returnTimeoutMs}
                onReturned={() => afterReturn(detail.id, detail.customerId ?? null)}
                onRejected={() => refreshSale(detail.id)}
                onClose={() => setReturningSaleId(null)}
              />
            ) : canReturn(detail) ? (
              <button type="button" className="min-h-12 w-fit border border-stone-500 bg-stone-50 px-3" onClick={() => setReturningSaleId(detail.id)}>
                {text("returns.action")}
              </button>
            ) : null}
          </article>
        ) : null}
      </section>
    </section>
  );
}

function lineToProduct(line: CartLine): CatalogProduct {
  return {
    id: line.productId,
    sku: line.sku,
    name: line.name,
    unit: line.unit,
    unitPrice: line.retailPrice,
    wholesaleUnitPrice: line.wholesalePrice,
    minimumUnitPrice: line.minimumPrice,
    trackStock: line.trackStock,
    isActive: true,
  };
}

function decrease(lines: CartLine[], line: CartLine): { lines: CartLine[] } | { error: string } | null {
  const next = subtractAmount(line.quantity, "1.0000");
  if (next === null) {
    return null;
  }

  return { lines: withQuantity(lines, line.productId, next) };
}

function collectionIssue(outstanding: string, amountText: string): MessageKey | null {
  if (amountText.trim() === "") {
    return "payment.required";
  }

  const due = parseAmount(outstanding);
  const paid = parseAmount(amountText);
  if (due === null || paid === null) {
    return "payment.invalid";
  }

  if (paid <= 0n) {
    return "payment.positive";
  }

  if (paid > due) {
    return "payment.exceeds";
  }

  return null;
}

function tenderDecision(total: string, tenderInput: string | null): {
  issue: MessageKey | null;
  outstanding: string | null;
  amount: string | null;
} {
  if (tenderInput === null) {
    return { issue: null, outstanding: formatAmount(0n), amount: total };
  }

  if (tenderInput.trim() === "") {
    return { issue: null, outstanding: total, amount: null };
  }

  const paid = parseAmount(tenderInput);
  if (paid === null) {
    return { issue: "payment.invalid", outstanding: null, amount: null };
  }

  if (paid <= 0n) {
    return { issue: "payment.positive", outstanding: null, amount: null };
  }

  const outstanding = subtractToZero(total, formatAmount(paid));
  if (outstanding === null) {
    return { issue: "payment.total", outstanding: null, amount: null };
  }

  return { issue: null, outstanding, amount: formatAmount(paid) };
}

function paymentStateKey(state: "unpaid" | "partial" | "paid"): MessageKey {
  if (state === "unpaid") {
    return "paymentState.unpaid";
  }

  if (state === "partial") {
    return "paymentState.partial";
  }

  return "paymentState.paid";
}

function paymentLabel(method: string, text: (key: MessageKey) => string): string {
  if (method === "cash") {
    return text("pos.cash");
  }

  if (method === "transfer") {
    return text("pos.transfer");
  }

  if (method === "card") {
    return text("pos.card");
  }

  if (method === "other") {
    return text("pos.other");
  }

  return method;
}

function salePayload(
  lines: CartLine[],
  total: string,
  kind: DiscountKind,
  input: string,
  discount: string,
  customerId: string | null,
  tender: { amount: string | null } | null,
  method: SalePaymentMethod,
): CashSaleRequest | null {
  const payloadLines = [];
  for (const line of lines) {
    const quantity = parseAmount(line.quantity);
    const price = parseAmount(line.unitPrice);
    const rate = line.discountKind === "percent" ? parseAmount(line.discountInput) : null;
    if (quantity === null || price === null || line.lineTotal === null || line.lineDiscount === null) {
      return null;
    }

    if (line.discountKind === "percent" && rate === null) {
      return null;
    }

    payloadLines.push({
      productId: line.productId,
      quantity: formatAmount(quantity),
      priceKind: line.priceKind,
      unitPrice: formatAmount(price),
      lineDiscount: line.lineDiscount,
      discountKind: line.discountKind,
      discountRate: rate === null ? undefined : formatAmount(rate),
      lineTotal: line.lineTotal,
    });
  }

  const ticketRate = kind === "percent" ? parseAmount(input) : null;
  if (kind === "percent" && ticketRate === null) {
    return null;
  }

  return cashSaleRequest(payloadLines, total, {
    amount: discount,
    kind,
    rate: ticketRate === null ? undefined : formatAmount(ticketRate),
  }, customerId ?? undefined, customerId ? (tender?.amount ? { method, amount: tender.amount } : null) : undefined);
}

function LtrValue({ children }: { children: ReactNode }) {
  return (
    <span dir="ltr" className="inline-block">
      {children}
    </span>
  );
}
