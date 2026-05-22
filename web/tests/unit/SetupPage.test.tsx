import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, test, vi } from "vitest";
import { SetupPage } from "@/app/SetupPage";
import "@/lib/i18n";

function renderAt(url = "/setup") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/setup" element={<SetupPage />} />
        <Route
          path="/t/:slug/verify-email-prompt"
          element={<div data-testid="prompt-page">prompt</div>}
        />
      </Routes>
    </MemoryRouter>,
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
  test("renders the form", () => {
    renderAt();
    expect(screen.getByTestId("setup-form")).toBeInTheDocument();
    expect(screen.getByTestId("setup-bootstrap-token")).toBeInTheDocument();
  });

  test("posts to /api/setup/complete and navigates to verify-email-prompt on 201", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          tenantId: 1,
          tenantSlug: "acme",
          ownerUserId: 1,
          verificationEmailSent: true,
        }),
        { status: 201, headers: { "content-type": "application/json" } },
      ),
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
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "invalid_bootstrap_token" }), {
        status: 401,
        headers: { "content-type": "application/json" },
      }),
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
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "setup_already_completed" }), {
        status: 410,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt();
    fillValidForm();
    await user.click(screen.getByTestId("setup-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("setup-server-error")).toBeInTheDocument();
    });
  });
});
