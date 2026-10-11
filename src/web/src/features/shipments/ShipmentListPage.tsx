import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { operationsApi, shipmentsApi } from "../../api/endpoints";
import { ApiError } from "../../api/http";
import type { ServiceLevel, ShipmentListFilters, ShipmentListItem, ShipmentSort, ShipmentStatus } from "../../api/types";
import { useSessionUser } from "../../auth/AuthContext";
import { EmptyState, ErrorNotice, Notice, TableSkeleton } from "../../components/Feedback";
import { FilterDate, FilterSelect } from "../../components/Field";
import { Icon } from "../../components/Icon";
import { StatusBadge, shipmentStatusStyles } from "../../components/StatusBadge";
import { businessToday, daysBetween, formatBusinessDate, formatDateTime, formatNumber, formatRatio, formatTime } from "../../lib/format";
import { serviceLevelLabels } from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";
import { useCarriers, useHubs } from "../../lib/reference";

const statuses = Object.keys(shipmentStatusStyles) as ShipmentStatus[];
const sorts: ShipmentSort[] = ["-createdAt", "createdAt", "promisedDeliveryDate", "-promisedDeliveryDate", "statusChangedAt", "-statusChangedAt"];
const directLookup = /^LS[0-9]{10}(-[0-9]{2})?$/;
const isoDate = /^[0-9]{4}-[0-9]{2}-[0-9]{2}$/;

interface ListView {
  filters: ShipmentListFilters;
  from: string;
  to: string;
}

function nextDay(date: string): string {
  const parsed = new Date(`${date}T00:00:00Z`);
  parsed.setUTCDate(parsed.getUTCDate() + 1);
  return parsed.toISOString().slice(0, 10);
}

/** Filtreler URL'de tutulur (paylaşılabilir, geri tuşu çalışır). Tarihler Europe/Istanbul gününe (+03:00) çevrilir. */
function readView(params: URLSearchParams): ListView {
  const status = params.getAll("status").filter((value): value is ShipmentStatus => statuses.includes(value as ShipmentStatus));
  const sort = params.get("sort");
  const from = params.get("from") ?? "";
  const to = params.get("to") ?? "";
  const serviceLevel = params.get("serviceLevel");
  const filters: ShipmentListFilters = {};
  const q = params.get("q")?.trim();
  if (q) filters.q = q;
  if (status.length > 0) filters.status = status;
  if (params.get("hubCode")) filters.hubCode = params.get("hubCode")!;
  if (serviceLevel === "Standard" || serviceLevel === "Express") filters.serviceLevel = serviceLevel;
  if (params.get("carrierCode")) filters.carrierCode = params.get("carrierCode")!;
  if (params.get("delayed") === "true") filters.delayed = true;
  if (params.get("hasOpenException") === "true") filters.hasOpenException = true;
  if (isoDate.test(from)) filters.createdFrom = `${from}T00:00:00+03:00`;
  if (isoDate.test(to)) filters.createdTo = `${nextDay(to)}T00:00:00+03:00`;
  if (sort && sorts.includes(sort as ShipmentSort) && sort !== "-createdAt") filters.sort = sort as ShipmentSort;
  return { filters, from: isoDate.test(from) ? from : "", to: isoDate.test(to) ? to : "" };
}

function delayText(item: ShipmentListItem, today: string): string {
  const days = daysBetween(item.promisedDeliveryDate, today);
  return days <= 0 ? "Gecikti" : `${days} gün`;
}

