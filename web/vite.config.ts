/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  build: {
    target: "esnext",
  },
  server: {
    host: "0.0.0.0",
    port: 5173,
    // Vite v6 blocks requests where the Host header is not in `allowedHosts`
    // (default: only localhost / 127.0.0.1). In containerised dev/e2e scenarios,
    // the test runner reaches the dev server via host.docker.internal:<port>,
    // which Vite would otherwise reject with HTTP 403. Allowing all hosts is
    // safe here — this is the DEV server only; production uses the nginx
    // Dockerfile.web image with its own host config.
    allowedHosts: ["host.docker.internal", ".localhost", ".local"],
    // Proxy target: defaults to localhost:5080 (host-side `pnpm dev` against a
    // host-published API on :5080). When Vite runs inside the dev-stack `web`
    // container, the API is reachable as http://api:8080 on the compose network
    // and `localhost:5080` resolves back to the web container itself. The dev
    // compose file sets DWBHUB_PROXY_API_TARGET so the proxy hits the right host.
    proxy: {
      // ws:true is required so that SignalR's WebSocket transport on /api/hubs/*
      // can be proxied. Without it, Vite only proxies HTTP on /api; SignalR falls
      // back to Long Polling (~5 s negotiation delay) and e2e assertions that wait
      // for SignalR-confirmed state (pending cleared, edit/delete reflected) time out.
      "/api": {
        target: process.env.DWBHUB_PROXY_API_TARGET || "http://localhost:5080",
        ws: true,
      },
      "/hub": {
        target: process.env.DWBHUB_PROXY_API_TARGET || "http://localhost:5080",
        ws: true,
      },
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./tests/setup.ts"],
    css: true,
    include: ["tests/unit/**/*.{test,spec}.{ts,tsx}"],
    exclude: ["tests/e2e/**", "node_modules/**", "dist/**"],
  },
});
