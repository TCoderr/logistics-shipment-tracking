import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { scanApi } from "../../api/endpoints";
import { ApiError, NetworkError } from "../../api/http";
import type { ScanType, ScanViews } from "../../api/types";
import { useSessionUser } from "../../auth/AuthContext";
import { Notice } from "../../components/Feedback";
import { SelectField, TextField } from "../../components/Field";
import { Icon, type IconName } from "../../components/Icon";
import { shipmentStatusLabel } from "../../components/StatusBadge";
import { formatTime } from "../../lib/format";
import { reasonLabel, scanTypeLabels } from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";

const labelPattern = /^LS[0-9]{10}-[0-9]{2}$/;

type Tone = "good" | "info" | "warn" | "critical";

interface ScanResult {
  at: Date;
  labelCode: string;
  scanType: ScanType;
  tone: Tone;
  icon: IconName;
  headline: string;
  detail: string | null;
  shipmentId: string | null;
}

function describeViews(views: ScanViews | null): string {
  if (!views) return "";
  return `${shipmentStatusLabel(views.shipment.status)} · ${views.consolidation.packagesAtHub}/${views.consolidation.packageCount} koli bu hub'da`;
}

function isScanViews(value: unknown): value is ScanViews {
  return typeof value === "object" && value !== null && "shipment" in value && "consolidation" in value;
}

