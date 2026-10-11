import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useLocation, useParams } from "react-router-dom";
import { shipmentsApi, simulatorApi } from "../../api/endpoints";
import { ApiError } from "../../api/http";
import type { PartyView, ShipmentDetail } from "../../api/types";
import { useSessionUser, type SessionUser } from "../../auth/AuthContext";
import { ErrorNotice, Notice, TableSkeleton } from "../../components/Feedback";
import { SelectField } from "../../components/Field";
import { Icon } from "../../components/Icon";
import { StatusBadge } from "../../components/StatusBadge";
import { businessToday, daysBetween, formatBusinessDate, formatDateTime, formatDuration, formatWeight } from "../../lib/format";
import {
  auditActionLabels,
  carrierOutcomeLabels,
  eventTypeLabels,
  exceptionTypeLabels,
  failureReasonLabels,
  reasonLabel,
  scanTypeLabels,
  serviceLevelLabels,
  severityLabels,
} from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";
import { useNow } from "../../lib/useNow";
import { useShipmentWatch } from "../../realtime/RealtimeProvider";
import { ShipmentActions } from "./ShipmentActions";
import { Timeline } from "./Timeline";

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function Party({ title, party }: { title: string; party: PartyView }) {
  const address = [party.addressLine1, party.addressLine2].filter(Boolean).join(", ");
  const area = [party.district, party.city].filter(Boolean).join(" / ");
  return (
    <section className="panel" aria-label={title}>
      <div className="panel-head">
        <h2>{title}</h2>
      </div>
      <dl className="kv">
        <dt>Ad</dt>
        <dd>{party.fullName}</dd>
        {party.companyName && (
          <>
            <dt>Firma</dt>
            <dd>{party.companyName}</dd>
          </>
        )}
        <dt>Telefon</dt>
        <dd className="mono">{party.phone ?? <span className="muted">Bu rolde gösterilmez</span>}</dd>
        {party.email && (
          <>
            <dt>E-posta</dt>
            <dd>{party.email}</dd>
          </>
        )}
        <dt>Adres</dt>
        <dd>
          {address ? `${address}, ` : ""}
          {area}
          {party.postalCode ? ` ${party.postalCode}` : ""}
        </dd>
      </dl>
    </section>
  );
}

function ExceptionBanner({ detail, user }: { detail: ShipmentDetail; user: SessionUser }) {
  const now = useNow(30_000);
  if (detail.openExceptions.length === 0) return null;
  return (
    <div className="exc-banner" role="region" aria-label="Açık istisnalar">
      <Icon name="flag" />
      <strong>Açık istisna:</strong>
      {detail.openExceptions.map((exception) => {
        const due = Date.parse(exception.dueAt);
        return (
          <span key={exception.id}>
            {exceptionTypeLabels[exception.type] ?? exception.type}
            {exception.occurrenceCount > 1 ? ` ×${exception.occurrenceCount}` : ""} · {severityLabels[exception.severity] ?? exception.severity} ·{" "}
            {exception.isOverdue ? `vade ${formatDuration(now - due)} geçti` : `vadeye ${formatDuration(due - now)}`} ·{" "}
            {exception.assignedToUserId === null ? "sahibi yok" : exception.assignedToUserId === user.id ? "sizde" : "atanmış"}
          </span>
        );
      })}
      {user.isOps && <Link to={`/exceptions?shipmentId=${detail.id}`}>Kuyrukta aç</Link>}
    </div>
  );
}