/** Arama kutusu. `/` kısayolu odaklar (odak bir metin alanında değilken). */
function SearchBox({ initial, onSearch }: { initial: string; onSearch: (text: string) => void }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [text, setText] = useState(initial);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
      if (event.key === "/" && !typing && !event.ctrlKey && !event.metaKey && !event.altKey) {
        event.preventDefault();
        inputRef.current?.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onSearch(text);
  };

  return (
    <form className="search" role="search" onSubmit={submit}>
      <Icon name="search" />
      <label className="sr-only" htmlFor="shipment-search">
        Gönderi ara
      </label>
      <input
        id="shipment-search"
        ref={inputRef}
        type="search"
        autoComplete="off"
        placeholder="Tracking no, koli etiketi, sipariş referansı veya telefonun son 4 hanesi"
        value={text}
        onChange={(event) => setText(event.target.value)}
      />
      <kbd aria-hidden="true">/</kbd>
      <button type="submit" className="btn">
        Ara
      </button>
    </form>
  );
}

function KpiStrip({ onShowDelayed }: { onShowDelayed: () => void }) {
  const summary = useQuery({ queryKey: queryKeys.summary(undefined, undefined), queryFn: ({ signal }) => operationsApi.summary({}, signal) });
  if (summary.isError) {
    return <ErrorNotice error={summary.error} subject="Operasyon özeti" onRetry={() => void summary.refetch()} />;
  }
  if (!summary.data) {
    return (
      <div className="kpis" role="status">
        <p className="kpi-asof">Operasyon özeti yükleniyor…</p>
      </div>
    );
  }
  const data = summary.data;
  const onTime = formatRatio(data.onTimeDelivery7Days.numerator, data.onTimeDelivery7Days.denominator);
  const firstAttempt = formatRatio(data.firstAttemptDelivery7Days.numerator, data.firstAttemptDelivery7Days.denominator);
  return (
    <div className="kpis" role="group" aria-label="Operasyon özeti">
      <dl className="kpi-row">
        <div className="kpi">
          <dt>Bugün teslim beklenen</dt>
          <dd>
            {formatNumber(data.dueToday.total)} <small>{formatNumber(data.dueToday.delivered)} teslim edildi</small>
          </dd>
        </div>
        <div className="kpi">
          <dt>Gecikmiş açık gönderi</dt>
          <dd>
            <button type="button" className="btn-link" onClick={onShowDelayed} aria-label={`Gecikmiş açık gönderi: ${data.delayedOpen}. Filtreyi uygula`}>
              {formatNumber(data.delayedOpen)}
            </button>{" "}
            <small>filtreyi uygula</small>
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
            <Link to="/exceptions">{formatNumber(data.openExceptions.count)}</Link>{" "}
            <small>{formatNumber(data.openExceptions.overdue)} vadesi geçti</small>
          </dd>
        </div>
      </dl>
      <p className="kpi-asof">
        <span>
          Hesaplama: <time dateTime={data.asOf}>{formatTime(data.asOf)}</time> · Tüm hub'lar · {data.timeZone}
        </span>
        <Link to="/summary">Tanımlar ve ayrıntı</Link>
      </p>
    </div>
  );
}

