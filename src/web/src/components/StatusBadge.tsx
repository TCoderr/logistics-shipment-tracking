import type { ShipmentStatus } from "../api/types";
import { Icon, type IconName } from "./Icon";

interface StatusStyle {
  label: string;
  tone: "good" | "info" | "warn" | "serious" | "critical" | "neutral";
  icon: IconName;
}

// docs/07 §3: her durum = şekil + Türkçe metin + ton. Renk tek başına bilgi taşımaz.
export const shipmentStatusStyles: Record<ShipmentStatus, StatusStyle> = {
  Created: { label: "Kaydedildi", tone: "neutral", icon: "created" },
  AtHub: { label: "Hub'da", tone: "info", icon: "athub" },
  InTransit: { label: "Yolda", tone: "info", icon: "transit" },
  OutForDelivery: { label: "Dağıtımda", tone: "info", icon: "ofd" },
  DeliveryFailed: { label: "Teslim edilemedi", tone: "serious", icon: "failed" },
  Delivered: { label: "Teslim edildi", tone: "good", icon: "delivered" },
  ReturningToSender: { label: "İadede", tone: "warn", icon: "returning" },
  ReturnedToSender: { label: "İade edildi", tone: "neutral", icon: "returned" },
  Cancelled: { label: "İptal", tone: "neutral", icon: "cancelled" },
};

export function shipmentStatusLabel(status: string): string {
  return shipmentStatusStyles[status as ShipmentStatus]?.label ?? status;
}

export function StatusBadge({ status }: { status: string }) {
  const style = shipmentStatusStyles[status as ShipmentStatus] ?? { label: status, tone: "neutral", icon: "dot" as const };
  return (
    <span className={`status t-${style.tone}`}>
      <Icon name={style.icon} />
      {style.label}
    </span>
  );
}