export function ScanPage() {
  const user = useSessionUser();
  const queryClient = useQueryClient();
  const [hubCode, setHubCode] = useState(user.hubCodes[0] ?? "");
  const [scanType, setScanType] = useState<ScanType>("Arrival");
  const [labelCode, setLabelCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [result, setResult] = useState<ScanResult | null>(null);
  const [history, setHistory] = useState<ScanResult[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);
  // Yanıtı alınamayan tarama aynı kimlikle tekrar gönderilir: sunucu teknik tekrarı tek etkiyle karşılar.
  const pending = useRef<{ signature: string; clientScanId: string; occurredAt: string } | null>(null);

  if (user.hubCodes.length === 0) {
    return (
      <Notice tone="neutral" role="alert" icon="lock">
        <p>Size atanmış bir hub yok. Hub ataması için operasyon yöneticinize başvurun.</p>
      </Notice>
    );
  }

  const record = (entry: ScanResult) => {
    setResult(entry);
    setHistory((current) => [entry, ...current].slice(0, 10));
  };

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const label = labelCode.trim().toUpperCase();
    if (!labelPattern.test(label)) {
      setFieldErrors({ labelCode: ["Koli etiketi LS + 10 rakam + '-' + 2 rakam biçiminde olmalı (ör. LS4829105735-01)."] });
      return;
    }
    setFieldErrors({});
    const signature = `${hubCode}|${scanType}|${label}`;
    if (pending.current?.signature !== signature) {
      pending.current = { signature, clientScanId: `web-${crypto.randomUUID()}`, occurredAt: new Date().toISOString() };
    }
    const attempt = pending.current;
    const base = { at: new Date(), labelCode: label, scanType };
    setBusy(true);
    try {
      const response = await scanApi.scan(hubCode, { clientScanId: attempt.clientScanId, labelCode: label, scanType, occurredAt: attempt.occurredAt });
      pending.current = null;
      const data = response.data;
      const reason = reasonLabel(data.reasonCode);
      const summary = [describeViews(data), reason].filter(Boolean).join(" · ");
      if (data.outcome === "Applied") {
        record({ ...base, tone: "good", icon: "delivered", headline: "Uygulandı", detail: summary, shipmentId: data.shipment.id });
      } else if (data.outcome === "Duplicate") {
        record({ ...base, tone: "info", icon: "info", headline: "Tekrar · daha önce kaydedildi", detail: summary, shipmentId: data.shipment.id });
      } else {
        record({ ...base, tone: "warn", icon: "clock", headline: "Geç tarama · uygulanmadı", detail: summary, shipmentId: data.shipment.id });
      }
      setLabelCode("");
      void queryClient.invalidateQueries({ queryKey: queryKeys.shipmentsRoot });
    } catch (error) {
      if (error instanceof NetworkError) {
        record({
          ...base,
          tone: "warn",
          icon: "plug",
          headline: "Yanıt alınamadı",
          detail: "Tarama sunucuya ulaşmış olabilir. Aynı etiketi yeniden gönderin; aynı tarama kimliği kullanılır, çift kayıt oluşmaz.",
          shipmentId: null,
        });
      } else if (error instanceof ApiError) {
        pending.current = null;
        if (error.code === "validation") {
          setFieldErrors(error.fieldErrors);
        } else {
          const views = isScanViews(error.problem) ? error.problem : null;
          const reasonCode = typeof error.problem.reasonCode === "string" ? error.problem.reasonCode : null;
          const texts: Record<string, string> = {
            "not-found": "Koli etiketi bulunamadı",
            forbidden: "Bu hub'da tarama yetkiniz yok",
            "hub-inactive": "Hub aktif değil",
            "client-scan-id-conflict": "Tarama kimliği çakıştı; yeniden deneyin",
            "shipment-busy": "Gönderi şu an güncelleniyor; birkaç saniye sonra yeniden deneyin",
          };
          const text = texts[error.code] ?? reasonLabel(reasonCode ?? error.code) ?? "İşlem reddedildi";
          record({
            ...base,
            tone: "critical",
            icon: "failed",
            headline: `Reddedildi · ${text}`,
            detail: describeViews(views) || null,
            shipmentId: views?.shipment.id ?? null,
          });
          setLabelCode("");
        }
      } else {
        record({ ...base, tone: "critical", icon: "failed", headline: "Tarama gönderilemedi", detail: null, shipmentId: null });
      }
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  };

  return (
    <>
      <div className="page-head">
        <h1>Hub taraması</h1>
      </div>
      <form className="panel scan-form" onSubmit={(event) => void onSubmit(event)} noValidate aria-label="Tarama formu">
        <SelectField label="Hub" name="hubCode" value={hubCode} onChange={(event) => setHubCode(event.target.value)}>
          {user.hubCodes.map((code) => (
            <option key={code} value={code}>
              {code}
            </option>
          ))}
        </SelectField>
        <SelectField label="Tarama tipi" name="scanType" value={scanType} onChange={(event) => setScanType(event.target.value as ScanType)}>
          {(Object.keys(scanTypeLabels) as ScanType[]).map((type) => (
            <option key={type} value={type}>
              {scanTypeLabels[type]}
            </option>
          ))}
        </SelectField>
        <TextField
          ref={inputRef}
          label="Koli etiketi"
          name="labelCode"
          autoFocus
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          placeholder="LS0000000000-01"
          value={labelCode}
          errors={fieldErrors.labelCode ?? fieldErrors.occurredAt ?? fieldErrors.clientScanId}
          onChange={(event) => setLabelCode(event.target.value)}
        />
        <button type="submit" className="btn btn-primary" disabled={busy}>
          {busy ? "Gönderiliyor…" : "Taramayı kaydet"}
        </button>
      </form>

      {/* Sonuç büyük ve sesli okunur; durum renkle değil ikon + metinle verilir. */}
      <div aria-live="assertive" aria-atomic="true">
        {result && (
          <div className={`scan-result t-${result.tone}`}>
            <Icon name={result.icon} />
            <div>
              {result.headline}
              <small>
                <span className="mono">{result.labelCode}</span> · {scanTypeLabels[result.scanType]}
                {result.detail ? ` · ${result.detail}` : ""}
              </small>
            </div>
          </div>
        )}
      </div>

      {history.length > 0 && (
        <section className="panel scan-history" aria-labelledby="scan-history-heading">
          <div className="panel-head">
            <h2 id="scan-history-heading">Bu oturumdaki son taramalar</h2>
          </div>
          <div className="table-wrap" tabIndex={0} role="region" aria-label="Son taramalar tablosu">
            <table>
              <thead>
                <tr>
                  <th scope="col">Saat</th>
                  <th scope="col">Etiket</th>
                  <th scope="col">Tip</th>
                  <th scope="col">Sonuç</th>
                </tr>
              </thead>
              <tbody>
                {history.map((entry, index) => (
                  <tr key={`${entry.at.getTime()}-${index}`}>
                    <td className="num">{formatTime(entry.at)}</td>
                    <td className="mono">{entry.shipmentId ? <Link to={`/shipments/${entry.shipmentId}`}>{entry.labelCode}</Link> : entry.labelCode}</td>
                    <td>{scanTypeLabels[entry.scanType]}</td>
                    <td>{entry.headline}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </>
  );
}
