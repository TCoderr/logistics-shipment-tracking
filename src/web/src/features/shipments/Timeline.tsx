import { useState } from "react";
import type { TimelineItem, TrackingEventType } from "../../api/types";
import { Icon, type IconName } from "../../components/Icon";
import { formatDateTime, formatTime } from "../../lib/format";
import { eventSourceLabels, eventTypeLabels, reasonLabel } from "../../lib/labels";

const eventIcons: Record<TrackingEventType, IconName> = {
  Created: "created",
  ArrivedAtHub: "athub",
  DepartedHub: "transit",
  HandedToCarrier: "transit",
  CarrierInTransit: "transit",
  OutForDelivery: "ofd",
  DeliveryFailed: "failed",
  Delivered: "delivered",
  ReturnInitiated: "returning",
  ReturnedToSender: "returned",
  Cancelled: "cancelled",
};

const lateReceiptMs = 10 * 60_000;

interface Row {
  first: TimelineItem;
  count: number;
}

/** Aynı hub'da art arda gelen aynı tip koli taramaları tek satırda toplanır ("3 koli taraması"). */
export function groupTimeline(items: TimelineItem[]): Row[] {
  const rows: Row[] = [];
  for (const item of items) {
    const previous = rows[rows.length - 1];
    const same =
      previous &&
      previous.first.source === "HubScan" &&
      item.source === "HubScan" &&
      previous.first.eventType === item.eventType &&
      previous.first.outcome === item.outcome &&
      previous.first.location?.hubCode === item.location?.hubCode &&
      (item.eventType === "ArrivedAtHub" || item.eventType === "DepartedHub" || item.eventType === "OutForDelivery");
    if (same) previous.count += 1;
    else rows.push({ first: item, count: 1 });
  }
  return rows;
}

export function Timeline({ items }: { items: TimelineItem[] }) {
  const [appliedOnly, setAppliedOnly] = useState(false);
  const visible = appliedOnly ? items.filter((item) => item.outcome === "Applied") : items;
  // API eski → yeni sıralar; ekranda en yeni olay üsttedir.
  const rows = groupTimeline(visible).reverse();
  const hiddenCount = items.length - visible.length;

  return (
    <section className="panel" aria-labelledby="timeline-heading">
      <div className="panel-head">
        <h2 id="timeline-heading">Zaman çizelgesi</h2>
        <div className="seg" role="group" aria-label="Olay filtresi">
          <button type="button" aria-pressed={!appliedOnly} onClick={() => setAppliedOnly(false)}>
            Tümü
          </button>
          <button type="button" aria-pressed={appliedOnly} onClick={() => setAppliedOnly(true)}>
            Uygulanan
          </button>
        </div>
      </div>
      <ol className="timeline">
        {rows.map(({ first: item, count }) => {
          const reason = reasonLabel(item.reasonCode);
          const lateReceipt = Date.parse(item.receivedAt) - Date.parse(item.occurredAt) > lateReceiptMs;
          const place = item.location?.hubCode ?? item.location?.city ?? null;
          return (
            <li key={item.id} className={`tl-item${item.outcome === "Applied" ? "" : " not-applied"}`}>
              <time className="tl-time" dateTime={item.occurredAt}>
                {formatDateTime(item.occurredAt)}
              </time>
              <span className="tl-dot">
                <Icon name={eventIcons[item.eventType] ?? "dot"} />
              </span>
              <div className="tl-body">
                <div className="tl-title">
                  <span>{eventTypeLabels[item.eventType] ?? item.eventType}</span>
                  {place && <span className="mono">{place}</span>}
                  {count > 1 && <span className="muted">{count} koli taraması</span>}
                  {item.outcome === "Stale" && <span className="tag t-warn">Geç alındı · Uygulanmadı</span>}
                  {item.outcome === "PendingReview" && <span className="tag t-serious">İnceleme bekliyor</span>}
                </div>
                <div className="tl-sub">
                  {eventSourceLabels[item.source] ?? item.source}
                  {reason && ` · ${reason}`}
                  {lateReceipt && (
                    <>
                      {" · alındı: "}
                      <time dateTime={item.receivedAt}>{formatTime(item.receivedAt)}</time>
                    </>
                  )}
                </div>
              </div>
            </li>
          );
        })}
      </ol>
      {rows.length === 0 && <p className="muted">Gösterilecek olay yok.</p>}
      {appliedOnly && hiddenCount > 0 && <p className="muted">{hiddenCount} uygulanmamış olay gizlendi.</p>}
    </section>
  );
}
