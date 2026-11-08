import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { PosPanel } from "./PosPanel";
import type { Locale } from "./i18n";
import type { SaleReturnResult } from "./salesClient";
import { memoryStorage } from "./test/memoryStorage";

const api = "http://127.0.0.1:5080";

type Sale = ReturnType<typeof customerSale>;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function customerSale(overrides: Record<string, unknown> = {}) {
  return {
    id: "sale-1",
    number: "V-000020",
    status: "posted",
    subtotal: "40.0000",
    discountAmount: "0.0000",
    total: "40.0000",
    createdAt: "2026-11-08T10:00:00Z",
    createdBy: "user-1",
    customerId: "c1" as string | null,
    customerName: "Amin" as string | null,
    paidAmount: "25.0000",
    refundedAmount: "0.0000",
    outstandingAmount: "15.0000",
    paymentState: "partial",
    lines: [
      { id: "line-1", lineNumber: 1, productId: "p1", productSku: "EAU-1", productName: "Eau", productUnit: "u", quantity: "3.0000", unitPrice: "10.0000", lineTotal: "30.0000", allocatedSaleDiscount: "0.0000", tracksStock: true, returnedQuantity: "0.0000", remainingReturnableQuantity: "3.0000", returnedAmount: "0.0000", remainingReturnableAmount: "30.0000" },
      { id: "line-2", lineNumber: 2, productId: "p2", productSku: "FAR-1", productName: "Farine", productUnit: "kg", quantity: "2.0000", unitPrice: "5.0000", lineTotal: "10.0000", allocatedSaleDiscount: "0.0000", tracksStock: false, returnedQuantity: "0.0000", remainingReturnableQuantity: "2.0000", returnedAmount: "0.0000", remainingReturnableAmount: "10.0000" },
    ],
    payments: [{ id: "pay-1", method: "cash", amount: "25.0000", receiptNumber: "R-000001" }],
    ...overrides,
  };
}

function walkInSale() {
  return customerSale({
    customerId: null,
    customerName: null,
    paidAmount: "40.0000",
    outstandingAmount: "0.0000",
    paymentState: "paid",
    payments: [{ id: "pay-1", method: "cash", amount: "40.0000" }],
  });
}

function summary(sale: Sale) {
  return {
    id: sale.id,
    number: sale.number,
    total: sale.total,
    customerId: sale.customerId,
    customerName: sale.customerName,
    paidAmount: sale.paidAmount,
    refundedAmount: sale.refundedAmount,
    outstandingAmount: sale.outstandingAmount,
    paymentState: sale.paymentState,
  };
}

function withReturned(sale: Sale, lineId: string, returnedQuantity: string, remainingQuantity: string, returnedAmount: string, remainingAmount: string) {
  return {
    ...sale,
    lines: sale.lines.map((line) =>
      line.id === lineId
        ? { ...line, returnedQuantity, remainingReturnableQuantity: remainingQuantity, returnedAmount, remainingReturnableAmount: remainingAmount }
        : line,
    ),
  };
}

function withoutReturnHistory(sale: Sale): Sale {
  return {
    ...sale,
    lines: sale.lines.map((line) => {
      const copy: Sale["lines"][number] = { ...line };
      const optional = copy as Partial<Sale["lines"][number]>;
      delete optional.returnedQuantity;
      delete optional.remainingReturnableQuantity;
      delete optional.returnedAmount;
      delete optional.remainingReturnableAmount;
      return copy;
    }),
  };
}

function returnResult(overrides: Partial<SaleReturnResult> = {}): SaleReturnResult {
  return {
    id: "ret-1",
    saleId: "sale-1",
    number: "AV-000001",
    reason: "Produit abîmé",
    returnedAt: "2026-11-08T11:00:00Z",
    returnAmount: "10.0000",
    requiredRefund: "0.0000",
    netAccountReduction: "10.0000",
    paidAmount: "25.0000",
    refundedAmount: "0.0000",
    outstandingAmount: "5.0000",
    paymentState: "partial",
    lines: [{ id: "rl-1", saleLineId: "line-1", productId: "p1", quantity: "1.0000", amount: "10.0000", tracksStock: true }],
    refunds: [],
    ...overrides,
  };
}