export function ShipmentListPage() {
  const user = useSessionUser();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const view = useMemo(() => readView(params), [params]);
  const filters = view.filters;
  const hubs = useHubs();
  const carriers = useCarriers(user.isOps);
  const [lookupError, setLookupError] = useState<unknown>(null);
  const today = businessToday();

  const list = useInfiniteQuery({
    queryKey: queryKeys.shipments(filters),
    queryFn: ({ pageParam, signal }) => shipmentsApi.list(filters, pageParam, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  });

  const update = (change: (next: URLSearchParams) => void) => {
    const next = new URLSearchParams(params);
    change(next);
    // Eşzamanlı: kontrollü filtre kontrolleri tıklamayla aynı anda güncellenir (yönlendirici geçiş kullanmaz, bkz. main.tsx).
    setParams(next);
  };
  const setParam = (key: string, value: string | null) =>
    update((next) => {
      if (value) next.set(key, value);
      else next.delete(key);
    });

  const onSearch = async (text: string) => {
    const q = text.trim();
    setLookupError(null);
    // Tracking no veya koli etiketi tam eşleşirse doğrudan detaya gidilir.
    if (directLookup.test(q)) {
      try {
        const found = await shipmentsApi.list({ q }, null, undefined, 2);
        const only = found.items.length === 1 ? found.items[0] : undefined;
        if (only) {
          void navigate(`/shipments/${only.id}`);
          return;
        }
      } catch (error) {
        setLookupError(error);
        return;
      }
    }
    setParam("q", q || null);
  };

  const toggleSort = (ascending: ShipmentSort, descending: ShipmentSort, first: ShipmentSort) => {
    const current = filters.sort ?? "-createdAt";
    const next = current === first ? (first === ascending ? descending : ascending) : first;
    setParam("sort", next === "-createdAt" ? null : next);
  };
  const ariaSort = (ascending: ShipmentSort, descending: ShipmentSort): "ascending" | "descending" | "none" => {
    const current = filters.sort ?? "-createdAt";
    return current === ascending ? "ascending" : current === descending ? "descending" : "none";
  };

  const items = list.data?.pages.flatMap((page) => page.items) ?? [];
  const hubName = (code: string) => hubs.data?.find((hub) => hub.code === code)?.name;
  const chips: { key: string; label: string; clear: () => void }[] = [];
  if (filters.q) chips.push({ key: "q", label: `Arama: ${filters.q}`, clear: () => setParam("q", null) });
  for (const status of filters.status ?? []) {
    chips.push({
      key: `status-${status}`,
      label: `Durum: ${shipmentStatusStyles[status].label}`,
      clear: () =>
        update((next) => {
          const rest = next.getAll("status").filter((value) => value !== status);
          next.delete("status");
          rest.forEach((value) => next.append("status", value));
        }),
    });
  }
  if (filters.hubCode) chips.push({ key: "hub", label: `Hub: ${filters.hubCode}`, clear: () => setParam("hubCode", null) });
  if (filters.serviceLevel) {
    chips.push({ key: "service", label: `Servis: ${serviceLevelLabels[filters.serviceLevel]}`, clear: () => setParam("serviceLevel", null) });
  }
  if (filters.carrierCode) chips.push({ key: "carrier", label: `Carrier: ${filters.carrierCode}`, clear: () => setParam("carrierCode", null) });
  if (filters.delayed) chips.push({ key: "delayed", label: "Gecikmiş", clear: () => setParam("delayed", null) });
  if (filters.hasOpenException) chips.push({ key: "exc", label: "Açık istisna", clear: () => setParam("hasOpenException", null) });
  if (view.from) chips.push({ key: "from", label: `Oluşturma ≥ ${formatBusinessDate(view.from)}`, clear: () => setParam("from", null) });
  if (view.to) chips.push({ key: "to", label: `Oluşturma ≤ ${formatBusinessDate(view.to)}`, clear: () => setParam("to", null) });
  const hasFilters = chips.length > 0;
  const clearAll = () => setParams(filters.sort ? new URLSearchParams({ sort: filters.sort }) : new URLSearchParams());

  const validation = list.error instanceof ApiError && list.error.code === "validation" ? list.error.fieldErrors : null;
  const cursorProblem = list.error instanceof ApiError && (list.error.code === "invalid-cursor" || list.error.code === "cursor-mismatch");

  return (
    <>
      <h1 className="sr-only">Gönderiler</h1>
      {user.isOps && <KpiStrip onShowDelayed={() => setParam("delayed", "true")} />}

      <div className="toolbar">
        <SearchBox key={filters.q ?? ""} initial={filters.q ?? ""} onSearch={(text) => void onSearch(text)} />
        {user.isOps && (
          <Link className="btn btn-primary" to="/shipments/new">
            + Gönderi oluştur
          </Link>
        )}
      </div>

      <div className="filters" role="group" aria-label="Filtreler">
        <FilterSelect label="Durum"
            value=""
            onChange={(event) => {
              const value = event.target.value;
              if (value) update((next) => (next.getAll("status").includes(value) ? undefined : next.append("status", value)));
            }}
          >
            <option value="">Ekle…</option>
            {statuses.map((status) => (
              <option key={status} value={status} disabled={filters.status?.includes(status)}>
                {shipmentStatusStyles[status].label}
              </option>
            ))}
          </FilterSelect>
        <FilterSelect label="Hub" value={filters.hubCode ?? ""} onChange={(event) => setParam("hubCode", event.target.value || null)}>
            <option value="">Tümü</option>
            {(hubs.data ?? []).map((hub) => (
              <option key={hub.code} value={hub.code}>
                {hub.code} · {hub.name}
              </option>
            ))}
          </FilterSelect>
        <FilterSelect label="Servis" value={filters.serviceLevel ?? ""} onChange={(event) => setParam("serviceLevel", event.target.value || null)}>
            <option value="">Tümü</option>
            {(Object.keys(serviceLevelLabels) as ServiceLevel[]).map((level) => (
              <option key={level} value={level}>
                {serviceLevelLabels[level]}
              </option>
            ))}
          </FilterSelect>
        {user.isOps && (
          <FilterSelect label="Carrier" value={filters.carrierCode ?? ""} onChange={(event) => setParam("carrierCode", event.target.value || null)}>
              <option value="">Tümü</option>
              {(carriers.data ?? []).map((carrier) => (
                <option key={carrier.code} value={carrier.code}>
                  {carrier.name}
                </option>
              ))}
            </FilterSelect>
        )}
        <label>
          <input type="checkbox" checked={filters.delayed === true} onChange={(event) => setParam("delayed", event.target.checked ? "true" : null)} />
          Gecikmiş
        </label>
        <label>
          <input
            type="checkbox"
            checked={filters.hasOpenException === true}
            onChange={(event) => setParam("hasOpenException", event.target.checked ? "true" : null)}
          />
          Açık istisna
        </label>
        <FilterDate label="Oluşturma (başlangıç)" value={view.from} max={view.to || undefined} onChange={(event) => setParam("from", event.target.value || null)} />
        <FilterDate label="Oluşturma (bitiş)" value={view.to} min={view.from || undefined} onChange={(event) => setParam("to", event.target.value || null)} />
      </div>

      <div className="active-filters">
        {hasFilters ? (
          <>
            <span className="muted">Aktif filtreler:</span>
            {chips.map((chip) => (
              <span className="chip" key={chip.key}>
                {chip.label}
                <button type="button" aria-label={`${chip.label} filtresini kaldır`} onClick={chip.clear}>
                  ×
                </button>
              </span>
            ))}
            <button type="button" className="btn-link" onClick={clearAll}>
              Filtreleri temizle
            </button>
          </>
        ) : (
          <span className="muted">Filtre yok: tüm {user.isOps ? "" : "kapsamınızdaki "}gönderiler.</span>
        )}
      </div>

      {lookupError !== null && <ErrorNotice error={lookupError} subject="Arama" />}
      {validation && (
        <Notice tone="warn" role="alert">
          <p>Filtre geçersiz: {Object.values(validation).flat().join(" ")}</p>
          {validation.q && <p className="muted">Telefonun son 4 hanesiyle arama için en az bir filtre daha seçin (ör. hub veya durum).</p>}
        </Notice>
      )}
      {list.isError && !validation && (
        <ErrorNotice
          error={list.error}
          subject="Gönderiler"
          onRetry={() => void (cursorProblem ? queryClient.resetQueries({ queryKey: queryKeys.shipments(filters) }) : list.refetch())}
        />
      )}

      {list.isPending && <TableSkeleton label="Gönderiler yükleniyor" />}

      {list.isSuccess && items.length === 0 && (
        <EmptyState>
          {hasFilters ? (
            <>
              <p>Bu filtrelerle gönderi yok.</p>
              <button type="button" className="btn" onClick={clearAll}>
                Filtreleri temizle
              </button>
            </>
          ) : user.isOps ? (
            <>
              <p>Henüz gönderi yok. İlk gönderiyi oluşturun.</p>
              <Link className="btn btn-primary" to="/shipments/new">
                + Gönderi oluştur
              </Link>
            </>
          ) : (
            <p>Hub kapsamınızda gönderi yok.</p>
          )}
        </EmptyState>
      )}

      {items.length > 0 && (
        <>
          <div className="table-wrap" tabIndex={0} role="region" aria-label="Gönderi tablosu">
            <table>
              <caption className="sr-only">Gönderiler</caption>
              <thead>
                <tr>
                  <th scope="col">Tracking</th>
                  <th scope="col">Durum</th>
                  <th scope="col" aria-sort={ariaSort("promisedDeliveryDate", "-promisedDeliveryDate")}>
                    <button type="button" className="sort" onClick={() => toggleSort("promisedDeliveryDate", "-promisedDeliveryDate", "promisedDeliveryDate")}>
                      Söz
                    </button>
                  </th>
                  <th scope="col">Gecikme</th>
                  <th scope="col">Rota</th>
                  <th scope="col">Şu an</th>
                  <th scope="col">Alıcı şehri</th>
                  <th scope="col" className="r">
                    Koli
                  </th>
                  <th scope="col">İstisna</th>
                  <th scope="col" aria-sort={ariaSort("statusChangedAt", "-statusChangedAt")}>
                    <button type="button" className="sort" onClick={() => toggleSort("statusChangedAt", "-statusChangedAt", "-statusChangedAt")}>
                      Son değişim
                    </button>
                  </th>
                  <th scope="col" aria-sort={ariaSort("createdAt", "-createdAt")}>
                    <button type="button" className="sort" onClick={() => toggleSort("createdAt", "-createdAt", "-createdAt")}>
                      Oluşturma
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.id}>
                    <td>
                      <Link className="mono" to={`/shipments/${item.id}`}>
                        {item.trackingNumber}
                      </Link>
                    </td>
                    <td>
                      <StatusBadge status={item.status} />
                    </td>
                    <td className="num">{formatBusinessDate(item.promisedDeliveryDate)}</td>
                    <td>
                      {item.isDelayed ? (
                        <span className="delay">
                          <Icon name="clock" />
                          {delayText(item, today)}
                        </span>
                      ) : (
                        <span className="muted">—</span>
                      )}
                    </td>
                    <td className="mono" title={`${hubName(item.originHubCode) ?? item.originHubCode} → ${hubName(item.destinationHubCode) ?? item.destinationHubCode}`}>
                      {item.originHubCode} → {item.destinationHubCode}
                    </td>
                    <td className="mono">{item.currentHubCode ?? <span className="muted">—</span>}</td>
                    <td>{item.recipientCity || <span className="muted">—</span>}</td>
                    <td className="r num">{item.packageCount}</td>
                    <td>
                      {item.openExceptionCount > 0 ? (
                        <span className="exc-count">
                          <Icon name="flag" />
                          {item.openExceptionCount}
                          <span className="sr-only"> açık istisna</span>
                        </span>
                      ) : (
                        <span className="muted">—</span>
                      )}
                    </td>
                    <td className="num">
                      <time dateTime={item.statusChangedAt}>{formatDateTime(item.statusChangedAt)}</time>
                    </td>
                    <td className="num">
                      <time dateTime={item.createdAt}>{formatDateTime(item.createdAt)}</time>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="table-foot">
            <span role="status" aria-live="polite">
              {formatNumber(items.length)} gönderi listeleniyor{list.hasNextPage ? " (devamı var)" : ""}
            </span>
            {list.hasNextPage && (
              <button type="button" className="btn" disabled={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>
                {list.isFetchingNextPage ? "Yükleniyor…" : "Daha fazla yükle"}
              </button>
            )}
          </div>
        </>
      )}
    </>
  );
}
