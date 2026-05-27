import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, test, vi, beforeEach, afterEach } from "vitest";
import { createApiClient } from "../../src/lib/api/client";
import { VerifyEmailPage } from "@/app/VerifyEmailPage";
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
  useApiClient: () =>
    createApiClient(
      () => null,
      () => Promise.resolve(null),
      "http://localhost/",
    ),
}));

function getUrl(u: RequestInfo | URL): string {
  if (u && typeof u === "object" && "url" in u) return (u as Request).url;
  return String(u);
}

function makeWrapper(url: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

function renderAt(url: string) {
  const Wrapper = makeWrapper(url);
  return render(
    <Wrapper>
      <Routes>
        <Route path="/t/:slug/verify-email" element={<VerifyEmailPage />} />
      </Routes>
    </Wrapper>,
  );
}

describe("VerifyEmailPage", () => {
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

  test("shows error when token is missing", () => {
    renderAt("/t/acme/verify-email");
    expect(screen.getByTestId("verify-error")).toBeInTheDocument();
  });

  test("shows pending then success on 200 response", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/verify-email/confirm"))
          return new Response(JSON.stringify({ verified: true }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt("/t/acme/verify-email?token=abc123");

    expect(screen.getByTestId("verify-pending")).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByTestId("verify-success")).toBeInTheDocument();
    });

    expect(screen.getByTestId("verify-go-to-login")).toHaveAttribute(
      "href",
      "/t/acme/login",
    );
  });

  test("shows error on 400 response", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/verify-email/confirm"))
          return new Response(
            JSON.stringify({ error: "invalid_or_expired_token" }),
            { status: 400, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt("/t/acme/verify-email?token=bad");

    await waitFor(() => {
      expect(screen.getByTestId("verify-error")).toBeInTheDocument();
    });
  });

  test("shows error when fetch throws", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/verify-email/confirm"))
          throw new Error("network down");
        throw new Error(`unexpected fetch: ${url}`);
      },
    );

    renderAt("/t/acme/verify-email?token=abc");

    await waitFor(() => {
      expect(screen.getByTestId("verify-error")).toBeInTheDocument();
    });
  });

  test("posts the token to the correct endpoint", async () => {
    const fetchSpy = fetch as ReturnType<typeof vi.fn>;
    fetchSpy.mockImplementation(async (u: RequestInfo | URL) => {
      const url = getUrl(u);
      if (url.includes("/api/auth/verify-email/confirm"))
        return new Response("{}", {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      throw new Error(`unexpected fetch: ${url}`);
    });

    renderAt("/t/acme/verify-email?token=xyz-9");

    await waitFor(() => {
      const confirmCall = fetchSpy.mock.calls.find((c: unknown[]) =>
        getUrl(c[0] as RequestInfo | URL).includes(
          "/api/auth/verify-email/confirm",
        ),
      );
      expect(confirmCall).toBeDefined();
    });

    const confirmCall = fetchSpy.mock.calls.find((c: unknown[]) =>
      getUrl(c[0] as RequestInfo | URL).includes(
        "/api/auth/verify-email/confirm",
      ),
    )!;
    const req = confirmCall[0] as Request;
    expect(req.method).toBe("POST");
    // openapi-fetch encodes the body into the Request object itself.
    const body = await req.json();
    expect(body).toEqual({ token: "xyz-9" });
  });
});
