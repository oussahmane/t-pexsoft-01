import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { CustomersPanel } from "./CustomersPanel";
import { customerStatementPrintCss } from "./customerStatementPrint";
import { formatStatementInstant } from "./customersClient";
import { translate, type Locale } from "./i18n";

const amin = {
  id: "c1",
  type: "individual" as const,
  name: "Amin",
  phone: "0551000001",
  address: null,
  notes: null,
  rc: null,
  nif: null,
  nis: null,
  ai: null,
  creditLimit: null,
  isActive: true,
};

const otherAmin = { ...amin, id: "c2", type: "business" as const, phone: "0551000002" };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

describe("CustomersPanel", () => {
  it.each([
    ["fr", "Clients", "ltr"],
    ["ar", "الزبائن", "rtl"],
    ["en", "Customers", "ltr"],
  ] as const)("translates the customer registry in %s", async (locale: Locale, title, dir) => {
    render(
      <CustomersPanel
        apiBaseUrl="http://127.0.0.1:5080"
        accessToken="token"
        fetcher={async () => json([])}
        locale={locale}
      />,
    );

    expect(await screen.findByRole("heading", { name: title })).toBeInTheDocument();
    expect(screen.getByTestId("customers")).toHaveAttribute("dir", dir);
  });

  it("lists customers who share a name with their phone and type", async () => {
    render(
      <CustomersPanel
        apiBaseUrl="http://127.0.0.1:5080"
        accessToken="token"
        fetcher={async () => json([amin, otherAmin])}
        locale="fr"
      />,
    );

    expect(await screen.findByRole("button", { name: /0551000001/ })).toHaveTextContent("Particulier");
    expect(screen.getByRole("button", { name: /0551000002/ })).toHaveTextContent("Entreprise");
  });

  it("creates a customer from the contact fields", async () => {
    const user = userEvent.setup();
    const saved = { ...amin, name: "Sara" };
    let customers: unknown[] = [];
    const fetcher = vi.fn(async (_input: string, init?: RequestInit) => {
      if (init?.method === "POST") {
        customers = [saved];
        return json(saved, 201);
      }
      return json(customers);
    });
    render(
      <CustomersPanel
        apiBaseUrl="http://127.0.0.1:5080"
        accessToken="token"
        fetcher={fetcher}
        locale="fr"
        createKey={() => "customer-key"}
      />,
    );

    await user.type(await screen.findByLabelText("Nom du client"), "Sara");
    await user.click(screen.getByRole("button", { name: "Enregistrer le client" }));
    const posted = fetcher.mock.calls.find((call) => call[1]?.method === "POST");
    expect(JSON.parse(String(posted?.[1]?.body))).toMatchObject({ type: "individual", name: "Sara" });
    expect(posted?.[1]?.headers).toMatchObject({ "Idempotency-Key": "customer-key" });
    expect(await screen.findByRole("button", { name: /Sara/ })).toBeInTheDocument();
  });

  it("shows the amount due and turns an inclusive through date into an exclusive statement end", async () => {
    const user = userEvent.setup();
    const statement = {
      customerId: amin.id,
      customerName: amin.name,
      timeZoneId: "Africa/Algiers",
      from: "2026-11-01",
      to: "2026-11-02",
      openingBalance: "10.0000",
      closingBalance: "16.0000",
      outstandingAmount: "16.0000",
      lines: [
        {
          occurredAt: "2026-10-31T23:30:00Z",
          kind: "sale",
          documentId: "sale-1",
          documentNumber: "V-000001",
          amount: "10.0000",
          runningBalance: "20.0000",
        },
        {
          occurredAt: "2026-10-31T23:40:00Z",
          kind: "payment",
          documentId: "pay-1",
          documentNumber: "R-000001",
          amount: "-4.0000",
          runningBalance: "16.0000",
        },
      ],
    };
    const fetcher = vi.fn(async (input: string) => {
      if (input.includes("/statement")) {
        return json(statement);
      }
      return json([{ ...amin, outstandingAmount: "16.0000" }]);
    });
    render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" />,
    );

    await user.click(await screen.findByRole("button", { name: /16\.0000/ }));
    expect(screen.getByText("Solde dû")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Du"), { target: { value: "2026-11-01" } });
    fireEvent.change(screen.getByLabelText("Au"), { target: { value: "2026-11-01" } });
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));

    expect(await screen.findByText("20.0000")).toBeInTheDocument();
    expect(screen.getAllByText(/2026-11-01/).length).toBeGreaterThan(0);
    expect(screen.queryByText("2026-10-31T23:30:00Z")).not.toBeInTheDocument();
    expect(screen.getByText("-4.0000")).toBeInTheDocument();
    expect(screen.getByText("Vente")).toBeInTheDocument();
    expect(screen.getByText("Solde d'ouverture")).toBeInTheDocument();
    expect(screen.getByText("Solde de clôture")).toBeInTheDocument();
    const requested = fetcher.mock.calls.map((call) => String(call[0])).find((url) => url.includes("/statement"));
    expect(requested).toBe(
      "http://127.0.0.1:5080/api/v1/customers/c1/statement?from=2026-11-01&to=2026-11-02",
    );
  });

  it("keeps an empty period on screen and prints the statement section", async () => {
    const user = userEvent.setup();
    const print = vi.spyOn(window, "print").mockImplementation(() => undefined);
    const fetcher = vi.fn(async (input: string) => {
      if (input.includes("/statement")) {
        return json({
          customerId: amin.id,
          customerName: amin.name,
          timeZoneId: "Africa/Algiers",
          from: "2026-11-01",
          to: "2026-12-01",
          openingBalance: "0.0000",
          closingBalance: "0.0000",
          outstandingAmount: "0.0000",
          lines: [],
        });
      }
      return json([amin]);
    });
    render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" />,
    );

    await user.click(await screen.findByRole("button", { name: /0551000001/ }));
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    expect(await screen.findByText("Aucune opération sur cette période.")).toBeInTheDocument();
    const requested = fetcher.mock.calls.map((call) => String(call[0])).find((url) => url.includes("/statement"));
    expect(requested).toBe("http://127.0.0.1:5080/api/v1/customers/c1/statement");
    await user.click(screen.getByRole("button", { name: "Imprimer" }));
    expect(print).toHaveBeenCalledOnce();
    print.mockRestore();
  });

  it("rejects one date and a reversed range before calling the API", async () => {
    const user = userEvent.setup();
    const fetcher = vi.fn(async () => json([amin]));
    render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" />,
    );

    await user.click(await screen.findByRole("button", { name: /0551000001/ }));
    fireEvent.change(screen.getByLabelText("Du"), { target: { value: "2026-11-02" } });
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("La période du relevé est incomplète ou invalide.");
    fireEvent.change(screen.getByLabelText("Au"), { target: { value: "2026-11-01" } });
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    expect(screen.getByRole("alert")).toHaveTextContent("La date de début est postérieure à la date de fin.");
    expect(fetcher.mock.calls.some((call) => String(call[0]).includes("/statement"))).toBe(false);
  });

  it("shows an API date error and keeps Arabic amounts left to right", async () => {
    const user = userEvent.setup();
    const fetcher = vi.fn(async (input: string) => {
      if (input.includes("/statement")) {
        return json({ code: "validation.failed", errors: { from: ["statement.date"] } }, 400);
      }
      return json([{ ...amin, outstandingAmount: "4.0000" }]);
    });
    render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="ar" />,
    );

    expect(await screen.findByTestId("customers")).toHaveAttribute("dir", "rtl");
    await user.click(await screen.findByRole("button", { name: /4\.0000/ }));
    expect(screen.getAllByText("4.0000").every((node) => node.getAttribute("dir") === "ltr")).toBe(true);
    await user.click(screen.getByRole("button", { name: "عرض الكشف" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("فترة الكشف ناقصة أو غير صالحة.");
    expect(screen.getByTestId("customer-statement")).toHaveAttribute("dir", "rtl");
  });

  it("shows an integrity error instead of a negative amount due", async () => {
    const user = userEvent.setup();
    render(
      <CustomersPanel
        apiBaseUrl="http://127.0.0.1:5080"
        accessToken="token"
        fetcher={async () => json([
          { ...amin, outstandingAmount: "-2.0000" },
          { ...otherAmin, name: "Lina", outstandingAmount: "3.0000" },
        ])}
        locale="fr"
      />,
    );

    const inconsistent = await screen.findByRole("button", { name: /Amin/ });
    expect(inconsistent).toHaveTextContent("Le solde du client est incohérent.");
    expect(inconsistent).not.toHaveTextContent("-2.0000");
    expect(inconsistent).not.toHaveTextContent("Solde dû");
    expect(screen.getByRole("button", { name: /Lina/ })).toHaveTextContent("3.0000");
    await user.click(inconsistent);
    const summary = screen.getByTestId("customer-statement");
    expect(summary).toHaveTextContent("Le solde du client est incohérent.");
    expect(summary).not.toHaveTextContent("-2.0000");
    expect(summary).not.toHaveTextContent("Solde dû");
  });

  it("shows the statement customer name on screen and in the printed statement", async () => {
    const user = userEvent.setup();
    const fetcher = vi.fn(async (input: string) => {
      if (String(input).includes("/statement")) {
        return json({
          customerId: amin.id,
          customerName: "Sara Benali",
          timeZoneId: "Africa/Algiers",
          from: "2026-11-01",
          to: "2026-12-01",
          openingBalance: "0.0000",
          closingBalance: "0.0000",
          outstandingAmount: "0.0000",
          lines: [],
        });
      }
      return json([amin, { ...otherAmin, name: "Lina", phone: "0551999999", address: "Rue 9" }]);
    });
    render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" />,
    );

    await user.click(await screen.findByRole("button", { name: /0551000001/ }));
    expect(screen.getByTestId("customer-statement")).not.toHaveTextContent("Sara Benali");
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    const name = await screen.findByTestId("customer-statement-name");
    expect(name).toHaveTextContent("Nom du client");
    expect(name).toHaveTextContent("Sara Benali");
    expect(name).not.toHaveTextContent("0551000001");
    expect(name).not.toHaveTextContent("Lina");
    expect(name).not.toHaveTextContent("Rue 9");
    const statement = screen.getByTestId("customer-statement");
    expect(statement).toContainElement(name);
    expect(printVisibility(name, customerStatementPrintCss)).toBe("visible");
    expect(translate("fr", "customers.name")).toBe("Nom du client");
    expect(translate("ar", "customers.name")).toBe("اسم الزبون");
    expect(translate("en", "customers.name")).toBe("Customer name");
  });

  it("prints only the statement and leaves other customers out of that section", async () => {
    const user = userEvent.setup();
    const fetcher = vi.fn(async (input: string) => {
      if (String(input).includes("/statement")) {
        return json({
          customerId: amin.id,
          customerName: amin.name,
          timeZoneId: "Africa/Algiers",
          from: "2026-11-01",
          to: "2026-12-01",
          openingBalance: "10.0000",
          closingBalance: "10.0000",
          outstandingAmount: "10.0000",
          lines: [],
        });
      }
      return json([amin, { ...otherAmin, name: "Lina", phone: "0551999999" }]);
    });
    const { container } = render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" />,
    );

    expect(container.querySelector("style")?.textContent).toBe(customerStatementPrintCss);
    await user.click(await screen.findByRole("button", { name: /0551000001/ }));
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    const statement = await screen.findByTestId("customer-statement");
    expect(statement).toHaveAttribute("dir", "ltr");
    expect(statement).toHaveTextContent("10.0000");
    expect(statement).not.toHaveTextContent("Lina");
    expect(statement).not.toHaveTextContent("0551999999");
    expect(screen.getByRole("button", { name: /0551999999/ })).toBeInTheDocument();
    expect(printVisibility(statement, customerStatementPrintCss)).toBe("visible");
    expect(printVisibility(screen.getByRole("button", { name: /0551999999/ }), customerStatementPrintCss)).toBe("hidden");
  });

  it("formats an Algiers instant onto the next civil date", () => {
    expect(formatStatementInstant("2026-10-31T23:30:00Z", "Africa/Algiers")).toBe("2026-11-01 00:30");
    expect(formatStatementInstant("2026-10-31T22:30:00Z", "Africa/Algiers")).toBe("2026-10-31 23:30");
  });

  it("translates an invalid company time zone in French, Arabic, and English", () => {
    expect(translate("fr", "timeZoneId.invalid")).toBe("Le fuseau horaire de l'entreprise n'est pas valide.");
    expect(translate("ar", "timeZoneId.invalid")).toBe("المنطقة الزمنية للشركة غير صالحة.");
    expect(translate("en", "timeZoneId.invalid")).toBe("The company time zone is invalid.");
    expect(translate("fr", "sale.payment_integrity")).toBe("Le solde du client est incohérent.");
    expect(translate("ar", "sale.payment_integrity")).toBe("رصيد الزبون غير متسق.");
    expect(translate("en", "sale.payment_integrity")).toBe("The customer balance is inconsistent.");
  });

  it("refreshes the outstanding amount and the open statement after a sale return", async () => {
    const user = userEvent.setup();
    let outstanding = "15.0000";
    const statementFor = (amount: string) => ({
      customerId: amin.id,
      customerName: amin.name,
      timeZoneId: "Africa/Algiers",
      from: "2026-11-01",
      to: "2026-12-01",
      openingBalance: "0.0000",
      closingBalance: amount,
      outstandingAmount: amount,
      lines: [
        { occurredAt: "2026-11-08T10:00:00Z", kind: "sale", documentId: "sale-1", documentNumber: "V-000020", amount: "40.0000", runningBalance: "40.0000" },
        ...(amount === "5.0000"
          ? [{ occurredAt: "2026-11-08T11:00:00Z", kind: "return", documentId: "ret-1", documentNumber: "AV-000001", amount: "-10.0000", runningBalance: "5.0000" }]
          : []),
      ],
    });
    const fetcher = vi.fn(async (input: string) => {
      if (input.includes("/statement")) {
        return json(statementFor(outstanding));
      }
      return json([{ ...amin, outstandingAmount: outstanding }]);
    });
    const view = render(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" revision={0} />,
    );
    await user.click(await screen.findByRole("button", { name: /15\.0000/ }));
    await user.click(screen.getByRole("button", { name: "Afficher le relevé" }));
    expect(await screen.findByText("V-000020")).toBeInTheDocument();

    outstanding = "5.0000";
    view.rerender(
      <CustomersPanel apiBaseUrl="http://127.0.0.1:5080" accessToken="token" fetcher={fetcher} locale="fr" revision={1} />,
    );
    expect(await screen.findByText("AV-000001")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /5\.0000/ })).toBeInTheDocument();
    expect(screen.getAllByText("Retour")).toHaveLength(1);
    expect(fetcher.mock.calls.filter((call) => String(call[0]).includes("/statement"))).toHaveLength(2);
  });
});

function printVisibility(element: Element, css: string): "visible" | "hidden" {
  const hidesPage = /body \* \{[^}]*visibility:\s*hidden !important;/.test(css);
  const showsStatement = /\[data-testid="customer-statement"\] \* \{[^}]*visibility:\s*visible !important;/.test(css);
  if (!hidesPage || !showsStatement) {
    return "visible";
  }

  return element.closest("[data-testid='customer-statement']") ? "visible" : "hidden";
}
