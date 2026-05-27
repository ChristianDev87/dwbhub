import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, test, vi, beforeEach, afterEach } from "vitest";
import { createApiClient } from "../../src/lib/api/client";
import { SetupPage } from "@/app/SetupPage";
import "@/lib/i18n";

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

function makeWrapper(url = "/setup") {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

function renderAt(url = "/setup") {
  const Wrapper = makeWrapper(url);
  return render(
    <Wrapper>
      <Routes>
        <Route path="/setup" element={<SetupPage />} />
        <Route
          path="/t/:slug/verify-email-prompt"
          element={<div data-testid="prompt-page">prompt</div>}
        />
      </Routes>
    </Wrapper>,
  );
}

function fillValidForm() {
  fireEvent.change(screen.getByTestId("setup-bootstrap-token"), {
    target: { value: "valid-token" },
  });
  fireEvent.change(screen.getByTestId("setup-tenant-name"), {
    target: { value: "Acme Corp" },
  });
  fireEvent.change(screen.getByTestId("setup-tenant-slug"), {
    target: { value: "acme" },
  });
  fireEvent.change(screen.getByTestId("setup-owner-email"), {
    target: { value: "owner@acme.test" },
  });
  fireEvent.change(screen.getByTestId("setup-owner-display-name"), {
    target: { value: "Owner" },
  });
  fireEvent.change(screen.getByTestId("setup-owner-password"), {
    target: { value: "validpassword" },
  });
}

describe("SetupPage", () => {
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

  test("renders the form", () => {
    renderAt();
    expect(screen.getByTestId("setup-form")).toBeInTheDocument();
    expect(screen.getByTestId("setup-bootstrap-token")).toBeInTheDocument();
  });

  test("posts to /api/setup/complete and navigates to verify-email-prompt on 201", async () => {
    const user = userEvent.setup();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/setup/complete"))
          return new Response(
            JSON.stringify({
              tenantId: 1,
              tenantSlug: "acme",
              ownerUserId: 1,
              verificationEmailSent: true,
            }),
            { status: 201, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt();
    fillValidForm();
    await user.click(screen.getByTestId("setup-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("prompt-page")).toBeInTheDocument();
    });
  });

  test("shows server-side error on 401 invalid_bootstrap_token", async () => {
    const user = userEvent.setup();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/setup/complete"))
          return new Response(
            JSON.stringify({ error: "invalid_bootstrap_token" }),
            { status: 401, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt();
    fillValidForm();
    await user.click(screen.getByTestId("setup-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("setup-server-error")).toBeInTheDocument();
    });
  });

  test("shows server-side error on 410 setup_already_completed", async () => {
    const user = userEvent.setup();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/setup/complete"))
          return new Response(
            JSON.stringify({ error: "setup_already_completed" }),
            { status: 410, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt();
    fillValidForm();
    await user.click(screen.getByTestId("setup-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("setup-server-error")).toBeInTheDocument();
    });
  });
});
