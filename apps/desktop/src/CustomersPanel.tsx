import { useEffect, useState, type FormEvent } from "react";
import {
  createCustomer,
  exclusiveStatementTo,
  formatStatementInstant,
  getCustomerStatement,
  listCustomers,
  updateCustomer,
  type CustomerInput,
  type CustomerRow,
  type CustomerStatement,
  type CustomerType,
} from "./customersClient";
import { customerStatementPrintCss } from "./customerStatementPrint";
import { direction, translate, type Locale } from "./i18n";

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

type CustomersPanelProps = {
  apiBaseUrl: string;
  accessToken: string;
  fetcher: Fetcher;
  locale: Locale;
  administrator?: boolean;
  createKey?: () => string;
  revision?: number;
};

type ProblemBody = {
  code?: string;
  errors?: Record<string, string[]>;
};

export function CustomersPanel({
  apiBaseUrl,
  accessToken,
  fetcher,
  locale,
  administrator = false,
  createKey = () => crypto.randomUUID(),
  revision = 0,
}: CustomersPanelProps) {
  const text = (key: string) => translate(locale, key);
  const [query, setQuery] = useState("");
  const [customers, setCustomers] = useState<CustomerRow[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [type, setType] = useState<CustomerType>("individual");
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [address, setAddress] = useState("");
  const [notes, setNotes] = useState("");
  const [rc, setRc] = useState("");
  const [nif, setNif] = useState("");
  const [nis, setNis] = useState("");
  const [ai, setAi] = useState("");
  const [creditLimit, setCreditLimit] = useState("");
  const [isActive, setIsActive] = useState(true);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [fromDate, setFromDate] = useState("");
  const [throughDate, setThroughDate] = useState("");
  const [statement, setStatement] = useState<CustomerStatement | null>(null);
  const [statementError, setStatementError] = useState<string | null>(null);

  async function reload(nextQuery = query) {
    const response = await listCustomers(fetcher, apiBaseUrl, accessToken, nextQuery);
    if (!response.ok) {
      setErrorCode("request.failed");
      return;
    }

    setCustomers((await response.json()) as CustomerRow[]);
  }

  useEffect(() => {
    void reload("");
  }, [apiBaseUrl, accessToken]);

  useEffect(() => {
    if (revision === 0) {
      return;
    }

    void reload();
    if (statement) {
      void showStatement();
    }
  }, [revision]);

  function select(customer: CustomerRow) {
    setSelectedId(customer.id);
    setType(customer.type);
    setName(customer.name);
    setPhone(customer.phone ?? "");
    setAddress(customer.address ?? "");
    setNotes(customer.notes ?? "");
    setRc(customer.rc ?? "");
    setNif(customer.nif ?? "");
    setNis(customer.nis ?? "");
    setAi(customer.ai ?? "");
    setCreditLimit(customer.creditLimit ?? "");
    setIsActive(customer.isActive);
    setErrorCode(null);
    setStatement(null);
    setStatementError(null);
  }

  function resetForm() {
    setSelectedId(null);
    setType("individual");
    setName("");
    setPhone("");
    setAddress("");
    setNotes("");
    setRc("");
    setNif("");
    setNis("");
    setAi("");
    setCreditLimit("");
    setIsActive(true);
    setErrorCode(null);
    setStatement(null);
    setStatementError(null);
  }

  async function showStatement() {
    if (!selectedId) {
      return;
    }

    const from = fromDate.trim();
    const through = throughDate.trim();
    setStatement(null);
    if ((from === "") !== (through === "")) {
      setStatementError("statement.date");
      return;
    }

    if (from !== "" && from > through) {
      setStatementError("statement.range");
      return;
    }

    const to = through === "" ? undefined : exclusiveStatementTo(through);
    if (through !== "" && !to) {
      setStatementError("statement.date");
      return;
    }

    const response = await getCustomerStatement(
      fetcher,
      apiBaseUrl,
      accessToken,
      selectedId,
      from || undefined,
      to,
    );
    if (!response.ok) {
      const problem = (await response.json()) as ProblemBody;
      const fieldCode = problem.errors ? Object.values(problem.errors)[0]?.[0] : undefined;
      setStatementError(fieldCode ?? problem.code ?? "request.failed");
      return;
    }

    setStatementError(null);
    setStatement((await response.json()) as CustomerStatement);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    const body: CustomerInput = {
      type,
      name,
      phone,
      address,
      notes,
    };
    if (administrator) {
      body.isActive = isActive;
      body.creditLimit = creditLimit;
      if (type === "business") {
        body.rc = rc;
        body.nif = nif;
        body.nis = nis;
        body.ai = ai;
      }
    }

    const response = selectedId
      ? await updateCustomer(fetcher, apiBaseUrl, accessToken, selectedId, body)
      : await createCustomer(fetcher, apiBaseUrl, accessToken, createKey(), body);
    if (!response.ok) {
      const problem = (await response.json()) as ProblemBody;
      const fieldCode = problem.errors ? Object.values(problem.errors)[0]?.[0] : undefined;
      setErrorCode(fieldCode ?? problem.code ?? "request.failed");
      return;
    }

    const saved = (await response.json()) as CustomerRow;
    setErrorCode(null);
    select(saved);
    await reload();
  }

  const selected = customers.find((customer) => customer.id === selectedId) ?? null;

  function outstandingFigure(amount: string | undefined, labelled: boolean) {
    if (!amount) {
      return null;
    }

    if (amount.trim().startsWith("-")) {
      return <span>{text("sale.payment_integrity")}</span>;
    }

    if (!labelled) {
      return <span dir="ltr">{amount}</span>;
    }

    return (
      <p>
        {text("customers.outstanding")} <span dir="ltr">{amount}</span>
      </p>
    );
  }

  return (
    <section className="flex flex-col gap-3" data-testid="customers" dir={direction(locale)}>
      <style>{customerStatementPrintCss}</style>
      <h2 className="text-xl font-semibold">{text("customers.title")}</h2>
      <label className="flex flex-col gap-1 text-sm">
        {text("customers.search")}
        <input
          value={query}
          onChange={(event) => {
            const next = event.target.value;
            setQuery(next);
            void reload(next);
          }}
          className="border border-stone-400 px-2 py-1"
        />
      </label>
      {customers.length === 0 ? <p>{text("customers.empty")}</p> : null}
      <ul className="flex flex-col gap-2">
        {customers.map((customer) => (
          <li key={customer.id}>
            <button type="button" className="border border-stone-400 bg-white px-3 py-2 text-start" onClick={() => select(customer)}>
              {customer.name}
              {" · "}
              <span dir="ltr">{customer.phone || "—"}</span>
              {" · "}
              {text(customer.type === "business" ? "customers.business" : "customers.individual")}
              {" · "}
              {text(customer.isActive ? "customers.active" : "customers.inactive")}
              {customer.outstandingAmount ? (
                <>
                  {" · "}
                  {outstandingFigure(customer.outstandingAmount, false)}
                </>
              ) : null}
            </button>
          </li>
        ))}
      </ul>
      <form className="flex flex-col gap-2" onSubmit={(event) => void submit(event)}>
        <h3 className="font-semibold">{selected ? text("customers.details") : text("customers.create")}</h3>
        <label className="flex flex-col gap-1 text-sm">
          {text("customers.type")}
          <select
            value={type}
            disabled={!administrator && selectedId !== null}
            onChange={(event) => setType(event.target.value as CustomerType)}
            className="border border-stone-400 px-2 py-1"
          >
            <option value="individual">{text("customers.individual")}</option>
            <option value="business">{text("customers.business")}</option>
          </select>
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {text("customers.name")}
          <input value={name} onChange={(event) => setName(event.target.value)} className="border border-stone-400 px-2 py-1" />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {text("customers.phone")}
          <input dir="ltr" value={phone} onChange={(event) => setPhone(event.target.value)} className="border border-stone-400 px-2 py-1" />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {text("customers.address")}
          <input value={address} onChange={(event) => setAddress(event.target.value)} className="border border-stone-400 px-2 py-1" />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {text("customers.notes")}
          <input value={notes} onChange={(event) => setNotes(event.target.value)} className="border border-stone-400 px-2 py-1" />
        </label>
        {administrator && type === "business" ? (
          <>
            <label className="flex flex-col gap-1 text-sm">
              {text("customers.rc")}
              <input dir="ltr" value={rc} onChange={(event) => setRc(event.target.value)} className="border border-stone-400 px-2 py-1" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              {text("customers.nif")}
              <input dir="ltr" value={nif} onChange={(event) => setNif(event.target.value)} className="border border-stone-400 px-2 py-1" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              {text("customers.nis")}
              <input dir="ltr" value={nis} onChange={(event) => setNis(event.target.value)} className="border border-stone-400 px-2 py-1" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              {text("customers.ai")}
              <input dir="ltr" value={ai} onChange={(event) => setAi(event.target.value)} className="border border-stone-400 px-2 py-1" />
            </label>
          </>
        ) : null}
        {administrator ? (
          <>
            <label className="flex flex-col gap-1 text-sm">
              {text("customers.creditLimit")}
              <input dir="ltr" value={creditLimit} onChange={(event) => setCreditLimit(event.target.value)} className="border border-stone-400 px-2 py-1" />
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={isActive} onChange={(event) => setIsActive(event.target.checked)} />
              {text("customers.active")}
            </label>
          </>
        ) : null}
        {errorCode ? <p role="alert">{text(errorCode)}</p> : null}
        <div className="flex gap-2">
          <button type="submit" className="border border-stone-500 bg-white px-3 py-1.5 text-sm">
            {text("customers.save")}
          </button>
          <button type="button" className="border border-stone-500 bg-white px-3 py-1.5 text-sm" onClick={resetForm}>
            {text("customers.create")}
          </button>
        </div>
      </form>
      {selectedId ? (
        <article className="customer-statement flex flex-col gap-2" data-testid="customer-statement" dir={direction(locale)}>
          <h3 className="font-semibold">{text("customers.statement")}</h3>
          {outstandingFigure(selected?.outstandingAmount, true)}
          <label className="flex flex-col gap-1 text-sm">
            {text("customers.from")}
            <input type="date" value={fromDate} onChange={(event) => setFromDate(event.target.value)} className="border border-stone-400 px-2 py-1" />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {text("customers.through")}
            <input type="date" value={throughDate} onChange={(event) => setThroughDate(event.target.value)} className="border border-stone-400 px-2 py-1" />
          </label>
          <button type="button" className="border border-stone-500 bg-white px-3 py-1.5 text-sm" onClick={() => void showStatement()}>
            {text("customers.showStatement")}
          </button>
          {statementError ? <p role="alert">{text(statementError)}</p> : null}
          {statement ? (
            <div className="flex flex-col gap-2">
              <p className="text-lg font-semibold" data-testid="customer-statement-name">
                <span className="block text-sm font-normal">{text("customers.name")}</span>
                {statement.customerName}
              </p>
              <p>
                {text("customers.opening")} <span dir="ltr">{statement.openingBalance}</span>
              </p>
              <button type="button" className="border border-stone-500 bg-white px-3 py-1.5 text-sm" onClick={() => window.print()}>
                {text("customers.print")}
              </button>
              {statement.lines.length === 0 ? <p>{text("customers.emptyStatement")}</p> : null}
              <table className="border-collapse text-sm">
                <thead>
                  <tr>
                    <th className="border border-stone-300 px-2 py-1 text-start">{text("customers.occurred")}</th>
                    <th className="border border-stone-300 px-2 py-1 text-start">{text("customers.document")}</th>
                    <th className="border border-stone-300 px-2 py-1 text-start">{text("customers.amount")}</th>
                    <th className="border border-stone-300 px-2 py-1 text-start">{text("customers.running")}</th>
                  </tr>
                </thead>
                <tbody>
                  {statement.lines.map((line) => (
                    <tr key={line.documentId}>
                      <td className="border border-stone-300 px-2 py-1" dir="ltr">
                        {formatStatementInstant(line.occurredAt, statement.timeZoneId)}
                      </td>
                      <td className="border border-stone-300 px-2 py-1">
                        {text(line.kind === "sale" || line.kind === "payment" || line.kind === "return" ? `customers.${line.kind}` : line.kind)}
                        {" "}
                        <span dir="ltr">{line.documentNumber || "—"}</span>
                      </td>
                      <td className="border border-stone-300 px-2 py-1" dir="ltr">{line.amount}</td>
                      <td className="border border-stone-300 px-2 py-1" dir="ltr">{line.runningBalance}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p>
                {text("customers.closing")} <span dir="ltr">{statement.closingBalance}</span>
              </p>
              <p>
                {text("customers.outstanding")} <span dir="ltr">{statement.outstandingAmount}</span>
              </p>
            </div>
          ) : null}
        </article>
      ) : null}
    </section>
  );
}
