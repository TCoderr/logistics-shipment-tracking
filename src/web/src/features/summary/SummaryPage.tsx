import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router-dom";
import { operationsApi } from "../../api/endpoints";
import type { CountByKey, DeliveryFailureReason } from "../../api/types";
import { ErrorNotice, TableSkeleton } from "../../components/Feedback";
import { FilterDate, FilterSelect } from "../../components/Field";
import { businessToday, formatBusinessDate, formatDateTimeFull, formatDuration, formatNumber, formatRatio } from "../../lib/format";
import { carrierOutcomeLabels, failureReasonLabels } from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";
import { useHubs } from "../../lib/reference";

const isoDate = /^[0-9]{4}-[0-9]{2}-[0-9]{2}$/;

function CountTable({ caption, rows, label, empty }: { caption: string; rows: CountByKey[]; label: (key: string) => string; empty: string }) {
  return (
    <section className="panel" aria-label={caption}>
      <div className="panel-head">
        <h2>{caption}</h2>
      </div>
      {rows.length === 0 ? (
        <p className="muted">{empty}</p>
      ) : (
        <div className="table-wrap" tabIndex={0} role="region" aria-label={`${caption} tablosu`}>
          <table>
            <thead>
              <tr>
                <th scope="col">Değer</th>
                <th scope="col" className="r">
                  Adet
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.key}>
                  <td>{label(row.key)}</td>
                  <td className="r num">{formatNumber(row.count)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export function SummaryPage() {
  const hubs = useHubs();
  const [params, setParams] = useSearchParams();
  const hubCode = params.get("hubCode") || undefined;
  const rawDate = params.get("date") ?? "";
  const date = isoDate.test(rawDate) ? rawDate : undefined;
  const summary = useQuery({ queryKey: queryKeys.summary(hubCode, date), queryFn: ({ signal }) => operationsApi.summary({ hubCode, date }, signal) });

  const setParam = (key: string, value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next);
  };

  const data = summary.data;
  const onTime = data ? formatRatio(data.onTimeDelivery7Days.numerator, data.onTimeDelivery7Days.denominator) : null;
  const firstAttempt = data ? formatRatio(data.firstAttemptDelivery7Days.numerator, data.firstAttemptDelivery7Days.denominator) : null;

  return (
    <>
      <div className="page-head">
        <h1>Operasyon özeti</h1>
      </div>
      <div className="filters" role="group" aria-label="Özet kapsamı">
        <FilterSelect label="Hub" value={hubCode ?? ""} onChange={(event) => setParam("hubCode", event.target.value || null)}>
            <option value="">Tüm hub'lar</option>
            {(hubs.data ?? []).map((hub) => (
              <option key={hub.code} value={hub.code}>
                {hub.code} · {hub.name}
              </option>
            ))}
          </FilterSelect>
        <FilterDate label="Gün" value={date ?? ""} max={businessToday()} onChange={(event) => setParam("date", event.target.value || null)} />
      </div>

      {summary.isError && <ErrorNotice error={summary.error} subject="Operasyon özeti" onRetry={() => void summary.refetch()} />}
      {summary.isPending && <TableSkeleton label="Operasyon özeti yükleniyor" rows={4} />}

      {data && onTime && firstAttempt && (
        <>
          <div className="kpis" role="group" aria-label="Operasyon göstergeleri">
            <dl className="kpi-row">
              <div className="kpi">
                <dt>Teslim beklenen ({formatBusinessDate(data.date)})</dt>
                <dd>
                  {formatNumber(data.dueToday.total)} <small>{formatNumber(data.dueToday.delivered)} teslim edildi</small>
                </dd>
              </div>
              <div className="kpi">
                <dt>Gecikmiş açık gönderi</dt>
                <dd>
                  {formatNumber(data.delayedOpen)}{" "}
                  <small>
                    <Link to={`/shipments?delayed=true${hubCode ? `&hubCode=${hubCode}` : ""}`}>listele</Link>
                  </small>
                </dd>
              </div>
              <div className="kpi">
                <dt>Zamanında teslim, son 7 gün</dt>
                <dd>
                  {onTime.primary} {onTime.secondary && <small>{onTime.secondary}</small>}
                </dd>
              </div>
              <div className="kpi">
                <dt>İlk denemede teslim, son 7 gün</dt>
                <dd>
                  {firstAttempt.primary} {firstAttempt.secondary && <small>{firstAttempt.secondary}</small>}
                </dd>
              </div>
              <div className="kpi">
                <dt>Açık istisna</dt>
                <dd>
                  {formatNumber(data.openExceptions.count)}{" "}
                  <small>
                    {formatNumber(data.openExceptions.overdue)} vadesi geçti
                    {data.openExceptions.oldestAgeSeconds !== null && ` · en eski ${formatDuration(data.openExceptions.oldestAgeSeconds * 1000)}`}
                  </small>
                </dd>
              </div>
            </dl>
            <p className="kpi-asof">
              <span>
                Hesaplama: <time dateTime={data.asOf}>{formatDateTimeFull(data.asOf)}</time> · {data.hubCode ?? "Tüm hub'lar"} · {data.timeZone}
              </span>
              <span>Sayılar veritabanındaki gerçek kayıtlardan hesaplanır.</span>
            </p>
          </div>

          <details className="kpi-defs">
            <summary>Bu sayılar nasıl hesaplanıyor?</summary>
            <dl>
              <dt>Teslim beklenen</dt>
              <dd>Söz verilen teslim tarihi seçili gün olan, iptal edilmemiş gönderiler; yanında bunlardan teslim edilenler.</dd>
              <dt>Gecikmiş açık</dt>
              <dd>Teslim edilmemiş, iade edilmemiş veya iadede olmayan ve iptal edilmemiş; söz verilen tarihi seçili günden önce olan gönderiler.</dd>
              <dt>Zamanında teslim</dt>
              <dd>Son 7 tam günde teslim edilenlerden, söz verilen tarihte veya öncesinde teslim edilenlerin oranı.</dd>
              <dt>İlk denemede teslim</dt>
              <dd>Son 7 tam günde teslim edilenlerden, hiç başarısız denemesi olmayanların oranı.</dd>
              <dt>Açık istisna</dt>
              <dd>Açık veya atanmış istisnalar; vadesi geçen: vade zamanı geçmiş olanlar.</dd>
              <dt>Küçük payda</dt>
              <dd>Payda 20'den küçükse oran yerine pay / payda gösterilir.</dd>
            </dl>
          </details>

          <div className="summary-grid">
            <CountTable
              caption={`Başarısız deneme nedenleri (${formatBusinessDate(data.date)})`}
              rows={data.failedAttemptReasonsToday}
              label={(key) => failureReasonLabels[key as DeliveryFailureReason] ?? key}
              empty="Seçili günde başarısız teslimat denemesi yok."
            />
            <CountTable
              caption="Carrier olay sonuçları (son 24 saat)"
              rows={data.carrierOutcomes24Hours}
              label={(key) => carrierOutcomeLabels[key] ?? key}
              empty="Son 24 saatte işlenen carrier olayı yok."
            />
          </div>
        </>
      )}
    </>
  );
}
