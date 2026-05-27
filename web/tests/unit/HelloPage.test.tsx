import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, test, vi, beforeEach, afterEach } from "vitest";
import { createApiClient } from "../../src/lib/api/client";
import { HelloPage } from "@/app/HelloPage";
import "@/lib/i18n";
// TODO(tests): once test-file count grows beyond ~3, hoist this i18n init into
// web/tests/setup.ts so the LanguageDetector + initReactI18next chain runs once
// per Vitest process instead of once per test file.

/**
 * openapi-fetch internally calls `new URL(path, baseUrl)`. In the jsdom test
 * environment baseUrl: "/" is not a valid absolute URL and throws TypeError.
 * We mock useApiClient to supply a client with an absolute localhost baseUrl
 * so URL construction succeeds, while globalThis.fetch is stubbed separately
 * to intercept calls without hitting the network.
 *
 * openapi-fetch passes a Request object as the first arg to fetch; we extract
 * the URL via duck-typing.
 */
vi.mock("@/lib/api/useApiClient", () => ({
  useApiClient: () => createApiClient(null, "http://localhost/"),
}));

function getUrl(u: RequestInfo | URL): string {
  if (u && typeof u === "object" && "url" in u) return (u as Request).url;
  return String(u);
}

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

function renderPage() {
  const Wrapper = makeWrapper();
  return render(<HelloPage />, { wrapper: Wrapper });
}

describe("HelloPage", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        throw new Error(`unexpected fetch: ${getUrl(u)}`);
      },
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  test("renders translated heading", () => {
    // Heading is static and does not depend on fetch settlement; a never-resolving
    // promise for /api/health keeps the component in the "loading" state so no
    // act() warning fires.
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/health"))
          return new Promise<Response>(() => {});
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderPage();

    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      /Hallo DwbHub|Hello DwbHub/,
    );
  });

  test("shows api status OK after successful fetch", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/health"))
          return new Response(
            JSON.stringify({ status: "ok", version: "1.2.3", uptime_seconds: 99 }),
            { status: 200, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId("api-status-ok")).toBeInTheDocument();
    });

    expect(screen.getByTestId("api-status-ok").textContent).toMatch(/1\.2\.3/);
    expect(screen.getByTestId("api-status-ok").textContent).toMatch(/99/);
  });

  test("shows error state when fetch fails", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/health"))
          throw new Error("network down");
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderPage();

    await waitFor(() => {
      expect(screen.getByTestId("api-status-error")).toBeInTheDocument();
    });

    expect(screen.getByTestId("api-status-error").textContent).toMatch(
      /\(network down\)/,
    );
  });
});
