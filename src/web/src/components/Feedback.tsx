import { useEffect, useState, type ReactNode } from "react";
import { ApiError, NetworkError } from "../api/http";
import { Icon, type IconName } from "./Icon";

type Tone = "info" | "good" | "warn" | "serious" | "critical" | "neutral";

const toneIcons: Record<Tone, IconName> = {
  info: "info",
  good: "delivered",
  warn: "clock",
  serious: "failed",
  critical: "failed",
  neutral: "info",
};

export function Notice({
  tone,
  children,
  role,
  icon,
}: {
  tone: Tone;
  children: ReactNode;
  role?: "alert" | "status";
  icon?: IconName;
}) {
  return (
    <div className={`notice t-${tone}`} role={role}>
      <Icon name={icon ?? toneIcons[tone]} />
      <div>{children}</div>
    </div>
  );
}

/** Kullanıcıya gösterilecek hata metni. Sunucunun `detail`'i yalnız iş kuralı reddinde (4xx) gösterilir. */
export function describeError(error: unknown, subject: string, verb = "yüklenemedi"): { message: string; traceId?: string; kind: "forbidden" | "notFound" | "network" | "rate" | "other" } {
  if (error instanceof NetworkError) {
    return { kind: "network", message: `${subject} ${verb}. Bağlantınızı kontrol edip yeniden deneyin.` };
  }
  if (error instanceof ApiError) {
    if (error.status === 403) {
      return { kind: "forbidden", message: "Bu işlem için yetkiniz yok. Hub ataması için operasyon yöneticinize başvurun.", traceId: error.traceId };
    }
    if (error.status === 404) {
      return { kind: "notFound", message: "Kayıt bulunamadı veya erişiminiz yok.", traceId: error.traceId };
    }
    if (error.status === 429) {
      const wait = error.retryAfterSeconds ? ` ${error.retryAfterSeconds} sn sonra` : " kısa süre sonra";
      return { kind: "rate", message: `Çok fazla istek gönderildi.${wait} tekrar deneyin.`, traceId: error.traceId };
    }
    if (error.status >= 400 && error.status < 500) {
      return { kind: "other", message: error.problem.detail ?? error.problem.title ?? `${subject} işlenemedi.`, traceId: error.traceId };
    }
    return { kind: "other", message: `${subject} ${verb}. Sunucu hatası oluştu; yeniden deneyin.`, traceId: error.traceId };
  }
  return { kind: "other", message: `${subject} ${verb}.` };
}

export function ErrorNotice({ error, subject, verb, onRetry }: { error: unknown; subject: string; verb?: string; onRetry?: () => void }) {
  const described = describeError(error, subject, verb);
  const tone: Tone = described.kind === "forbidden" || described.kind === "notFound" ? "neutral" : "critical";
  const icon: IconName | undefined = described.kind === "forbidden" ? "lock" : described.kind === "network" ? "plug" : undefined;
  return (
    <Notice tone={tone} role="alert" icon={icon}>
      <p>{described.message}</p>
      {(onRetry || described.traceId) && (
        <div className="notice-actions">
          {onRetry && described.kind !== "forbidden" && described.kind !== "notFound" && (
            <button type="button" className="btn" onClick={onRetry}>
              Yeniden dene
            </button>
          )}
          {described.traceId && (
            <span className="muted">
              Destek kodu: <span className="mono">{described.traceId}</span>
            </span>
          )}
        </div>
      )}
    </Notice>
  );
}

/** Tablo iskeleti; ilk 300 ms gösterilmez (hızlı yanıtlarda titreme olmasın). */
export function TableSkeleton({ rows = 6, label }: { rows?: number; label: string }) {
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setVisible(true), 300);
    return () => window.clearTimeout(timer);
  }, []);
  return (
    <div className="panel" role="status" aria-live="polite">
      <span className="sr-only">{label}</span>
      {visible && (
        <div aria-hidden="true" className="skeleton-rows">
          {Array.from({ length: rows }, (_, index) => (
            <div key={index} className="skeleton skeleton-row" />
          ))}
        </div>
      )}
    </div>
  );
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}