function apiFetcher(state: { sale: Sale }, onReturn: (init: RequestInit) => Promise<Response>) {
  return vi.fn(async (input: string, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith("/returns") && init?.method === "POST") {
      return onReturn(init);
    }
    if (url.endsWith("/api/v1/products") || url.endsWith("/api/v1/stock/balances")) {
      return json([]);
    }
    if (url.endsWith(`/api/v1/sales/${state.sale.id}`)) {
      return json(state.sale);
    }
    if (url.endsWith("/api/v1/sales")) {
      return json([summary(state.sale)]);
    }
    throw new Error(url);
  });
}

function renderRegister(
  fetcher: (input: string, init?: RequestInit) => Promise<Response>,
  options: { storage?: Storage; createKey?: () => string; locale?: Locale; onSalesChanged?: (id: string | null) => void; timeout?: number } = {},
) {
  return render(
    <PosPanel
      apiBaseUrl={api}
      accessToken="token"
      fetcher={fetcher}
      locale={options.locale ?? "fr"}
      createKey={options.createKey ?? (() => "key-1")}
      storage={options.storage ?? memoryStorage()}
      returnTimeoutMs={options.timeout}
      onSalesChanged={options.onSalesChanged}
    />,
  );
}

async function openFromHistory(user: ReturnType<typeof userEvent.setup>, saleId = "sale-1") {
  const row = await screen.findByTestId(`sale-row-${saleId}`);
  await user.click(within(row).getByRole("button", { name: "Retourner des articles" }));
  return screen.findByTestId("sale-return");
}

async function selectQuantity(user: ReturnType<typeof userEvent.setup>, product: string, quantity?: string) {
  await user.click(screen.getByRole("checkbox", { name: `Retourner ${product}` }));
  if (quantity !== undefined) {
    const input = screen.getByLabelText(`Quantité à retourner ${product}`);
    await user.clear(input);
    await user.type(input, quantity);
  }
}

async function review(user: ReturnType<typeof userEvent.setup>, reason = "Produit abîmé") {
  const field = screen.getByLabelText("Motif du retour");
  if ((field as HTMLInputElement).value === "" && reason) {
    await user.type(field, reason);
  }
  await user.click(screen.getByRole("button", { name: "Vérifier le retour" }));
}

function returnPosts(fetchMock: ReturnType<typeof apiFetcher>) {
  return fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/returns"));
}

function headerValue(init: RequestInit | undefined, name: string): string | undefined {
  return (init?.headers as Record<string, string> | undefined)?.[name];
}

