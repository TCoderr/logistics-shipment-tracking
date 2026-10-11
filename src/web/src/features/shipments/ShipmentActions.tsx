import { useState } from "react";
import { shipmentsApi } from "../../api/endpoints";
import { ApiError, NetworkError } from "../../api/http";
import type { DeliveryFailureReason, DeliveryOutcome, ShipmentDetail } from "../../api/types";
import type { SessionUser } from "../../auth/AuthContext";
import { ErrorNotice, Notice } from "../../components/Feedback";
import { SelectField, TextAreaField, TextField } from "../../components/Field";
import { shipmentStatusLabel } from "../../components/StatusBadge";
import { formatTime, localInputToOffsetIso, nowAsLocalInput } from "../../lib/format";
import { createIdempotentOperation } from "../../lib/idempotency";
import { failureReasonLabels } from "../../lib/labels";
import { useCarriers } from "../../lib/reference";

type Panel = "cancel" | "return" | "complete" | "attempt" | "carrier" | "rotate";

type Feedback =
  | { kind: "success"; message: string }
  | { kind: "conflict"; status: string; at: string }
  | { kind: "refused"; error: ApiError }
  | { kind: "unconfirmed" }
  | { kind: "error"; error: unknown };

const refusalTexts: Record<string, string> = {
  "shipment-not-cancellable": "Gönderi bu durumda iptal edilemez.",
  "return-not-allowed": "Bu durumda göndericiye iade başlatılamaz.",
  "transition-not-allowed": "Gönderi bu işlem için uygun durumda değil.",
  "carrier-assignment-not-allowed": "Bu durumda carrier atanamaz.",
  "packages-not-out-for-delivery": "Kolilerin hepsi dağıtıma çıkmadı. Eksik koli için başarısız deneme (Koli eksik) kaydedin.",
  "invalid-occurred-at": "Olay zamanı geçersiz: gelecekte ya da dağıtıma çıkıştan önce olamaz.",
  "unknown-carrier": "Carrier bulunamadı.",
  "carrier-inactive": "Carrier aktif değil.",
  "shipment-busy": "Gönderi şu an başka bir işlemle güncelleniyor. Birkaç saniye sonra yeniden deneyin.",
};

export interface ShipmentActionsProps {
  detail: ShipmentDetail;
  etag: string | null;
  user: SessionUser;
  /** Güncel durumu sunucudan yeniden okur (otorite sunucudur) ve okunan detayı döner. */
  refresh: () => Promise<ShipmentDetail | undefined>;
}

