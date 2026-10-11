import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { useParams } from "react-router-dom";
import { publicApi } from "../../api/endpoints";
import { ApiError } from "../../api/http";
import { Notice } from "../../components/Feedback";
import { Icon } from "../../components/Icon";
import { shipmentStatusStyles } from "../../components/StatusBadge";
import { formatBusinessDate, formatDateTime, formatTime } from "../../lib/format";

/**
 * Alıcının oturumsuz takip sayfası (mobil öncelikli). Yalnız sunucunun allowlist yanıtı gösterilir.
 * Takip kodu bir yetki belgesidir: sayfa başlığına, depolamaya, console'a veya dış bağlantıya yazılmaz.
 */
export function PublicTrackingPage() {
  const { code = "" } = useParams();
  const tracking = useQuery({
    // Kod yalnız bellekteki sorgu anahtarındadır; kalıcı önbellek yoktur.
    queryKey: ["public-tracking", code],
    queryFn: ({ signal }) => publicApi.tracking(code, signal),
    retry: false,
    refetchOnWindowFocus: false,
    gcTime: 0,
  });

  useEffect(() => {
    const previous = document.title;
    document.title = "Gönderi takibi";
    return () => {
      document.title = previous;
    };
  }, []);

  // Yenilemede hata alınırsa (ör. kod yenilendi → 404) eldeki eski veri gösterilmez.
  const data = tracking.isError ? undefined : tracking.data;
  const style = data ? shipmentStatusStyles[data.status] : null;
  const rateLimited = tracking.error instanceof ApiError && tracking.error.status === 429;
  const notFound = tracking.error instanceof ApiError && tracking.error.status === 404;

  return (
    <main id="main" className="public-shell">
      <div className="phone">
        <h1>Gönderi takibi</h1>

        {tracking.isPending && (
          <p role="status" className="muted">
            Gönderi bilgisi yükleniyor…
          </p>
        )}

        {notFound && (
          <Notice tone="neutral" role="alert">
            <p>Gönderi bulunamadı. Bağlantıyı kontrol edin; bağlantı yenilenmiş olabilir.</p>
          </Notice>
        )}
        {rateLimited && (
          <Notice tone="warn" role="alert" icon="clock">
            <p>Çok fazla istek gönderildi. Lütfen bir dakika sonra tekrar deneyin.</p>
            <div className="notice-actions">
              <button type="button" className="btn" onClick={() => void tracking.refetch()}>
                Yeniden dene
              </button>
            </div>
          </Notice>
        )}
        {tracking.isError && !notFound && !rateLimited && (
          <Notice tone="critical" role="alert" icon="plug">
            <p>Gönderi bilgisi şu an alınamıyor. Bağlantınızı kontrol edip yeniden deneyin.</p>
            <div className="notice-actions">
              <button type="button" className="btn" onClick={() => void tracking.refetch()}>
                Yeniden dene
              </button>
            </div>
          </Notice>
        )}

        {data && (
          <>
            <p className="muted">
              <span className="mono">{data.trackingNumberMasked}</span> · {data.packageCount} koli · {data.destinationCity}
            </p>
            <div className="pub-status">
              <span className={`status t-${style?.tone ?? "neutral"}`}>
                <Icon name={style?.icon ?? "dot"} />
                {data.statusLabel}
              </span>
              {data.isDelayed && (
                <span className="delay">
                  <Icon name="clock" />
                  Gecikti
                </span>
              )}
            </div>
            <p className="pub-date">
              Tahmini teslim: <strong>{formatBusinessDate(data.promisedDeliveryDate, true)}</strong>
            </p>
            <p className="muted">
              Son güncelleme: <time dateTime={data.lastUpdatedAt}>{formatDateTime(data.lastUpdatedAt)}</time>
            </p>

            <h2 className="sr-only">Gönderi geçmişi</h2>
            <ol className="pub-tl">
              {[...data.timeline].reverse().map((event, index, all) => (
                <li key={`${event.occurredAt}-${index}`} className={index === all.length - 1 ? "first" : undefined}>
                  <span className="dot" aria-hidden="true" />
                  <div>
                    <time dateTime={event.occurredAt}>{formatDateTime(event.occurredAt)}</time>
                    {event.label}
                    {event.city ? ` · ${event.city}` : ""}
                  </div>
                </li>
              ))}
            </ol>

            <div className="form-actions">
              <button type="button" className="btn" disabled={tracking.isFetching} onClick={() => void tracking.refetch()}>
                {tracking.isFetching ? "Yenileniyor…" : "Yenile"}
              </button>
              <span className="muted">Sayfa {formatTime(new Date(tracking.dataUpdatedAt))} itibarıyla günceldir.</span>
            </div>
          </>
        )}

        <p className="privacy">Ad, adres ve telefon bilgileri bu sayfada gösterilmez.</p>
      </div>
    </main>
  );
}
