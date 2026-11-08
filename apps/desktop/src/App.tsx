import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { CatalogPanel } from "./CatalogPanel";
import { CustomersPanel } from "./CustomersPanel";
import { PosPanel } from "./PosPanel";
import { PurchasingPanel } from "./PurchasingPanel";
import { StockPanel } from "./StockPanel";
import { HealthPanel } from "./HealthPanel";
import { direction, translate, type Locale } from "./i18n";
import { secureStore, type CredentialStore, type SecureStore } from "./secureStore";

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

type Session = {
  accessToken: string;
  companyName: string;
  credentialStore: CredentialStore;
};

type AppProps = {
  apiBaseUrl?: string;
  fetcher?: Fetcher;
  store?: SecureStore;
  initialLocale?: Locale;
};

type ProblemBody = {
  code?: string;
  errors?: Record<string, string[]>;
};

const locales: Locale[] = ["fr", "ar", "en"];

export default function App({
  apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "http://127.0.0.1:5080",
  fetcher = fetch,
  store = secureStore,
  initialLocale = "fr",
}: AppProps) {
  const [locale, setLocale] = useState<Locale>(initialLocale);
  const [screen, setScreen] = useState<"loading" | "setup" | "login" | "workspace">("loading");
  const [session, setSession] = useState<Session | null>(null);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [legalName, setLegalName] = useState("");
  const [currencyCode, setCurrencyCode] = useState("DZD");
  const [taxRate, setTaxRate] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [salesRevision, setSalesRevision] = useState(0);

  useEffect(() => {
    document.documentElement.lang = locale;
    document.documentElement.dir = direction(locale);
  }, [locale]);

  useEffect(() => {
    let cancelled = false;

    async function boot() {
      const stored = await store.load();
      if (stored) {
        const refreshed = await postJson(fetcher, `${apiBaseUrl}/api/v1/auth/refresh`, {
          refreshToken: stored,
        });
        if (!cancelled && refreshed.response.ok) {
          const body = (await refreshed.response.json()) as { accessToken: string; refreshToken: string };
          const where = await store.save(body.refreshToken);
          const company = await loadCompany(fetcher, apiBaseUrl, body.accessToken);
          if (!cancelled && company) {
            setSession({ accessToken: body.accessToken, companyName: company, credentialStore: where });
            setScreen("workspace");
            return;
          }
        } else if (!cancelled) {
          await store.clear();
        }
      }

      try {
        const status = await fetcher(`${apiBaseUrl}/api/v1/setup/status`);
        if (!status.ok) {
          throw new Error("status");
        }
        const body = (await status.json()) as { complete?: boolean };
        if (!cancelled) {
          setScreen(body.complete ? "login" : "setup");
        }
      } catch {
        if (!cancelled) {
          setErrorCode("setup.status_unavailable");
        }
      }
    }

    void boot();
    return () => {
      cancelled = true;
    };
  }, [apiBaseUrl, fetcher, store]);

  async function submitSetup(event: FormEvent) {
    event.preventDefault();
    if (password.length < 12) {
      setErrorCode("password.too_short");
      return;
    }
    if (legalName.trim() === "") {
      setErrorCode("legalName.required");
      return;
    }
    if (taxRate.trim() === "") {
      setErrorCode("defaultTaxRate.required");
      return;
    }

    setErrorCode(null);
    const result = await postJson(fetcher, `${apiBaseUrl}/api/v1/setup`, {
      legalName,
      currencyCode,
      defaultTaxRate: taxRate,
      email,
      password,
    });
    if (!result.response.ok) {
      setErrorCode((await problemCode(result.response)) ?? "validation.failed");
      return;
    }

    setPassword("");
    setScreen("login");
  }

  async function submitLogin(event: FormEvent) {
    event.preventDefault();
    setErrorCode(null);
    const result = await postJson(fetcher, `${apiBaseUrl}/api/v1/auth/login`, { email, password });
    if (!result.response.ok) {
      setErrorCode((await problemCode(result.response)) ?? "auth.invalid_credentials");
      return;
    }

    const body = (await result.response.json()) as { accessToken: string; refreshToken: string };
    const where = await store.save(body.refreshToken);
    const company = await loadCompany(fetcher, apiBaseUrl, body.accessToken);
    setPassword("");
    setSession({
      accessToken: body.accessToken,
      companyName: company ?? "",
      credentialStore: where,
    });
    setScreen("workspace");
  }

  async function logout() {
    if (session) {
      const stored = await store.load();
      if (stored) {
        await postJson(fetcher, `${apiBaseUrl}/api/v1/auth/logout`, { refreshToken: stored });
      }
    }
    await store.clear();
    setSession(null);
    setScreen("login");
  }

  const text = (key: string) => translate(locale, key);

  return (
    <main className="min-h-screen bg-stone-100 text-start text-stone-900">
      <section className={`mx-auto flex w-full flex-col gap-6 px-6 py-10 ${screen === "workspace" ? "max-w-6xl" : "max-w-lg"}`}>
        <div className="flex gap-2">
          {locales.map((item) => (
            <button
              key={item}
              type="button"
              lang={item}
              className="border border-stone-400 bg-white px-2 py-1 text-sm"
              onClick={() => setLocale(item)}
            >
              {text(`language.${item}`)}
            </button>
          ))}
        </div>
        <HealthPanel
          apiBaseUrl={apiBaseUrl}
          fetcher={fetcher}
          embedded
          copy={{
            intro: text("health.intro"),
            api: text("health.api"),
            database: text("health.database"),
            status: text("health.status"),
            checking: text("health.checking"),
            checkAgain: text("health.checkAgain"),
          }}
        />
        {screen === "loading" ? <p>…</p> : null}
        {screen === "loading" && errorCode ? <p role="alert">{text(errorCode)}</p> : null}
        {screen === "setup" ? (
          <form className="flex flex-col gap-3" onSubmit={submitSetup} data-testid="screen-setup">
            <h1 className="text-2xl font-semibold">{text("screen.setup")}</h1>
            <Label text={text("field.legalName")}>
              <input className="w-full border border-stone-400 px-2 py-1" value={legalName} onChange={(event) => setLegalName(event.target.value)} />
            </Label>
            <Label text={text("field.currency")}>
              <input className="w-full border border-stone-400 px-2 py-1" value={currencyCode} onChange={(event) => setCurrencyCode(event.target.value)} />
            </Label>
            <Label text={text("field.taxRate")}>
              <input className="w-full border border-stone-400 px-2 py-1" value={taxRate} onChange={(event) => setTaxRate(event.target.value)} />
            </Label>
            <Label text={text("field.email")}>
              <input className="w-full border border-stone-400 px-2 py-1" value={email} onChange={(event) => setEmail(event.target.value)} />
            </Label>
            <Label text={text("field.password")}>
              <input type="password" className="w-full border border-stone-400 px-2 py-1" value={password} onChange={(event) => setPassword(event.target.value)} />
            </Label>
            {errorCode ? <p role="alert">{text(errorCode)}</p> : null}
            <button type="submit" className="w-fit border border-stone-500 bg-white px-3 py-1.5 text-sm">
              {text("action.setup")}
            </button>
          </form>
        ) : null}
        {screen === "login" ? (
          <form className="flex flex-col gap-3" onSubmit={submitLogin} data-testid="screen-login">
            <h1 className="text-2xl font-semibold">{text("screen.login")}</h1>
            <Label text={text("field.email")}>
              <input className="w-full border border-stone-400 px-2 py-1" value={email} onChange={(event) => setEmail(event.target.value)} />
            </Label>
            <Label text={text("field.password")}>
              <input type="password" className="w-full border border-stone-400 px-2 py-1" value={password} onChange={(event) => setPassword(event.target.value)} />
            </Label>
            {errorCode ? <p role="alert">{text(errorCode)}</p> : null}
            <button type="submit" className="w-fit border border-stone-500 bg-white px-3 py-1.5 text-sm">
              {text("action.login")}
            </button>
          </form>
        ) : null}
        {screen === "workspace" && session ? (
          <section data-testid="screen-workspace" className="flex flex-col gap-3">
            <h1 className="text-2xl font-semibold">{session.companyName}</h1>
            {session.credentialStore === "memory" ? <p>{text("credential.memory")}</p> : null}
            <button type="button" className="w-fit border border-stone-500 bg-white px-3 py-1.5 text-sm" onClick={() => void logout()}>
              {text("action.logout")}
            </button>
            <PosPanel
              apiBaseUrl={apiBaseUrl}
              accessToken={session.accessToken}
              fetcher={fetcher}
              locale={locale}
              administrator={isAdministrator(session.accessToken)}
              onSalesChanged={() => setSalesRevision((current) => current + 1)}
            />
            <CustomersPanel
              apiBaseUrl={apiBaseUrl}
              accessToken={session.accessToken}
              fetcher={fetcher}
              locale={locale}
              administrator={isAdministrator(session.accessToken)}
              revision={salesRevision}
            />
            <CatalogPanel
              apiBaseUrl={apiBaseUrl}
              accessToken={session.accessToken}
              fetcher={fetcher}
              locale={locale}
            />
            <StockPanel
              apiBaseUrl={apiBaseUrl}
              accessToken={session.accessToken}
              fetcher={fetcher}
              locale={locale}
            />
            <PurchasingPanel
              apiBaseUrl={apiBaseUrl}
              accessToken={session.accessToken}
              fetcher={fetcher}
              locale={locale}
            />
          </section>
        ) : null}
      </section>
    </main>
  );
}

