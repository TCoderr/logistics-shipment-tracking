import type { ExceptionFilters, ShipmentListFilters } from "../api/types";

// Sorgu anahtarları tek yerde: işlem sonrası ve (dilim 10) canlı bildirimde hangi sorguların tazeleneceği buradan belirlenir.
export const queryKeys = {
  hubs: ["reference", "hubs"] as const,
  carriers: ["reference", "carriers"] as const,
  shipmentsRoot: ["shipments"] as const,
  shipments: (filters: ShipmentListFilters) => ["shipments", "list", filters] as const,
  shipment: (id: string) => ["shipments", "detail", id] as const,
  shipmentAudit: (id: string) => ["shipments", "audit", id] as const,
  shipmentReceipts: (id: string) => ["shipments", "receipts", id] as const,
  exceptionsRoot: ["exceptions"] as const,
  exceptions: (filters: ExceptionFilters) => ["exceptions", "list", filters] as const,
  summaryRoot: ["summary"] as const,
  summary: (hubCode: string | undefined, date: string | undefined) => ["summary", hubCode ?? null, date ?? null] as const,
  simulator: ["simulator"] as const,
};
