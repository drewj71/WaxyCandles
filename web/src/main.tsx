import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import {
  RouterProvider,
} from "@tanstack/react-router";
import { router } from "./router";
import "./index.css";
import {
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query";
import { shouldRetryQuery } from "./api/errors";

const queryClient = new QueryClient({
  defaultOptions: {
    // No retries on 4xx/429 or the market data provider's 503/504 (billed).
    queries: { retry: shouldRetryQuery },
    // Never retry a write automatically; the user decides.
    mutations: { retry: false },
  },
});

createRoot(
  document.getElementById("root")!,
).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);