export function ShipmentActions({ detail, etag, user, refresh }: ShipmentActionsProps) {
  const [panel, setPanel] = useState<Panel | null>(null);
  const [busy, setBusy] = useState(false);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [reason, setReason] = useState("");
  const [carrierCode, setCarrierCode] = useState("");
  const [outcome, setOutcome] = useState<DeliveryOutcome>("Delivered");
  const [failureReason, setFailureReason] = useState<DeliveryFailureReason>("RecipientAbsent");
  const [attemptNote, setAttemptNote] = useState("");
  const [attemptAt, setAttemptAt] = useState(() => nowAsLocalInput());
  // Kullanıcı zamanı elle değiştirmediyse panelin açıldığı anın tam zamanı gönderilir: dakikaya yuvarlanmış girdi,
  // saniyeler önce yapılan "dağıtıma çıkış" taramasından önceye düşüp reddedilmesin. Değer denemeler arasında sabittir
  // (yanıt kaybında aynı gövde = aynı Idempotency-Key).
  const [attemptOpenedAt, setAttemptOpenedAt] = useState<string | null>(null);
  const [attemptOperation] = useState(() => createIdempotentOperation());

  const allowed = new Set(detail.allowedActions);
  // Carrier atama için ayrı bir allowedActions değeri yok; sunucu kuralı iptal ile aynıdır ve sunucu yine de doğrular.
  const canAssignCarrier = user.isOps && allowed.has("Cancel");
  const canRotate = user.isManager && detail.publicTrackingCode !== null;
  const carriers = useCarriers(canAssignCarrier);
  const activePanel = panel && isAvailable(panel) ? panel : null;

  function isAvailable(candidate: Panel): boolean {
    switch (candidate) {
      case "cancel":
        return allowed.has("Cancel");
      case "return":
        return allowed.has("ReturnToSender");
      case "complete":
        return allowed.has("CompleteReturn");
      case "attempt":
        return allowed.has("RecordDeliveryAttempt");
      case "carrier":
        return canAssignCarrier;
      case "rotate":
        return canRotate;
    }
  }

  const open = (next: Panel) => {
    setPanel(next);
    setFeedback(null);
    setFieldErrors({});
    if (next === "attempt") {
      const now = new Date();
      setAttemptAt(nowAsLocalInput(now));
      setAttemptOpenedAt(now.toISOString());
    }
  };

  /** Komutu çalıştırır; sonuç ne olursa olsun güncel durumu sunucudan yeniden okur. Yazılan metinler korunur. */
  const run = async (command: () => Promise<unknown>, success: string) => {
    setBusy(true);
    setFeedback(null);
    setFieldErrors({});
    try {
      await command();
      setPanel(null);
      setReason("");
      setAttemptNote("");
      setFeedback({ kind: "success", message: success });
      await refresh();
    } catch (error) {
      if (error instanceof ApiError && error.status === 412) {
        const fresh = await refresh();
        setFeedback({ kind: "conflict", status: fresh?.status ?? detail.status, at: fresh?.statusChangedAt ?? detail.statusChangedAt });
      } else if (error instanceof ApiError && error.code === "validation") {
        setFieldErrors(error.fieldErrors);
      } else if (error instanceof ApiError && (error.status === 409 || error.status === 422 || error.code === "shipment-busy")) {
        setFeedback({ kind: "refused", error });
        await refresh();
      } else if (error instanceof NetworkError && panel === "attempt") {
        attemptOperation.markUnconfirmed();
        setFeedback({ kind: "unconfirmed" });
      } else {
        setFeedback({ kind: "error", error });
      }
    } finally {
      setBusy(false);
    }
  };

  const requireEtag = (): string | null => {
    if (etag) return etag;
    setFeedback({ kind: "error", error: new Error("etag") });
    return null;
  };

  const submitAttempt = () => {
    const occurredAt = attemptOpenedAt ?? localInputToOffsetIso(attemptAt);
    if (!occurredAt) {
      setFieldErrors({ occurredAt: ["Geçerli bir tarih ve saat girin."] });
      return;
    }
    if (outcome === "Failed" && failureReason === "Other" && attemptNote.trim() === "") {
      setFieldErrors({ note: ["'Diğer' nedeni için not zorunludur."] });
      return;
    }
    const body = {
      outcome,
      failureReason: outcome === "Failed" ? failureReason : null,
      occurredAt,
      note: attemptNote.trim() === "" ? null : attemptNote.trim(),
    };
    // Aynı gövde yeniden gönderilirse aynı Idempotency-Key kullanılır (yanıt kaybında ikinci deneme kaydı oluşmaz).
    const { key } = attemptOperation.keyFor(body);
    void run(async () => {
      try {
        await shipmentsApi.recordDeliveryAttempt(detail.id, body, key);
        attemptOperation.complete();
      } catch (error) {
        const retryable =
          error instanceof NetworkError ||
          (error instanceof ApiError && (error.code === "idempotency-request-in-progress" || error.code === "commit-outcome-unknown"));
        if (!retryable) attemptOperation.complete();
        if (error instanceof ApiError && retryable) throw new NetworkError();
        throw error;
      }
    }, outcome === "Delivered" ? "Teslimat kaydedildi." : "Başarısız teslimat denemesi kaydedildi.");
  };

  const terminal = ["Delivered", "ReturnedToSender", "Cancelled"].includes(detail.status);
  const hasAnyAction = detail.allowedActions.length > 0 || canAssignCarrier || canRotate;

  return (
    <section className="panel" aria-labelledby="actions-heading">
      <div className="panel-head">
        <h2 id="actions-heading">İzinli işlemler</h2>
      </div>

      {feedback?.kind === "success" && (
        <Notice tone="good" role="status">
          <p>{feedback.message}</p>
        </Notice>
      )}
      {feedback?.kind === "conflict" && (
        <Notice tone="warn" role="alert">
          <p>
            Bu gönderi siz bakarken güncellendi ({shipmentStatusLabel(feedback.status)}, {formatTime(feedback.at)}). Güncel durumu yükledik; işlemi tekrar
            gözden geçirin.
          </p>
        </Notice>
      )}
      {feedback?.kind === "refused" && (
        <Notice tone="serious" role="alert">
          <p>{refusalTexts[feedback.error.code] ?? feedback.error.problem.title ?? "İşlem reddedildi."}</p>
          {feedback.error.problem.returnToSenderAllowed === true && <p>Bu gönderi için göndericiye iade başlatılabilir.</p>}
        </Notice>
      )}
      {feedback?.kind === "unconfirmed" && (
        <Notice tone="warn" role="alert" icon="plug">
          <p>Yanıt alınamadı. Kayıt sunucuya ulaşmış olabilir; aynı bilgilerle yeniden denemek güvenlidir (ikinci deneme kaydı oluşmaz).</p>
        </Notice>
      )}
      {feedback?.kind === "error" && <ErrorNotice error={feedback.error} subject="İşlem" verb="tamamlanamadı" />}

      <div className="actions">
        {allowed.has("RecordDeliveryAttempt") && (
          <button type="button" className="btn btn-primary" aria-expanded={activePanel === "attempt"} onClick={() => open("attempt")}>
            Teslimat denemesi kaydet
          </button>
        )}
        {allowed.has("ReturnToSender") && (
          <button type="button" className="btn" aria-expanded={activePanel === "return"} onClick={() => open("return")}>
            Göndericiye iade başlat
          </button>
        )}
        {allowed.has("CompleteReturn") && (
          <button type="button" className="btn" aria-expanded={activePanel === "complete"} onClick={() => open("complete")}>
            İadeyi tamamla
          </button>
        )}
        {canAssignCarrier && (
          <button type="button" className="btn" aria-expanded={activePanel === "carrier"} onClick={() => open("carrier")}>
            {detail.carrier ? "Carrier değiştir" : "Carrier ata"}
          </button>
        )}
        {allowed.has("Cancel") && (
          <button type="button" className="btn btn-danger" aria-expanded={activePanel === "cancel"} onClick={() => open("cancel")}>
            Gönderiyi iptal et
          </button>
        )}
        {canRotate && (
          <button type="button" className="btn" aria-expanded={activePanel === "rotate"} onClick={() => open("rotate")}>
            Takip kodunu yenile
          </button>
        )}
      </div>

      {!hasAnyAction && <p className="why-not">{terminal ? "Gönderi tamamlandı; yapılabilecek işlem yok." : "Bu gönderi için size açık bir işlem yok."}</p>}
      {user.isOps && !terminal && !allowed.has("Cancel") && detail.status !== "ReturningToSender" && (
        <p className="why-not">İptal yok: gönderi hub'dan çıkış yaptı veya carrier'a devredildi.</p>
      )}

      {activePanel === "cancel" && (
        <form
          className="confirm"
          noValidate
          aria-label="Gönderi iptali"
          onSubmit={(event) => {
            event.preventDefault();
            const current = requireEtag();
            if (current) void run(() => shipmentsApi.cancel(detail.id, current, reason), "Gönderi iptal edildi.");
          }}
        >
          <TextAreaField label="İptal nedeni" name="reason" required maxLength={500} value={reason} errors={fieldErrors.reason} onChange={(event) => setReason(event.target.value)} />
          <div className="actions">
            <button type="submit" className="btn btn-danger" disabled={busy}>
              {busy ? "İptal ediliyor…" : "Gönderiyi iptal et"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}

      {activePanel === "return" && (
        <form
          className="confirm"
          noValidate
          aria-label="Göndericiye iade"
          onSubmit={(event) => {
            event.preventDefault();
            const current = requireEtag();
            if (current) void run(() => shipmentsApi.returnToSender(detail.id, current, reason), "Göndericiye iade başlatıldı.");
          }}
        >
          <TextAreaField label="İade nedeni" name="reason" required maxLength={500} value={reason} errors={fieldErrors.reason} onChange={(event) => setReason(event.target.value)} />
          <div className="actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? "Başlatılıyor…" : "Göndericiye iadeyi başlat"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}

      {activePanel === "complete" && (
        <form
          className="confirm"
          noValidate
          aria-label="İade tamamlama"
          onSubmit={(event) => {
            event.preventDefault();
            const current = requireEtag();
            if (current) void run(() => shipmentsApi.completeReturn(detail.id, current), "İade tamamlandı.");
          }}
        >
          <p>Gönderinin göndericiye teslim edildiğini onaylıyor musunuz? Bu işlem gönderiyi kapatır.</p>
          <div className="actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? "Kaydediliyor…" : "İadeyi tamamla"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}

      {activePanel === "carrier" && (
        <form
          className="confirm"
          noValidate
          aria-label="Carrier atama"
          onSubmit={(event) => {
            event.preventDefault();
            const current = requireEtag();
            if (!carrierCode) {
              setFieldErrors({ carrierCode: ["Bir carrier seçin."] });
              return;
            }
            if (current) void run(() => shipmentsApi.assignCarrier(detail.id, current, carrierCode), "Carrier atandı.");
          }}
        >
          {carriers.isError && <ErrorNotice error={carriers.error} subject="Carrier listesi" onRetry={() => void carriers.refetch()} />}
          <SelectField label="Carrier" name="carrierCode" required value={carrierCode} errors={fieldErrors.carrierCode} onChange={(event) => setCarrierCode(event.target.value)}>
            <option value="">Seçin…</option>
            {(carriers.data ?? [])
              .filter((carrier) => carrier.isActive)
              .map((carrier) => (
                <option key={carrier.code} value={carrier.code}>
                  {carrier.name} ({carrier.code})
                </option>
              ))}
          </SelectField>
          <div className="actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? "Atanıyor…" : "Carrier'ı ata"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}

      {activePanel === "rotate" && (
        <form
          className="confirm"
          noValidate
          aria-label="Takip kodu yenileme"
          onSubmit={(event) => {
            event.preventDefault();
            const current = requireEtag();
            if (current) void run(() => shipmentsApi.rotatePublicCode(detail.id, current), "Takip kodu yenilendi; eski bağlantı artık çalışmaz.");
          }}
        >
          <p>Alıcıya gönderilmiş mevcut takip bağlantısı hemen geçersiz olur. Yeni bağlantıyı alıcıyla yeniden paylaşmanız gerekir.</p>
          <div className="actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? "Yenileniyor…" : "Takip kodunu yenile"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}

      {activePanel === "attempt" && (
        <form
          className="confirm"
          noValidate
          aria-label="Teslimat denemesi"
          onSubmit={(event) => {
            event.preventDefault();
            submitAttempt();
          }}
        >
          <SelectField label="Sonuç" name="outcome" value={outcome} errors={fieldErrors.outcome} onChange={(event) => setOutcome(event.target.value as DeliveryOutcome)}>
            <option value="Delivered">Teslim edildi</option>
            <option value="Failed">Teslim edilemedi</option>
          </SelectField>
          {outcome === "Failed" && (
            <SelectField label="Neden" name="failureReason" value={failureReason} errors={fieldErrors.failureReason} onChange={(event) => setFailureReason(event.target.value as DeliveryFailureReason)}>
              {(Object.keys(failureReasonLabels) as DeliveryFailureReason[]).map((code) => (
                <option key={code} value={code}>
                  {failureReasonLabels[code]}
                </option>
              ))}
            </SelectField>
          )}
          <TextField label="Olay zamanı" name="occurredAt" type="datetime-local" required value={attemptAt}
            errors={fieldErrors.occurredAt}
            onChange={(event) => {
              setAttemptAt(event.target.value);
              setAttemptOpenedAt(null);
            }}
          />
          <TextAreaField
            label={outcome === "Failed" && failureReason === "Other" ? "Not" : "Not (isteğe bağlı)"}
            name="note"
            maxLength={500}
            value={attemptNote}
            errors={fieldErrors.note}
            onChange={(event) => setAttemptNote(event.target.value)}
          />
          <div className="actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? "Kaydediliyor…" : "Denemeyi kaydet"}
            </button>
            <button type="button" className="btn" onClick={() => setPanel(null)}>
              Vazgeç
            </button>
          </div>
        </form>
      )}
    </section>
  );
}