function Label({ text, children }: { text: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-sm">
      <span>{text}</span>
      {children}
    </label>
  );
}

async function postJson(fetcher: Fetcher, url: string, body: unknown): Promise<{ response: Response }> {
  const response = await fetcher(url, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  return { response };
}

async function problemCode(response: Response): Promise<string | null> {
  const body = (await response.json()) as ProblemBody;
  const fieldCode = body.errors ? Object.values(body.errors)[0]?.[0] : undefined;
  return fieldCode ?? body.code ?? null;
}

async function loadCompany(fetcher: Fetcher, apiBaseUrl: string, accessToken: string): Promise<string | null> {
  const response = await fetcher(`${apiBaseUrl}/api/v1/company`, {
    headers: { authorization: `Bearer ${accessToken}` },
  });
  if (!response.ok) {
    return null;
  }

  const body = (await response.json()) as { legalName?: string };
  return body.legalName ?? null;
}

function isAdministrator(accessToken: string): boolean {
  const payload = accessToken.split(".")[1];
  if (!payload) {
    return false;
  }

  try {
    const claims = JSON.parse(atob(payload.replace(/-/g, "+").replace(/_/g, "/"))) as { role?: string | string[] };
    return claims.role === "Administrator" || (Array.isArray(claims.role) && claims.role.includes("Administrator"));
  } catch {
    return false;
  }
}
