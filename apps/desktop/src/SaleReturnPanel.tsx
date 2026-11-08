import { useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";
import { translate, type Locale } from "./i18n";
import { formatAmount, parseAmount } from "./money";
import {
  REASON_MAX_LENGTH,
  REFUND_METHODS,
  clearPending,
  estimateLineAmount,
  loadPending,
  reasonIssue,
  refundQuote,
  returnFailure,
  returnQuantityIssue,
  returnRows,
  savePending,
  tenderAllocation,
  type PendingReturn,
  type ReturnFailure,
  type ReturnableSale,
} from "./saleReturns";
import { postSaleReturn, type RefundMethod, type SaleReturnRequest, type SaleReturnResult } from "./salesClient";

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

type SaleReturnPanelProps = {
  sale: ReturnableSale;
  apiBaseUrl: string;
  accessToken: string;
  fetcher: Fetcher;
  locale: Locale;
  createKey: () => string;
  storage: Storage | null;
  timeoutMs?: number;
  onReturned: (result: SaleReturnResult) => Promise<void> | void;
  onRejected: () => Promise<void> | void;
  onClose: () => void;
};

const emptyTenders: Record<RefundMethod, string> = { cash: "", transfer: "", card: "" };

export function SaleReturnPanel({
  sale,
  apiBaseUrl,
  accessToken,
  fetcher,
  locale,
  createKey,
  storage,
  timeoutMs = 30_000,
  onReturned,
  onRejected,
  onClose,
}: SaleReturnPanelProps) {
  const text = (key: string) => translate(locale, key);
  const restored = useRef<PendingReturn | null | undefined>(undefined);
  if (restored.current === undefined) {
    restored.current = loadPending(storage, apiBaseUrl, sale.id);
  }
  const initial = restored.current;
  const busy = useRef(false);
  const [pending, setPending] = useState<PendingReturn | null>(initial);
  const [selected, setSelected] = useState<Record<string, string>>(() =>
    initial ? Object.fromEntries(initial.request.lines.map((line) => [line.saleLineId, line.quantity])) : {},
  );
  const [tenders, setTenders] = useState<Record<RefundMethod, string> | null>(() =>
    initial && initial.request.refunds.length > 0
      ? { ...emptyTenders, ...Object.fromEntries(initial.request.refunds.map((refund) => [refund.method, refund.amount])) }
      : null,
  );
  const [reason, setReason] = useState(initial?.request.reason ?? "");
  const [reasonTouched, setReasonTouched] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<ReturnFailure | null>(
    initial ? { message: "returns.pendingNotice", uncertain: true } : null,
  );
  const [result, setResult] = useState<SaleReturnResult | null>(null);
  const [incomplete, setIncomplete] = useState(false);

  const { rows, authoritative } = returnRows(sale);
  const walkIn = !sale.customerId;
  const locked = pending !== null || submitting;

  const chosen = rows.filter((row) => selected[row.id] !== undefined);
  const lineIssues = Object.fromEntries(chosen.map((row) => [row.id, returnQuantityIssue(selected[row.id], row.cap)]));
  const estimates = chosen.map((row) => {
    const quantity = parseAmount(selected[row.id]);
    return lineIssues[row.id] || quantity === null ? null : estimateLineAmount(row.basisValue, row.basisQuantity, quantity);
  });
  const returnAmount = chosen.length > 0 && estimates.every((value) => value !== null)
    ? estimates.reduce<bigint>((sum, value) => sum + (value as bigint), 0n)
    : null;
  const quote = !authoritative || returnAmount === null ? null : refundQuote(returnAmount, sale);
  const required = quote?.requiredRefund ?? 0n;
  const effectiveTenders = tenders ?? { ...emptyTenders, cash: required > 0n ? formatAmount(required) : "" };
  const allocation = tenderAllocation(required, effectiveTenders);
  const reasonProblem = reasonIssue(reason);
  const ready =
    returnAmount !== null &&
    quote !== null &&
    !quote.exceedsCollected &&
    allocation.issue === null &&
    reasonProblem === null;

  const request: SaleReturnRequest | null = ready
    ? {
        reason: reason.trim(),
        lines: chosen.map((row) => ({ saleLineId: row.id, quantity: formatAmount(parseAmount(selected[row.id]) as bigint) })),
        refunds: allocation.refunds,
      }
    : null;

  function edit() {
    setConfirming(false);
    if (failure && !failure.uncertain) {
      setFailure(null);
    }
  }

  function toggle(rowId: string, cap: bigint, checked: boolean) {
    setSelected((current) => {
      const next = { ...current };
      if (checked) {
        next[rowId] = formatAmount(cap);
      } else {
        delete next[rowId];
      }
      return next;
    });
    edit();
  }

  function changeQuantity(rowId: string, value: string) {
    setSelected((current) => ({ ...current, [rowId]: value }));
    edit();
  }

  function changeTender(method: RefundMethod, value: string) {
    setTenders({ ...effectiveTenders, [method]: value });
    edit();
  }

  function review(event: FormEvent) {
    event.preventDefault();
    setReasonTouched(true);
    setIncomplete(chosen.length === 0);
    if (ready) {
      setConfirming(true);
    }
  }

  async function submit() {
    if (busy.current) {
      return;
    }

    const active = pending ?? (request ? { key: createKey(), request } : null);
    if (!active) {
      return;
    }

    if (!pending) {
      savePending(storage, apiBaseUrl, sale.id, active);
      setPending(active);
    }

    busy.current = true;
    setSubmitting(true);
    setFailure(null);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await postSaleReturn(fetcher, apiBaseUrl, accessToken, sale.id, active.key, active.request, controller.signal);
      if (!response.ok) {
        const failed = await returnFailure(response);
        setFailure(failed);
        if (!failed.uncertain) {
          clearPending(storage, apiBaseUrl, sale.id);
          setPending(null);
          setConfirming(false);
          await onRejected();
        }
        return;
      }

      const posted = (await response.json()) as SaleReturnResult;
      clearPending(storage, apiBaseUrl, sale.id);
      setPending(null);
      setResult(posted);
      setSelected({});
      setTenders(null);
      setReason("");
      setReasonTouched(false);
      setConfirming(false);
      await onReturned(posted);
    } catch {
      setFailure({ message: "returns.error.network", uncertain: true });
    } finally {
      clearTimeout(timer);
      busy.current = false;
      setSubmitting(false);
    }
  }

  function escape(event: KeyboardEvent) {
    if (event.key === "Escape" && confirming && !locked) {
      event.stopPropagation();
      setConfirming(false);
    }
  }

  if (result) {
    return (
      <ReturnResult
        result={result}
        sale={sale}
        walkIn={walkIn}
        text={text}
        onClose={onClose}
      />
    );
  }

  const summary = pending?.request ?? request;
  const productName = (saleLineId: string) => sale.lines.find((line) => line.id === saleLineId)?.productName ?? saleLineId;

  return (
    <section
      className="flex flex-col gap-3 border border-stone-400 bg-stone-50 p-3"
      aria-label={text("returns.title")}
      data-testid="sale-return"
      onKeyDown={escape}
    >
      <h4 className="text-lg font-semibold">
        {text("returns.title")} <LtrValue>{sale.number}</LtrValue>
      </h4>
      <p>{walkIn ? text("returns.walkIn") : `${text("pos.customer")} ${sale.customerName ?? ""}`}</p>
      {!authoritative ? <p role="alert">{text("returns.unavailable")}</p> : null}
      <form className="flex flex-col gap-3" onSubmit={review} noValidate>
        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.select")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.product")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.sold")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.previous")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.returnedAmount")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.remaining")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.unitPrice")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.remainingValue")}</th>
                <th className="border border-stone-300 px-2 py-1 text-start">{text("returns.quantity")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => {
                const checked = selected[row.id] !== undefined;
                const issue = checked ? lineIssues[row.id] : null;
                const exhausted = row.cap <= 0n;
                return (
                  <tr key={row.id} data-testid={`return-line-${row.id}`}>
                    <td className="border border-stone-300 px-2 py-1">
                      <input
                        type="checkbox"
                        className="size-5"
                        aria-label={`${text("returns.select")} ${row.productName}`}
                        checked={checked}
                        disabled={locked || exhausted}
                        autoFocus={index === 0 && !locked}
                        onChange={(event) => toggle(row.id, row.cap, event.target.checked)}
                      />
                    </td>
                    <td className="border border-stone-300 px-2 py-1">
                      {row.productName}
                      {" · "}
                      <LtrValue>{row.productSku}</LtrValue>
                      {row.tracksStock !== null ? (
                        <span className="block text-xs text-stone-600">
                          {text(row.tracksStock ? "returns.tracked" : "returns.untracked")}
                        </span>
                      ) : null}
                    </td>
                    <td className="border border-stone-300 px-2 py-1" dir="ltr">{row.quantity}</td>
                    <td className="border border-stone-300 px-2 py-1" data-testid={`return-previous-${row.id}`}>
                      {row.previous === null ? "—" : <LtrValue>{row.previous}</LtrValue>}
                    </td>
                    <td className="border border-stone-300 px-2 py-1" data-testid={`return-returned-amount-${row.id}`}>
                      {row.returnedAmount === null ? "—" : <LtrValue>{row.returnedAmount}</LtrValue>}
                    </td>
                    <td className="border border-stone-300 px-2 py-1" data-testid={`return-remaining-${row.id}`}>
                      {row.remainingQuantity === null ? "—" : <LtrValue>{row.remainingQuantity}</LtrValue>}
                    </td>
                    <td className="border border-stone-300 px-2 py-1" dir="ltr">{row.unitPrice}</td>
                    <td className="border border-stone-300 px-2 py-1" data-testid={`return-remaining-value-${row.id}`}>
                      {row.remainingValue === null ? "—" : <LtrValue>{row.remainingValue}</LtrValue>}
                    </td>
                    <td className="border border-stone-300 px-2 py-1">
                      <input
                        aria-label={`${text("returns.quantity")} ${row.productName}`}
                        dir="ltr"
                        inputMode="decimal"
                        value={selected[row.id] ?? ""}
                        disabled={locked || exhausted}
                        onChange={(event) => {
                          if (event.target.value === "") {
                            toggle(row.id, row.cap, false);
                          } else {
                            changeQuantity(row.id, event.target.value);
                          }
                        }}
                        className="min-h-10 w-28 border border-stone-400 bg-white px-2"
                      />
                      {issue ? <p role="alert">{text(issue)}</p> : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {incomplete && chosen.length === 0 ? <p role="alert">{text("returns.selectLine")}</p> : null}
        {quote ? (
          <div className="flex flex-col gap-1" data-testid="return-preview">
            <p>
              {text("returns.estimate")} <LtrValue>{formatAmount(returnAmount as bigint)}</LtrValue>
            </p>
            {!walkIn ? (
              <p>
                {text("returns.outstandingBefore")} <LtrValue>{formatAmount(quote.outstanding)}</LtrValue>
              </p>
            ) : null}
            <p className="font-semibold" data-testid="return-required-refund">
              {text(walkIn ? "returns.directRefund" : "returns.requiredRefund")} <LtrValue>{formatAmount(quote.requiredRefund)}</LtrValue>
            </p>
            {quote.collected !== null ? (
              <p>
                {text("returns.collected")} <LtrValue>{formatAmount(quote.collected)}</LtrValue>
              </p>
            ) : null}
            {!walkIn ? (
              <p data-testid="return-net-reduction">
                {text("returns.netReduction")} <LtrValue>{formatAmount(quote.netReduction)}</LtrValue>
              </p>
            ) : null}
            <p className="text-xs text-stone-600">{text("returns.serverNote")}</p>
            {quote.exceedsCollected ? <p role="alert">{text("returns.exceedsCollected")}</p> : null}
          </div>
        ) : null}
        {quote && quote.requiredRefund === 0n ? <p data-testid="return-no-refund">{text("returns.noRefund")}</p> : null}
        {quote && quote.requiredRefund > 0n ? (
          <fieldset className="flex flex-col gap-2 border border-stone-300 p-2" data-testid="return-tenders">
            <legend className="px-1 font-semibold">{text("returns.tenderTitle")}</legend>
            <div className="grid gap-2 sm:grid-cols-3">
              {REFUND_METHODS.map((method) => (
                <label key={method} className="flex flex-col gap-1 text-sm">
                  {text(`pos.${method}`)}
                  <input
                    aria-label={`${text("returns.tenderLabel")} ${text(`pos.${method}`)}`}
                    dir="ltr"
                    inputMode="decimal"
                    value={effectiveTenders[method]}
                    disabled={locked}
                    onChange={(event) => changeTender(method, event.target.value)}
                    className="min-h-10 border border-stone-400 bg-white px-2"
                  />
                </label>
              ))}
            </div>
            <p data-testid="return-to-allocate">
              {text("returns.toAllocate")} <LtrValue>{formatAmount(allocation.remaining)}</LtrValue>
            </p>
            {allocation.issue ? <p role="alert">{text(allocation.issue)}</p> : null}
          </fieldset>
        ) : null}
        <label className="flex flex-col gap-1 text-sm">
          {text("returns.reason")}
          <input
            aria-label={text("returns.reason")}
            value={reason}
            disabled={locked}
            onChange={(event) => {
              setReason(event.target.value);
              setReasonTouched(true);
              edit();
            }}
            className="min-h-10 border border-stone-400 bg-white px-2"
          />
          <span className="text-xs text-stone-600" dir="ltr">
            {reason.trim().length}/{REASON_MAX_LENGTH}
          </span>
        </label>
        {reasonTouched && reasonProblem ? <p role="alert">{text(reasonProblem)}</p> : null}
        {failure ? <p role="alert" data-testid="return-error">{text(failure.message)}</p> : null}
        {!confirming && !pending ? (
          <div className="flex flex-wrap gap-2">
            {authoritative ? (
              <button type="submit" className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60" disabled={submitting}>
                {text("returns.review")}
              </button>
            ) : null}
            <button type="button" className="min-h-12 border border-stone-500 bg-white px-3" onClick={onClose}>
              {text("returns.close")}
            </button>
          </div>
        ) : null}
      </form>
      {(confirming || pending) && summary ? (
        <div className="flex flex-col gap-2 border border-stone-500 bg-white p-3" role="region" aria-label={text("returns.confirmTitle")}>
          <h5 className="font-semibold">{text("returns.confirmTitle")}</h5>
          <p>
            {text("returns.sale")} <LtrValue>{sale.number}</LtrValue>
            {" · "}
            {walkIn ? text("pos.walkIn") : sale.customerName}
          </p>
          <ul className="flex flex-col gap-1">
            {summary.lines.map((line) => (
              <li key={line.saleLineId}>
                {productName(line.saleLineId)} × <LtrValue>{line.quantity}</LtrValue>
              </li>
            ))}
          </ul>
          {quote ? (
            <>
              <p>
                {text("returns.estimate")} <LtrValue>{formatAmount(returnAmount as bigint)}</LtrValue>
              </p>
              <p>
                {text(walkIn ? "returns.directRefund" : "returns.requiredRefund")} <LtrValue>{formatAmount(quote.requiredRefund)}</LtrValue>
              </p>
            </>
          ) : null}
          {summary.refunds.map((refund) => (
            <p key={refund.method}>
              {text(`pos.${refund.method}`)} <LtrValue>{refund.amount}</LtrValue>
            </p>
          ))}
          {quote && !walkIn ? (
            <p>
              {text("returns.netReduction")} <LtrValue>{formatAmount(quote.netReduction)}</LtrValue>
            </p>
          ) : null}
          <p>
            {text("returns.reason")} : {summary.reason}
          </p>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              autoFocus
              disabled={submitting}
              className="min-h-12 border border-stone-700 bg-stone-800 px-3 text-white disabled:opacity-60"
              onClick={() => void submit()}
            >
              {submitting ? text("returns.submitting") : pending ? text("returns.retry") : text("returns.confirm")}
            </button>
            {!pending ? (
              <button type="button" disabled={submitting} className="min-h-12 border border-stone-500 bg-white px-3" onClick={() => setConfirming(false)}>
                {text("returns.back")}
              </button>
            ) : (
              <button type="button" disabled={submitting} className="min-h-12 border border-stone-500 bg-white px-3" onClick={onClose}>
                {text("returns.close")}
              </button>
            )}
          </div>
        </div>
      ) : null}
    </section>
  );
}

function ReturnResult({
  result,
  sale,
  walkIn,
  text,
  onClose,
}: {
  result: SaleReturnResult;
  sale: ReturnableSale;
  walkIn: boolean;
  text: (key: string) => string;
  onClose: () => void;
}) {
  const productName = (saleLineId: string) => sale.lines.find((line) => line.id === saleLineId)?.productName ?? saleLineId;
  return (
    <section className="flex flex-col gap-2 border border-stone-400 bg-white p-3" role="status" aria-label={text("returns.recorded")} data-testid="sale-return-result">
      <h4 className="text-lg font-semibold">{text("returns.recorded")}</h4>
      <p>
        {text("returns.document")} <LtrValue>{result.number}</LtrValue>
      </p>
      <p>
        {text("returns.sale")} <LtrValue>{sale.number}</LtrValue>
      </p>
      <h5 className="font-semibold">{text("returns.items")}</h5>
      <ul className="flex flex-col gap-1">
        {result.lines.map((line) => (
          <li key={line.id}>
            {productName(line.saleLineId)} × <LtrValue>{line.quantity}</LtrValue>
            {" = "}
            <LtrValue>{line.amount}</LtrValue>
          </li>
        ))}
      </ul>
      <p>
        {text("returns.returnAmount")} <LtrValue>{result.returnAmount}</LtrValue>
      </p>
      <p>
        {text(walkIn ? "returns.directRefund" : "returns.refundAmount")} <LtrValue>{result.requiredRefund}</LtrValue>
      </p>
      {result.refunds.map((refund) => (
        <p key={refund.id}>
          {text(`pos.${refund.method}`)} <LtrValue>{refund.amount}</LtrValue>
        </p>
      ))}
      {!walkIn ? (
        <p>
          {text("returns.netReduction")} <LtrValue>{result.netAccountReduction}</LtrValue>
        </p>
      ) : null}
      <button type="button" autoFocus className="min-h-12 w-fit border border-stone-500 bg-white px-3" onClick={onClose}>
        {text("returns.close")}
      </button>
    </section>
  );
}

function LtrValue({ children }: { children: ReactNode }) {
  return (
    <span dir="ltr" className="inline-block">
      {children}
    </span>
  );
}
