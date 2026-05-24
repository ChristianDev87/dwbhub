import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { I18nextProvider } from "react-i18next";
import { i18n } from "../../src/lib/i18n";
import { AuthContext } from "../../src/app/auth-context";
import { BotTokenModal } from "../../src/app/BotTokenModal";

const VALID_TOKEN =
  "TestTokenSegment0000000000000000000000.NotReal.TestTokenFinalSegment000000000000000";

const fakeAuthValue = {
  state: {
    kind: "authenticated" as const,
    accessToken: "test-access-token",
    user: { id: 1, email: "owner@acme.test", displayName: "Owner", role: "Owner" },
    tenant: { id: 1, slug: "acme", name: "ACME" },
  },
  login: vi.fn(),
  logout: vi.fn(),
  refresh: vi.fn(),
};

function renderModal(
  overrides: {
    onClose?: () => void;
    onSuccess?: () => void;
    mode?: "configure" | "rotate";
  } = {},
) {
  const onClose = overrides.onClose ?? vi.fn();
  const onSuccess = overrides.onSuccess ?? vi.fn();
  return {
    onClose,
    onSuccess,
    ...render(
      <AuthContext.Provider value={fakeAuthValue}>
        <I18nextProvider i18n={i18n}>
          <BotTokenModal
            slug="acme"
            guildPublicId="11111111-1111-1111-1111-111111111111"
            guildDisplayName="Production"
            mode={overrides.mode ?? "configure"}
            onClose={onClose}
            onSuccess={onSuccess}
          />
        </I18nextProvider>
      </AuthContext.Provider>,
    ),
  };
}

describe("BotTokenModal", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("rejects empty token", async () => {
    renderModal();
    fireEvent.click(screen.getByTestId("bot-token-save"));
    await waitFor(() => {
      expect(screen.getByTestId("error-bot-token")).toBeInTheDocument();
    });
  });

  it("rejects invalid format token", async () => {
    renderModal();
    fireEvent.change(screen.getByTestId("input-bot-token"), {
      target: { value: "too-short.bad" },
    });
    fireEvent.click(screen.getByTestId("bot-token-save"));
    await waitFor(() => {
      expect(screen.getByTestId("error-bot-token")).toBeInTheDocument();
    });
  });

  it("calls PUT and triggers onSuccess on 204", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 204 });
    vi.stubGlobal("fetch", fetchMock);

    const { onSuccess, onClose } = renderModal();
    fireEvent.change(screen.getByTestId("input-bot-token"), {
      target: { value: VALID_TOKEN },
    });
    fireEvent.click(screen.getByTestId("bot-token-save"));

    await waitFor(() => expect(onSuccess).toHaveBeenCalledTimes(1));
    expect(onClose).toHaveBeenCalled();
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(
      "/api/t/acme/guilds/11111111-1111-1111-1111-111111111111/bot-credentials",
    );
    expect(init?.method).toBe("PUT");
    expect(JSON.parse((init?.body as string) ?? "{}")).toEqual({
      token: VALID_TOKEN,
    });
  });

  it("shows error on 400 response", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 400,
      json: async () => ({ error: "invalid_bot_token_format" }),
    });
    vi.stubGlobal("fetch", fetchMock);

    renderModal();
    fireEvent.change(screen.getByTestId("input-bot-token"), {
      target: { value: VALID_TOKEN },
    });
    fireEvent.click(screen.getByTestId("bot-token-save"));
    await waitFor(() => {
      expect(screen.getByTestId("error-bot-token-submit")).toBeInTheDocument();
    });
  });

  it("shows error on 403 (owner role required)", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 403,
      json: async () => ({ error: "forbidden" }),
    });
    vi.stubGlobal("fetch", fetchMock);

    renderModal();
    fireEvent.change(screen.getByTestId("input-bot-token"), {
      target: { value: VALID_TOKEN },
    });
    fireEvent.click(screen.getByTestId("bot-token-save"));
    await waitFor(() => {
      expect(screen.getByTestId("error-bot-token-submit")).toBeInTheDocument();
    });
  });
});
