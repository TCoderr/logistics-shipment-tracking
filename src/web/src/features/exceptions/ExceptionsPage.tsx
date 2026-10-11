import { useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { Fragment, useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { exceptionsApi } from "../../api/endpoints";
import { ApiError } from "../../api/http";
import type { ExceptionFilters, ExceptionItem, ExceptionType, ResolutionCode } from "../../api/types";
import { useSessionUser } from "../../auth/AuthContext";
import { EmptyState, ErrorNotice, Notice, TableSkeleton } from "../../components/Feedback";
import { FilterSelect, SelectField, TextAreaField } from "../../components/Field";
import { Icon } from "../../components/Icon";
import { StatusBadge, shipmentStatusLabel } from "../../components/StatusBadge";
import { formatDateTime, formatDuration, formatNumber } from "../../lib/format";
import { exceptionTypeLabels, resolutionLabels, severityLabels } from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";
import { useHubs } from "../../lib/reference";
import { useNow } from "../../lib/useNow";

type Tab = "mine" | "unassigned" | "open" | "resolved";

const tabs: { id: Tab; label: string }[] = [
  { id: "mine", label: "Benim" },
  { id: "unassigned", label: "Sahipsiz" },
  { id: "open", label: "Tümü açık" },
  { id: "resolved", label: "Çözülen" },
];
const exceptionTypes = Object.keys(exceptionTypeLabels) as ExceptionType[];
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function readFilters(params: URLSearchParams): { tab: Tab; filters: ExceptionFilters } {
  const rawTab = params.get("tab");
  const tab: Tab = rawTab === "mine" || rawTab === "unassigned" || rawTab === "resolved" ? rawTab : "open";
  const filters: ExceptionFilters = {};
  if (tab === "mine") filters.assignedTo = "me";
  if (tab === "unassigned") filters.assignedTo = "unassigned";
  if (tab === "resolved") {
    filters.status = ["Resolved"];
    filters.sort = "-openedAt";
  }
  const type = params.get("type");
  if (type && exceptionTypes.includes(type as ExceptionType)) filters.type = type as ExceptionType;
  if (params.get("hubCode")) filters.hubCode = params.get("hubCode")!;
  if (params.get("overdue") === "true" && tab !== "resolved") filters.overdue = true;
  const shipmentId = params.get("shipmentId");
  if (shipmentId && uuid.test(shipmentId)) filters.shipmentId = shipmentId;
  return { tab, filters };
}

type Feedback = { tone: "good" | "warn" | "serious"; text: string } | { tone: "error"; error: unknown };

function describeCommandError(error: unknown): Feedback {
  if (error instanceof ApiError) {
    switch (error.code) {
      case "precondition-failed":
        return { tone: "warn", text: "Bu istisna siz bakarken güncellendi. Güncel durumu yükledik; işlemi tekrar gözden geçirin." };
      case "exception-resolved":
        return { tone: "warn", text: "İstisna zaten çözülmüş. Kuyruk yenilendi." };
      case "forbidden":
        return { tone: "serious", text: error.problem.detail ?? "Bu işlem için yetkiniz yok." };
      case "transition-not-allowed":
      case "return-not-allowed": {
        const status = typeof error.problem.currentShipmentStatus === "string" ? shipmentStatusLabel(error.problem.currentShipmentStatus) : null;
        return { tone: "serious", text: `Gönderi bu çözüm için uygun durumda değil${status ? ` (durum: ${status})` : ""}. İstisna değiştirilmedi.` };
      }
      case "resolution-not-allowed":
      case "invalid-assignee":
        return { tone: "serious", text: error.problem.detail ?? error.problem.title ?? "İşlem reddedildi." };
      case "exception-busy":
        return { tone: "warn", text: "İstisna şu an güncelleniyor. Birkaç saniye sonra yeniden deneyin." };
    }
  }
  return { tone: "error", error };
}

function DueCell({ item, now }: { item: ExceptionItem; now: number }) {
  if (item.status === "Resolved") {
    return item.resolvedAt ? <time dateTime={item.resolvedAt}>{formatDateTime(item.resolvedAt)}</time> : <span className="muted">—</span>;
  }
  const due = Date.parse(item.dueAt);
  return (
    <span className={item.isOverdue ? "overdue" : undefined}>
      {item.isOverdue && <Icon name="clock" />} {item.isOverdue ? `${formatDuration(now - due)} geçti` : formatDuration(due - now)}{" "}
      <time className="muted" dateTime={item.dueAt}>
        ({formatDateTime(item.dueAt)})
      </time>
    </span>
  );
}

export function ExceptionsPage() {
  const user = useSessionUser();
  const queryClient = useQueryClient();
  const hubs = useHubs();
  const [params, setParams] = useSearchParams();
  const { tab, filters } = useMemo(() => readFilters(params), [params]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  // Yazılan not ve seçim, kuyruk yenilense de (ör. 412 sonrası) istisna bazında korunur.
  const [drafts, setDrafts] = useState<Record<string, { code: string; note: string }>>({});
  const now = useNow(30_000);

  const list = useInfiniteQuery({
    queryKey: queryKeys.exceptions(filters),
    queryFn: ({ pageParam, signal }) => exceptionsApi.list(filters, pageParam, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
  });
  const items = list.data?.pages.flatMap((page) => page.items) ?? [];

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") setSelectedId(null);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const setParam = (key: string, value: string | null) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next);
  };

  const afterChange = async () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.shipmentsRoot });
    void queryClient.invalidateQueries({ queryKey: queryKeys.summaryRoot });
    await queryClient.invalidateQueries({ queryKey: queryKeys.exceptionsRoot });
  };

  const execute = async (item: ExceptionItem, command: () => Promise<unknown>, success: string) => {
    setBusyId(item.id);
    setFeedback(null);
    setFieldErrors({});
    try {
      await command();
      setFeedback({ tone: "good", text: success });
      await afterChange();
      return true;
    } catch (error) {
      if (error instanceof ApiError && error.code === "validation") {
        setFieldErrors(error.fieldErrors);
      } else {
        setFeedback(describeCommandError(error));
        // Sunucu otoritedir: çakışma/ret sonrası kuyruk yeniden okunur.
        if (error instanceof ApiError) await afterChange();
      }
      return false;
    } finally {
      setBusyId(null);
    }
  };

  const assignToMe = (item: ExceptionItem) => execute(item, () => exceptionsApi.assign(item.id, item.etag, user.id), "İstisna üzerinize alındı.");

  const resolve = async (item: ExceptionItem) => {
    const draft = drafts[item.id] ?? { code: "", note: "" };
    const errors: Record<string, string[]> = {};
    if (!draft.code) errors.resolutionCode = ["Bir çözüm seçin."];
    if (draft.note.trim() === "") errors.note = ["Çözüm notu zorunludur."];
    if (Object.keys(errors).length > 0) {
      setFieldErrors(errors);
      return;
    }
    const done = await execute(item, () => exceptionsApi.resolve(item.id, item.etag, draft.code as ResolutionCode, draft.note.trim()), "İstisna çözüldü.");
    if (done) {
      setSelectedId(null);
      setDrafts((current) => {
        const rest = { ...current };
        delete rest[item.id];
        return rest;
      });
    }
  };

  const hasExtraFilters = Boolean(filters.type || filters.hubCode || filters.overdue || filters.shipmentId);

  return (
    <>
      <div className="page-head">
        <h1>İstisnalar</h1>
      </div>

      <div className="subtabs" role="group" aria-label="Kuyruk görünümü">
        {tabs.map((entry) => (
          <button
            key={entry.id}
            type="button"
            className="subtab"
            aria-pressed={tab === entry.id}
            onClick={() => {
              setSelectedId(null);
              setParam("tab", entry.id === "open" ? null : entry.id);
            }}
          >
            {entry.label}
          </button>
        ))}
      </div>

      <div className="filters" role="group" aria-label="Filtreler">
        <FilterSelect label="Tip" value={filters.type ?? ""} onChange={(event) => setParam("type", event.target.value || null)}>
            <option value="">Tümü</option>
            {exceptionTypes.map((type) => (
              <option key={type} value={type}>
                {exceptionTypeLabels[type]}
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
        {tab !== "resolved" && (
          <label>
            <input type="checkbox" checked={filters.overdue === true} onChange={(event) => setParam("overdue", event.target.checked ? "true" : null)} />
            Yalnız vadesi geçenler
          </label>
        )}
        {filters.shipmentId && (
          <span className="chip">
            Tek gönderi
            <button type="button" aria-label="Gönderi filtresini kaldır" onClick={() => setParam("shipmentId", null)}>
              ×
            </button>
          </span>
        )}
      </div>

      <div aria-live="polite">
        {feedback && feedback.tone !== "error" && (
          <Notice tone={feedback.tone} role={feedback.tone === "good" ? "status" : "alert"}>
            <p>{feedback.text}</p>
          </Notice>
        )}
      </div>
      {feedback?.tone === "error" && <ErrorNotice error={feedback.error} subject="İşlem" verb="tamamlanamadı" />}
      {list.isError && <ErrorNotice error={list.error} subject="İstisnalar" onRetry={() => void queryClient.resetQueries({ queryKey: queryKeys.exceptions(filters) })} />}
      {list.isPending && <TableSkeleton label="İstisnalar yükleniyor" />}

      {list.isSuccess && items.length === 0 && (
        <EmptyState>
          <p>
            {tab === "mine"
              ? "Üzerinizde açık istisna yok."
              : tab === "unassigned"
                ? "Sahipsiz istisna yok."
                : tab === "resolved"
                  ? "Çözülmüş istisna yok."
                  : "Açık istisna yok."}
            {hasExtraFilters && " Filtreleri değiştirerek tekrar deneyin."}
          </p>
        </EmptyState>
      )}

      {items.length > 0 && (
        <>
          <div className="table-wrap" tabIndex={0} role="region" aria-label="İstisna tablosu">
            <table>
              <caption className="sr-only">İstisna kuyruğu</caption>
              <thead>
                <tr>
                  <th scope="col">Önem</th>
                  <th scope="col">Tip</th>
                  <th scope="col">Gönderi</th>
                  <th scope="col">Gönderi durumu</th>
                  <th scope="col">Hub</th>
                  <th scope="col">{tab === "resolved" ? "Çözüldü" : "Vade"}</th>
                  <th scope="col">Sahibi</th>
                  <th scope="col">
                    <span className="sr-only">İşlemler</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => {
                  const selected = selectedId === item.id;
                  const mine = item.assignedTo?.id === user.id;
                  const canResolve = item.status !== "Resolved" && (user.isManager || mine);
                  const draft = drafts[item.id] ?? { code: "", note: "" };
                  const setDraft = (change: Partial<{ code: string; note: string }>) =>
                    setDrafts((current) => ({ ...current, [item.id]: { ...draft, ...change } }));
                  return (
                    <Fragment key={item.id}>
                      <tr className={selected ? "is-selected" : undefined}>
                        <td>
                          <span className={`sev sev-${item.severity.toLowerCase()}`}>
                            <Icon name="diamond" />
                            {severityLabels[item.severity] ?? item.severity}
                          </span>
                        </td>
                        <td>
                          {exceptionTypeLabels[item.type] ?? item.type}
                          {item.occurrenceCount > 1 && <span className="muted num"> ×{item.occurrenceCount}</span>}
                        </td>
                        <td>
                          <Link className="mono" to={`/shipments/${item.shipment.id}`}>
                            {item.shipment.trackingNumber}
                          </Link>
                        </td>
                        <td>
                          <StatusBadge status={item.shipment.status} />
                        </td>
                        <td className="mono">{item.hubCode ?? <span className="muted">—</span>}</td>
                        <td className="num">
                          <DueCell item={item} now={now} />
                        </td>
                        <td>{item.assignedTo ? (mine ? "Siz" : item.assignedTo.displayName) : <span className="muted">—</span>}</td>
                        <td>
                          <span className="actions">
                            {item.status === "Open" && (
                              <button
                                type="button"
                                className="btn"
                                aria-label={`Üzerime al: ${item.shipment.trackingNumber}`}
                                disabled={busyId === item.id}
                                onClick={() => void assignToMe(item)}
                              >
                                Üzerime al
                              </button>
                            )}
                            <button
                              type="button"
                              className="btn"
                              aria-expanded={selected}
                              aria-label={`${selected ? "Kapat" : item.status === "Resolved" ? "Ayrıntı" : "Çöz"}: ${item.shipment.trackingNumber}`}
                              onClick={() => {
                                setFieldErrors({});
                                setSelectedId(selected ? null : item.id);
                              }}
                            >
                              {selected ? "Kapat" : item.status === "Resolved" ? "Ayrıntı" : "Çöz"}
                            </button>
                          </span>
                        </td>
                      </tr>
                      {selected && (
                        <tr className="resolve-row">
                          <td colSpan={8}>
                            <div className="resolve">
                              <dl className="kv full">
                                <dt>Açıldı</dt>
                                <dd>
                                  <time dateTime={item.openedAt}>{formatDateTime(item.openedAt)}</time> · {item.occurrenceCount} kez tekrarlandı
                                </dd>
                                {item.resolutionCode && (
                                  <>
                                    <dt>Çözüm</dt>
                                    <dd>{resolutionLabels[item.resolutionCode] ?? item.resolutionCode}</dd>
                                  </>
                                )}
                              </dl>
                              {item.status !== "Resolved" && !canResolve && (
                                <p className="full why-not">
                                  Çözebilmek için istisnanın sizde olması gerekir.{" "}
                                  {item.status === "Open" ? "Önce üzerinize alın." : "Bu istisna başka bir görevliye atanmış."}
                                </p>
                              )}
                              {canResolve && (
                                <form
                                  className="full resolve-form"
                                  noValidate
                                  aria-label={`${item.shipment.trackingNumber} istisna çözümü`}
                                  onSubmit={(event) => {
                                    event.preventDefault();
                                    void resolve(item);
                                  }}
                                >
                                  <SelectField label="Çözüm" name="resolutionCode" required value={draft.code} errors={fieldErrors.resolutionCode} onChange={(event) => setDraft({ code: event.target.value })}>
                                    <option value="">Seçin…</option>
                                    {item.allowedResolutions.map((code) => (
                                      <option key={code} value={code}>
                                        {resolutionLabels[code] ?? code}
                                      </option>
                                    ))}
                                  </SelectField>
                                  <TextAreaField label="Çözüm notu" name="note" required maxLength={1000} value={draft.note} errors={fieldErrors.note} onChange={(event) => setDraft({ note: event.target.value })} />
                                  <div className="actions">
                                    <button type="submit" className="btn btn-primary" disabled={busyId === item.id}>
                                      {busyId === item.id ? "Kaydediliyor…" : "Çözümü kaydet"}
                                    </button>
                                    <button type="button" className="btn" onClick={() => setSelectedId(null)}>
                                      Kapat
                                    </button>
                                  </div>
                                </form>
                              )}
                            </div>
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="table-foot">
            <span role="status" aria-live="polite">
              {formatNumber(items.length)} istisna listeleniyor{list.hasNextPage ? " (devamı var)" : ""}
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
