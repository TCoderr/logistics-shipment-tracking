import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { ApiError } from "./api/http";
import { App } from "./App";
import "./styles/app.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // 4xx yanıtlar kesin sonuçtur (yetki, bulunamadı, doğrulama); yalnız ağ/5xx hataları bir kez yeniden denenir.
      retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 1,
      staleTime: 0,
      refetchOnWindowFocus: true,
    },
    mutations: { retry: false },
  },
});

const root = document.getElementById("root");
if (!root) throw new Error("#root bulunamadı");

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      {/* Konum güncellemeleri geçişe (startTransition) ertelenmez: filtreler ve arama URL'ye bağlı kontrollü girdilerdir;
          ertelenirse tıklanan kutu geri alınır, art arda gelen filtre değişiklikleri birbirini ezer ve liste klavye odağının
          altından sonradan değişir. setSearchParams'ın flushSync seçeneği BrowserRouter'da etkisizdir. */}
      <BrowserRouter useTransitions={false}>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
