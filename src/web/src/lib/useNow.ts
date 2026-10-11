import { useEffect, useState } from "react";

/** Göreli süre gösterimleri (vade, bayat veri) için belirli aralıkla güncellenen "şimdi". */
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(timer);
  }, [intervalMs]);
  return now;
}