function AuditPanel({ shipmentId }: { shipmentId: string }) {
  const [open, setOpen] = useState(false);
  const audit = useQuery({ queryKey: queryKeys.shipmentAudit(shipmentId), queryFn: ({ signal }) => shipmentsApi.audit(shipmentId, signal), enabled: open });
  return (
    <details className="panel" onToggle={(event) => setOpen(event.currentTarget.open)}>
      <summary>
        <strong>İşlem geçmişi (audit)</strong>
      </summary>
      {audit.isError && <ErrorNotice error={audit.error} subject="İşlem geçmişi" onRetry={() => void audit.refetch()} />}
      {open && audit.isPending && <p className="muted">Yükleniyor…</p>}
      {audit.data && (
        <div className="table-wrap" tabIndex={0} role="region" aria-label="İşlem geçmişi tablosu">
          <table>
            <thead>
              <tr>
                <th scope="col">Zaman</th>
                <th scope="col">İşlem</th>
                <th scope="col">Yapan</th>
                <th scope="col">Sonuç</th>
              </tr>
            </thead>
            <tbody>
              {audit.data.items.map((entry) => (
                <tr key={entry.id}>
                  <td className="num">
                    <time dateTime={entry.occurredAt}>{formatDateTime(entry.occurredAt)}</time>
                  </td>
                  <td>{auditActionLabels[entry.action] ?? entry.action}</td>
                  <td>{entry.actorDisplayName ?? entry.actorId}</td>
                  <td>
                    {entry.outcome === "Succeeded" ? "Başarılı" : "Reddedildi"}
                    {entry.reasonCode ? ` · ${reasonLabel(entry.reasonCode)}` : ""}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </details>
  );
}

function CarrierReceiptsPanel({ shipmentId }: { shipmentId: string }) {
  const [open, setOpen] = useState(false);
  const receipts = useQuery({
    queryKey: queryKeys.shipmentReceipts(shipmentId),
    queryFn: ({ signal }) => shipmentsApi.carrierReceipts(shipmentId, signal),
    enabled: open,
  });
  return (
    <details className="panel" onToggle={(event) => setOpen(event.currentTarget.open)}>
      <summary>
        <strong>Carrier olay makbuzları</strong>
      </summary>
      {receipts.isError && <ErrorNotice error={receipts.error} subject="Carrier makbuzları" onRetry={() => void receipts.refetch()} />}
      {open && receipts.isPending && <p className="muted">Yükleniyor…</p>}
      {receipts.data && receipts.data.length === 0 && <p className="muted">Bu gönderi için carrier olayı alınmadı.</p>}
      {receipts.data && receipts.data.length > 0 && (
        <div className="table-wrap" tabIndex={0} role="region" aria-label="Carrier makbuz tablosu">
          <table>
            <thead>
              <tr>
                <th scope="col">Alındı</th>
                <th scope="col">Olay</th>
                <th scope="col">İşleme</th>
                <th scope="col">Sonuç</th>
              </tr>
            </thead>
            <tbody>
              {receipts.data.map((receipt) => (
                <tr key={receipt.id}>
                  <td className="num">
                    <time dateTime={receipt.receivedAt}>{formatDateTime(receipt.receivedAt)}</time>
                  </td>
                  <td>{receipt.eventType ? (eventTypeLabels[receipt.eventType as keyof typeof eventTypeLabels] ?? receipt.eventType) : "—"}</td>
                  <td>{receipt.processingStatus}</td>
                  <td>
                    {receipt.outcome ? (carrierOutcomeLabels[receipt.outcome] ?? receipt.outcome) : "Bekliyor"}
                    {receipt.outcomeReason ? ` · ${reasonLabel(receipt.outcomeReason)}` : ""}
                    {receipt.conflictCount > 0 ? ` · ${receipt.conflictCount} çakışma` : ""}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </details>
  );
}

/**
 * Demo: simüle carrier olayları. Panel yalnız sunucu keşif ucu yanıt verirse görünür (Development + Simulation:Enabled + OpsManager);
 * Production'da route yoktur. Olaylar gerçek imzalı webhook hattından geçer; iş sonucu inbox'ta asenkron belirlenir.
 */
function SimulatorPanel({ detail, onRan }: { detail: ShipmentDetail; onRan: () => void }) {
  const info = useQuery({ queryKey: queryKeys.simulator, queryFn: ({ signal }) => simulatorApi.info(signal), retry: false, staleTime: Infinity });
  const [scenario, setScenario] = useState("HappyPath");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null);
  if (!info.data) return null;
  const assigned = detail.carrier?.code === info.data.carrierCode;

  const run = async () => {
    setBusy(true);
    setResult(null);
    try {
      const response = await simulatorApi.run(detail.trackingNumber, scenario);
      const statuses = response.deliveries.map((delivery) => `${delivery.eventType} → ${delivery.httpStatus}`).join(", ");
      setResult({ ok: true, text: `${response.deliveries.length} olay gönderildi (${statuses}). İş sonucu birkaç saniye içinde zaman çizelgesine yansır.` });
      onRan();
    } catch (error) {
      setResult({ ok: false, text: error instanceof ApiError ? (error.problem.title ?? "Simülasyon çalıştırılamadı.") : "Simülasyon çalıştırılamadı." });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="panel" aria-labelledby="simulator-heading">
      <div className="panel-head">
        <h2 id="simulator-heading">Carrier simülasyonu</h2>
        <span className="env-badge">Demo</span>
      </div>
      {!assigned && <p className="why-not">Olayların uygulanması için gönderiye {info.data.carrierCode} carrier'ı atanmış olmalı; aksi halde olaylar reddedilir.</p>}
      <SelectField label="Senaryo" name="scenario" value={scenario} onChange={(event) => setScenario(event.target.value)}>
        {info.data.scenarios.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </SelectField>
      <div className="form-actions">
        <button type="button" className="btn" disabled={busy} onClick={() => void run()}>
          {busy ? "Gönderiliyor…" : "Simüle olayları gönder"}
        </button>
      </div>
      {result && (
        <Notice tone={result.ok ? "info" : "critical"} role="status">
          <p>{result.text}</p>
        </Notice>
      )}
    </section>
  );
}

export function ShipmentDetailPage() {
  const { shipmentId = "" } = useParams();
  const user = useSessionUser();
  const location = useLocation();
  const queryClient = useQueryClient();
  const valid = uuid.test(shipmentId);
  const flash = location.state as { created?: boolean; replayed?: boolean } | null;
  // Açık detay için canlı ipucu aboneliği; sunucu kapsamı ayrıca doğrular, veri yine HTTP ile okunur.
  useShipmentWatch(valid ? shipmentId : null);

  const query = useQuery({
    queryKey: queryKeys.shipment(shipmentId),
    queryFn: ({ signal }) => shipmentsApi.detail(shipmentId, signal),
    enabled: valid,
  });

  /** İşlem sonrası: detay sunucudan yeniden okunur, bağlı liste/kuyruk/özet sorguları bayatlatılır. */
  const refresh = async (): Promise<ShipmentDetail | undefined> => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.shipmentsRoot, refetchType: "active", predicate: (q) => q.queryKey[1] !== "detail" });
    void queryClient.invalidateQueries({ queryKey: queryKeys.exceptionsRoot });
    void queryClient.invalidateQueries({ queryKey: queryKeys.summaryRoot });
    const result = await query.refetch();
    return result.data?.data;
  };

  if (!valid) {
    return (
      <Notice tone="neutral" role="alert">
        <p>Gönderi bulunamadı veya erişiminiz yok.</p>
      </Notice>
    );
  }
  if (query.isPending) return <TableSkeleton label="Gönderi yükleniyor" rows={8} />;
  // Kapsam dışına çıkan veya silinen gönderi: eldeki eski veri gösterilmez (yetki sunucudadır).
  const gone = query.error instanceof ApiError && (query.error.status === 404 || query.error.status === 403);
  if (gone) {
    return (
      <>
        <p>
          <Link to="/shipments">← Gönderiler</Link>
        </p>
        <Notice tone="neutral" role="alert">
          <p>Gönderi bulunamadı veya erişiminiz yok.</p>
        </Notice>
      </>
    );
  }
  if (!query.data) {
    return <ErrorNotice error={query.error} subject="Gönderi" onRetry={() => void query.refetch()} />;
  }

  const detail = query.data.data;
  const etag = query.data.etag;
  const refreshFailed = query.isError;
  const today = businessToday();
  const delayDays = detail.isDelayed ? daysBetween(detail.promisedDeliveryDate, today) : 0;

  return (
    <>
      {flash?.created && (
        <Notice tone="good" role="status">
          <p>
            Gönderi oluşturuldu.
            {flash.replayed && " Önceki isteğin sonucu döndü; ikinci bir kayıt oluşmadı."}
          </p>
        </Notice>
      )}
      {refreshFailed && (
        <Notice tone="warn" role="alert" icon="plug">
          <p>Güncel durum alınamadı; ekrandaki bilgi eski olabilir.</p>
          <div className="notice-actions">
            <button type="button" className="btn" onClick={() => void query.refetch()}>
              Yeniden dene
            </button>
          </div>
        </Notice>
      )}

      <div className="detail-head">
        <Link to="/shipments">← Gönderiler</Link>
        <h1>{detail.trackingNumber}</h1>
        <StatusBadge status={detail.status} />
        {detail.isDelayed && (
          <span className="delay">
            <Icon name="clock" />
            {delayDays > 0 ? `${delayDays} gün gecikti` : "Gecikti"}
          </span>
        )}
        <div className="meta">
          <span>{serviceLevelLabels[detail.serviceLevel] ?? detail.serviceLevel}</span>
          <span>
            Söz: <b>{formatBusinessDate(detail.promisedDeliveryDate)}</b>
          </span>
          <span className="mono">
            {detail.originHub.code} → {detail.destinationHub.code}
          </span>
          <span>
            Şu an: <b className="mono">{detail.currentHub?.code ?? "—"}</b>
          </span>
          {detail.externalReference && (
            <span>
              Sipariş: <span className="mono">{detail.externalReference}</span>
            </span>
          )}
        </div>
      </div>
      <ExceptionBanner detail={detail} user={user} />
      {detail.piiRedacted && (
        <Notice tone="neutral">
          <p>Bu gönderinin kişisel verileri saklama süresi dolduğu için maskelendi.</p>
        </Notice>
      )}

      <div className="detail-grid">
        <div>
          <Timeline items={detail.timeline} />
          {user.isManager && <AuditPanel shipmentId={detail.id} />}
          {user.isOps && <CarrierReceiptsPanel shipmentId={detail.id} />}
        </div>
        <div>
          <ShipmentActions detail={detail} etag={etag} user={user} refresh={refresh} />

          <section className="panel" aria-labelledby="packages-heading">
            <div className="panel-head">
              <h2 id="packages-heading">Paketler ({detail.packages.length})</h2>
            </div>
            <div className="table-wrap" tabIndex={0} role="region" aria-label="Paket tablosu">
              <table>
                <thead>
                  <tr>
                    <th scope="col">Etiket</th>
                    <th scope="col" className="r">
                      Ağırlık
                    </th>
                    <th scope="col">Boyut (mm)</th>
                    <th scope="col">Son tarama</th>
                  </tr>
                </thead>
                <tbody>
                  {detail.packages.map((pkg) => (
                    <tr key={pkg.id}>
                      <td className="mono">{pkg.labelCode}</td>
                      <td className="r num">{formatWeight(pkg.weightGrams)}</td>
                      <td className="num">{pkg.dimensionsMm.join("×")}</td>
                      <td>
                        {pkg.lastScanType ? (
                          <>
                            {scanTypeLabels[pkg.lastScanType] ?? pkg.lastScanType}
                            {pkg.currentHubCode && <span className="mono"> · {pkg.currentHubCode}</span>}
                          </>
                        ) : (
                          <span className="muted">Taranmadı</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <section className="panel" aria-labelledby="attempts-heading">
            <div className="panel-head">
              <h2 id="attempts-heading">
                Teslimat denemeleri {detail.deliveryAttempts.count}/{detail.deliveryAttempts.max}
              </h2>
            </div>
            {detail.deliveryAttempts.attempts.length === 0 ? (
              <p className="muted">Henüz deneme yok.</p>
            ) : (
              <ol className="plain-list">
                {detail.deliveryAttempts.attempts.map((attempt) => (
                  <li key={attempt.attemptNumber}>
                    <span className="num">{attempt.attemptNumber}.</span> {attempt.outcome === "Delivered" ? "Teslim edildi" : "Teslim edilemedi"}
                    {attempt.failureReason ? ` · ${failureReasonLabels[attempt.failureReason] ?? attempt.failureReason}` : ""} ·{" "}
                    <time dateTime={attempt.occurredAt}>{formatDateTime(attempt.occurredAt)}</time>
                  </li>
                ))}
              </ol>
            )}
          </section>

          <Party title="Alıcı" party={detail.recipient} />
          <Party title="Gönderen" party={detail.sender} />

          <section className="panel" aria-labelledby="carrier-heading">
            <div className="panel-head">
              <h2 id="carrier-heading">Carrier ve takip</h2>
            </div>
            <dl className="kv">
              <dt>Carrier</dt>
              <dd>
                {detail.carrier ? (
                  <>
                    {detail.carrier.name} <span className="mono">({detail.carrier.code})</span>
                    {detail.carrier.handedOverAt && (
                      <>
                        {" · devir "}
                        <time dateTime={detail.carrier.handedOverAt}>{formatDateTime(detail.carrier.handedOverAt)}</time>
                      </>
                    )}
                  </>
                ) : (
                  <span className="muted">Atanmadı</span>
                )}
              </dd>
              {detail.declaredValue && (
                <>
                  <dt>Beyan değeri</dt>
                  <dd className="num">
                    {new Intl.NumberFormat("tr-TR", { style: "currency", currency: detail.declaredValue.currency }).format(detail.declaredValue.amount)}
                  </dd>
                </>
              )}
              {detail.publicTrackingCode && (
                <>
                  <dt>Alıcı takibi</dt>
                  <dd>
                    {/* Kod bir yetki belgesidir: bağlantı referrer taşımaz, yeni sekmede açılır. */}
                    <a href={`/t/${detail.publicTrackingCode}`} target="_blank" rel="noreferrer noopener">
                      Public takip sayfasını aç
                    </a>
                  </dd>
                </>
              )}
              <dt>Oluşturma</dt>
              <dd>
                <time dateTime={detail.createdAt}>{formatDateTime(detail.createdAt)}</time>
              </dd>
            </dl>
          </section>

          {user.isManager && <SimulatorPanel detail={detail} onRan={() => void refresh()} />}
        </div>
      </div>
    </>
  );
}