describe("POS returns and refunds", () => {
  it("opens the return screen from sale history and from sale details", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    renderRegister(apiFetcher(state, async () => json(returnResult(), 201)));

    const panel = await openFromHistory(user);
    expect(within(panel).getByTestId("return-line-line-1")).toHaveTextContent("Eau · EAU-1");
    expect(within(panel).getByTestId("return-line-line-2")).toHaveTextContent("Farine · FAR-1");
    await user.click(within(panel).getByRole("button", { name: "Fermer" }));
    expect(screen.queryByTestId("sale-return")).not.toBeInTheDocument();

    const article = screen.getByRole("article", { name: "V-000020" });
    await user.click(within(article).getByRole("button", { name: "Retourner des articles" }));
    expect(await screen.findByTestId("sale-return")).toHaveTextContent("Retour sur vente");
  });

  it("keeps a fully returned or unposted sale out of the return form", async () => {
    const user = userEvent.setup();
    const finished = customerSale({ refundedAmount: "25.0000", outstandingAmount: "0.0000", paymentState: "paid" });
    finished.lines = finished.lines.map((line) => ({
      ...line,
      returnedQuantity: line.quantity,
      remainingReturnableQuantity: "0.0000",
      returnedAmount: line.lineTotal,
      remainingReturnableAmount: "0.0000",
    }));
    const state = { sale: finished };
    renderRegister(apiFetcher(state, async () => json({}, 500)));
    const row = await screen.findByTestId("sale-row-sale-1");
    await user.click(within(row).getByRole("button", { name: "Retourner des articles" }));
    const article = await screen.findByRole("article", { name: "V-000020" });
    expect(within(article).getByRole("alert")).toHaveTextContent("entièrement retournée");
    expect(screen.queryByTestId("sale-return")).not.toBeInTheDocument();
    expect(within(article).queryByRole("button", { name: "Retourner des articles" })).not.toBeInTheDocument();

    state.sale = customerSale({ status: "draft" });
    await user.click(within(row).getByRole("button", { name: "Retourner des articles" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("n'accepte plus de retour"));
    expect(screen.queryByTestId("sale-return")).not.toBeInTheDocument();
  });

  it("shows the server's returned and remaining quantities and amounts", async () => {
    const user = userEvent.setup();
    const state = {
      sale: withReturned(customerSale({ outstandingAmount: "5.0000" }), "line-1", "1.0000", "2.0000", "10.0000", "20.0000"),
    };
    renderRegister(apiFetcher(state, async () => json({}, 500)));
    const panel = await openFromHistory(user);

    const eau = within(panel).getByTestId("return-line-line-1");
    expect(eau).toHaveTextContent("3.0000");
    expect(within(eau).getByTestId("return-previous-line-1")).toHaveTextContent("1.0000");
    expect(within(eau).getByTestId("return-returned-amount-line-1")).toHaveTextContent("10.0000");
    expect(within(eau).getByTestId("return-remaining-line-1")).toHaveTextContent("2.0000");
    expect(within(eau).getByTestId("return-remaining-value-line-1")).toHaveTextContent("20.0000");
    expect(eau).toHaveTextContent("Remis en stock");
    expect(within(panel).getByTestId("return-line-line-2")).toHaveTextContent("Non suivi en stock");
    expect(panel).not.toHaveTextContent("Inconnu ici");

    await selectQuantity(user, "Eau");
    expect(screen.getByLabelText("Quantité à retourner Eau")).toHaveValue("2.0000");
    expect(screen.getByTestId("return-preview")).toHaveTextContent("20.0000");
    await user.clear(screen.getByLabelText("Quantité à retourner Eau"));
    await user.type(screen.getByLabelText("Quantité à retourner Eau"), "3");
    expect(within(panel).getByRole("alert")).toHaveTextContent("La quantité dépasse le reste retournable.");
  });

  it("shows a return made on another computer after the sale detail is refreshed", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    renderRegister(apiFetcher(state, async () => json({}, 500)));
    const panel = await openFromHistory(user);
    expect(within(panel).getByTestId("return-previous-line-1")).toHaveTextContent("0.0000");
    expect(within(panel).getByTestId("return-remaining-line-1")).toHaveTextContent("3.0000");
    await user.click(within(panel).getByRole("button", { name: "Fermer" }));

    state.sale = withReturned(customerSale({ outstandingAmount: "5.0000" }), "line-1", "1.0000", "2.0000", "10.0000", "20.0000");
    await user.click(screen.getByRole("button", { name: /V-000020/ }));
    const article = await screen.findByRole("article", { name: "V-000020" });
    await user.click(within(article).getByRole("button", { name: "Retourner des articles" }));
    const refreshed = await screen.findByTestId("sale-return");
    expect(within(refreshed).getByTestId("return-previous-line-1")).toHaveTextContent("1.0000");
    expect(within(refreshed).getByTestId("return-returned-amount-line-1")).toHaveTextContent("10.0000");
    expect(within(refreshed).getByTestId("return-remaining-line-1")).toHaveTextContent("2.0000");
    expect(within(refreshed).getByTestId("return-remaining-value-line-1")).toHaveTextContent("20.0000");
    await selectQuantity(user, "Eau");
    expect(screen.getByTestId("return-preview")).toHaveTextContent("Valeur estimée du retour 20.0000");
  });

  it("blocks a return when the sale detail has no authoritative return history", async () => {
    const user = userEvent.setup();
    const state = { sale: withoutReturnHistory(customerSale()) };
    const fetchMock = apiFetcher(state, async () => json(returnResult(), 201));
    renderRegister(fetchMock);
    const row = await screen.findByTestId("sale-row-sale-1");
    await user.click(within(row).getByRole("button", { name: "Retourner des articles" }));
    const article = await screen.findByRole("article", { name: "V-000020" });
    expect(within(article).getByRole("alert")).toHaveTextContent("L'historique des retours est indisponible");
    expect(screen.queryByTestId("sale-return")).not.toBeInTheDocument();
    expect(returnPosts(fetchMock)).toHaveLength(0);
  });

  it("accepts a partial quantity and reduces customer debt without a refund", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    const fetchMock = apiFetcher(state, async () => json(returnResult(), 201));
    renderRegister(fetchMock);
    await openFromHistory(user);

    await selectQuantity(user, "Eau", "0");
    expect(screen.getByRole("alert")).toHaveTextContent("La quantité retournée doit être supérieure à zéro.");
    await user.clear(screen.getByLabelText("Quantité à retourner Eau"));
    await user.type(screen.getByLabelText("Quantité à retourner Eau"), "1");

    const preview = screen.getByTestId("return-preview");
    expect(preview).toHaveTextContent("Valeur estimée du retour 10.0000");
    expect(preview).toHaveTextContent("Reste dû avant retour 15.0000");
    expect(screen.getByTestId("return-required-refund")).toHaveTextContent("0.0000");
    expect(screen.getByTestId("return-net-reduction")).toHaveTextContent("10.0000");
    expect(screen.getByTestId("return-no-refund")).toHaveTextContent("le retour réduit la dette du client");
    expect(screen.queryByTestId("return-tenders")).not.toBeInTheDocument();

    await review(user);
    const confirm = screen.getByRole("region", { name: "Confirmer le retour" });
    expect(confirm).toHaveTextContent("V-000020");
    expect(confirm).toHaveTextContent("Eau × 1.0000");
    expect(confirm).toHaveTextContent("Baisse de la dette du client 10.0000");
    await user.click(within(confirm).getByRole("button", { name: "Confirmer le retour" }));

    await screen.findByTestId("sale-return-result");
    expect(JSON.parse(String(returnPosts(fetchMock)[0][1]?.body))).toEqual({
      reason: "Produit abîmé",
      lines: [{ saleLineId: "line-1", quantity: "1.0000" }],
      refunds: [],
    });
  });

  it("pays a cash refund when the return exceeds the outstanding amount", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    const fetchMock = apiFetcher(state, async () =>
      json(returnResult({ returnAmount: "30.0000", requiredRefund: "15.0000", netAccountReduction: "15.0000", refunds: [{ id: "rf-1", method: "cash", amount: "15.0000" }] }), 201),
    );
    renderRegister(fetchMock);
    await openFromHistory(user);
    await selectQuantity(user, "Eau");

    expect(screen.getByTestId("return-required-refund")).toHaveTextContent("Remboursement à verser 15.0000");
    expect(screen.getByLabelText("Remboursement Espèces")).toHaveValue("15.0000");
    expect(screen.getByTestId("return-to-allocate")).toHaveTextContent("0.0000");
    expect(screen.queryByLabelText(/Autre/)).not.toBeInTheDocument();
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    await screen.findByTestId("sale-return-result");
    expect(JSON.parse(String(returnPosts(fetchMock)[0][1]?.body)).refunds).toEqual([{ method: "cash", amount: "15.0000" }]);
  });

  it("requires an exact split across cash, transfer, and card", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    const fetchMock = apiFetcher(state, async () => json(returnResult({ returnAmount: "40.0000", requiredRefund: "25.0000" }), 201));
    renderRegister(fetchMock);
    await openFromHistory(user);
    await selectQuantity(user, "Eau");
    await selectQuantity(user, "Farine");
    expect(screen.getByTestId("return-required-refund")).toHaveTextContent("25.0000");

    const cash = screen.getByLabelText("Remboursement Espèces");
    await user.clear(cash);
    await user.type(cash, "10");
    expect(screen.getByTestId("return-to-allocate")).toHaveTextContent("15.0000");
    expect(screen.getByRole("alert")).toHaveTextContent("Le remboursement n'est pas entièrement réparti.");
    await review(user);
    expect(screen.queryByRole("region", { name: "Confirmer le retour" })).not.toBeInTheDocument();

    await user.type(screen.getByLabelText("Remboursement Virement"), "20");
    expect(screen.getByRole("alert")).toHaveTextContent("La répartition dépasse le remboursement à verser.");
    await user.clear(screen.getByLabelText("Remboursement Virement"));
    await user.type(screen.getByLabelText("Remboursement Virement"), "10");
    await user.type(screen.getByLabelText("Remboursement Carte"), "5");
    expect(screen.getByTestId("return-to-allocate")).toHaveTextContent("0.0000");

    await review(user);
    const confirm = screen.getByRole("region", { name: "Confirmer le retour" });
    expect(confirm).toHaveTextContent("Espèces 10.0000");
    expect(confirm).toHaveTextContent("Virement 10.0000");
    expect(confirm).toHaveTextContent("Carte 5.0000");
    await user.click(within(confirm).getByRole("button", { name: "Confirmer le retour" }));
    await screen.findByTestId("sale-return-result");
    expect(JSON.parse(String(returnPosts(fetchMock)[0][1]?.body)).refunds).toEqual([
      { method: "cash", amount: "10.0000" },
      { method: "transfer", amount: "10.0000" },
      { method: "card", amount: "5.0000" },
    ]);
  });

  it("presents a walk-in refund as a direct refund", async () => {
    const user = userEvent.setup();
    const state = { sale: walkInSale() };
    const fetchMock = apiFetcher(state, async () =>
      json(returnResult({ requiredRefund: "10.0000", netAccountReduction: "0.0000", refunds: [{ id: "rf-1", method: "cash", amount: "10.0000" }] }), 201),
    );
    renderRegister(fetchMock);
    const panel = await openFromHistory(user);
    expect(panel).toHaveTextContent("Vente comptoir : le remboursement est versé directement");
    await selectQuantity(user, "Eau", "1");
    expect(screen.getByTestId("return-required-refund")).toHaveTextContent("Remboursement direct 10.0000");
    expect(screen.queryByTestId("return-net-reduction")).not.toBeInTheDocument();
    await review(user);
    expect(screen.getByRole("region", { name: "Confirmer le retour" })).toHaveTextContent("Vente comptoir");
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    const result = await screen.findByTestId("sale-return-result");
    expect(result).toHaveTextContent("Remboursement direct 10.0000");
    expect(result).not.toHaveTextContent("Baisse de la dette du client");
  });

  it("requires a reason between 1 and 200 characters", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    const fetchMock = apiFetcher(state, async () => json(returnResult(), 201));
    renderRegister(fetchMock);
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await user.click(screen.getByRole("button", { name: "Vérifier le retour" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Le motif du retour est obligatoire.");

    await user.click(screen.getByLabelText("Motif du retour"));
    await user.paste("x".repeat(201));
    expect(screen.getByRole("alert")).toHaveTextContent("Le motif ne doit pas dépasser 200 caractères.");
    await user.click(screen.getByRole("button", { name: "Vérifier le retour" }));
    expect(screen.queryByRole("region", { name: "Confirmer le retour" })).not.toBeInTheDocument();
    expect(returnPosts(fetchMock)).toHaveLength(0);
  });

  it("keeps the same key and payload across a timeout and treats the replay as success", async () => {
    const user = userEvent.setup();
    const createKey = vi.fn(() => "key-timeout");
    const state = { sale: customerSale() };
    let attempts = 0;
    const fetchMock = apiFetcher(state, (init) => {
      attempts += 1;
      if (attempts === 1) {
        return new Promise<Response>((_resolve, reject) => {
          init.signal?.addEventListener("abort", () => reject(new DOMException("Timed out", "AbortError")));
        });
      }
      return Promise.resolve(json(returnResult(), 201));
    });
    renderRegister(fetchMock, { createKey, timeout: 20 });
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));

    expect(await screen.findByTestId("return-error")).toHaveTextContent("Aucune réponse confirmée du serveur");
    expect(screen.getByLabelText("Quantité à retourner Eau")).toHaveValue("1");
    expect(screen.getByLabelText("Quantité à retourner Eau")).toBeDisabled();
    expect(screen.getByLabelText("Motif du retour")).toHaveValue("Produit abîmé");

    await user.click(screen.getByRole("button", { name: "Réessayer le même retour" }));
    expect(await screen.findByTestId("sale-return-result")).toHaveTextContent("AV-000001");
    const posts = returnPosts(fetchMock);
    expect(posts.map((call) => headerValue(call[1], "Idempotency-Key"))).toEqual(["key-timeout", "key-timeout"]);
    expect(posts[1][1]?.body).toBe(posts[0][1]?.body);
    expect(createKey).toHaveBeenCalledTimes(1);
  });

  it("restores an uncertain return after the screen is reopened", async () => {
    const user = userEvent.setup();
    const storage = memoryStorage();
    const state = { sale: customerSale() };
    const first = apiFetcher(state, async () => {
      throw new TypeError("Failed to fetch");
    });
    const view = renderRegister(first, { storage, createKey: () => "key-kept" });
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    await screen.findByTestId("return-error");
    view.unmount();

    const second = apiFetcher(state, async () => json(returnResult(), 201));
    renderRegister(second, { storage, createKey: () => "key-new" });
    await openFromHistory(user);
    expect(screen.getByTestId("return-error")).toHaveTextContent("Le résultat du dernier envoi de ce retour est incertain.");
    expect(screen.getByLabelText("Quantité à retourner Eau")).toHaveValue("1.0000");
    await user.click(screen.getByRole("button", { name: "Réessayer le même retour" }));
    await screen.findByTestId("sale-return-result");
    expect(headerValue(returnPosts(second)[0][1], "Idempotency-Key")).toBe("key-kept");
    expect(returnPosts(second)[0][1]?.body).toBe(returnPosts(first)[0][1]?.body);
  });

  it("keeps the return pending after the session expires", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    let attempts = 0;
    const fetchMock = apiFetcher(state, async () => {
      attempts += 1;
      return attempts === 1 ? json({ code: "auth.required" }, 401) : json(returnResult(), 201);
    });
    renderRegister(fetchMock, { createKey: () => "key-auth" });
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    expect(await screen.findByTestId("return-error")).toHaveTextContent("Votre session a expiré.");
    await user.click(screen.getByRole("button", { name: "Réessayer le même retour" }));
    await screen.findByTestId("sale-return-result");
    expect(returnPosts(fetchMock).map((call) => headerValue(call[1], "Idempotency-Key"))).toEqual(["key-auth", "key-auth"]);
  });

  it("sends only one return while the first request is in progress", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    let resolveReturn: (response: Response) => void = () => undefined;
    const fetchMock = apiFetcher(state, () => new Promise<Response>((resolve) => {
      resolveReturn = resolve;
    }));
    renderRegister(fetchMock);
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    const confirm = screen.getByRole("button", { name: "Confirmer le retour" });
    confirm.click();
    confirm.click();
    await waitFor(() => expect(returnPosts(fetchMock)).toHaveLength(1));
    expect(await screen.findByRole("button", { name: "Enregistrement du retour…" })).toBeDisabled();
    resolveReturn(json(returnResult(), 201));
    await screen.findByTestId("sale-return-result");
    expect(returnPosts(fetchMock)).toHaveLength(1);
  });

  it("shows the posted return and refreshes the sale, history, and customer", async () => {
    const user = userEvent.setup();
    const onSalesChanged = vi.fn();
    const state = { sale: customerSale() };
    const fetchMock = apiFetcher(state, async () => {
      state.sale = withReturned(customerSale({ outstandingAmount: "5.0000" }), "line-1", "1.0000", "2.0000", "10.0000", "20.0000");
      return json(returnResult(), 201);
    });
    renderRegister(fetchMock, { onSalesChanged });
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));

    const result = await screen.findByTestId("sale-return-result");
    expect(result).toHaveTextContent("Avoir AV-000001");
    expect(result).toHaveTextContent("Vente V-000020");
    expect(result).toHaveTextContent("Eau × 1.0000 = 10.0000");
    expect(result).toHaveTextContent("Montant du retour 10.0000");
    expect(result).toHaveTextContent("Montant remboursé 0.0000");
    expect(result).toHaveTextContent("Baisse de la dette du client 10.0000");
    expect(within(result).getByText("AV-000001")).toHaveAttribute("dir", "ltr");

    await waitFor(() => expect(onSalesChanged).toHaveBeenCalledWith("c1"));
    const saleReads = fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/api/v1/sales/sale-1"));
    const historyReads = fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/api/v1/sales") && !call[1]?.method);
    expect(saleReads).toHaveLength(2);
    expect(historyReads.length).toBeGreaterThanOrEqual(2);
    expect(screen.getByRole("article", { name: "V-000020" })).toHaveTextContent("5.0000");

    await user.click(within(result).getByRole("button", { name: "Fermer" }));
    await user.click(within(screen.getByRole("article", { name: "V-000020" })).getByRole("button", { name: "Retourner des articles" }));
    expect(await screen.findByTestId("return-previous-line-1")).toHaveTextContent("1.0000");
    expect(screen.getByTestId("return-remaining-line-1")).toHaveTextContent("2.0000");
  });

  it("reports a quantity already returned by another operator and starts a new operation", async () => {
    const user = userEvent.setup();
    const keys = ["key-a", "key-b"];
    const state = { sale: customerSale() };
    let attempts = 0;
    const fetchMock = apiFetcher(state, async () => {
      attempts += 1;
      if (attempts === 1) {
        state.sale = withReturned(customerSale({ outstandingAmount: "5.0000" }), "line-1", "1.0000", "2.0000", "10.0000", "20.0000");
        return json({ code: "validation.failed", errors: { "lines[0].quantity": ["return.exceeds"] } }, 400);
      }
      return json(returnResult(), 201);
    });
    renderRegister(fetchMock, { createKey: () => keys.shift() ?? "key-extra" });
    await openFromHistory(user);
    await selectQuantity(user, "Eau");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));

    expect(await screen.findByTestId("return-error")).toHaveTextContent("peut-être par un autre opérateur");
    expect(screen.queryByRole("region", { name: "Confirmer le retour" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("return-previous-line-1")).toHaveTextContent("1.0000"));
    expect(screen.getByTestId("return-remaining-line-1")).toHaveTextContent("2.0000");
    expect(screen.getByLabelText("Quantité à retourner Eau")).toBeEnabled();
    expect(screen.getByLabelText("Quantité à retourner Eau")).toHaveValue("3.0000");
    expect(screen.getByTestId("return-line-line-1")).toHaveTextContent("La quantité dépasse le reste retournable.");

    await user.clear(screen.getByLabelText("Quantité à retourner Eau"));
    await user.type(screen.getByLabelText("Quantité à retourner Eau"), "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    await screen.findByTestId("sale-return-result");
    expect(returnPosts(fetchMock).map((call) => headerValue(call[1], "Idempotency-Key"))).toEqual(["key-a", "key-b"]);
  });

  it("shows a localized message without internal details for an idempotency conflict", async () => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    renderRegister(apiFetcher(state, async () => json({ code: "sale.idempotency_conflict", detail: "duplicate key value violates constraint" }, 409)));
    await openFromHistory(user);
    await selectQuantity(user, "Eau", "1");
    await review(user);
    await user.click(screen.getByRole("button", { name: "Confirmer le retour" }));
    const error = await screen.findByTestId("return-error");
    expect(error).toHaveTextContent("Ce retour a déjà été envoyé avec un contenu différent.");
    expect(error).not.toHaveTextContent("constraint");
  });

  it.each([
    ["fr", "Retour sur vente", "ltr", "Retourner des articles"],
    ["ar", "مرتجع على البيع", "rtl", "إرجاع منتجات"],
    ["en", "Sale return", "ltr", "Return items"],
  ] as const)("renders the return screen in %s", async (locale, title, dir, action) => {
    const user = userEvent.setup();
    const state = { sale: customerSale() };
    renderRegister(apiFetcher(state, async () => json({}, 500)), { locale });
    const row = await screen.findByTestId("sale-row-sale-1");
    await user.click(within(row).getByRole("button", { name: action }));
    const panel = await screen.findByTestId("sale-return");
    expect(panel).toHaveTextContent(title);
    expect(screen.getByTestId("pos")).toHaveAttribute("dir", dir);
    const amounts = within(panel).getAllByText("30.0000");
    expect(amounts.length).toBeGreaterThan(0);
    expect(amounts.every((node) => node.closest("[dir='ltr']") !== null)).toBe(true);
  });
});
