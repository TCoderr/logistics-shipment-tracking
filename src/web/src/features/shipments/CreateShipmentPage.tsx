import { useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { shipmentsApi } from "../../api/endpoints";
import { ApiError, NetworkError } from "../../api/http";
import type { CreateShipmentRequest, PartyRequest, ServiceLevel } from "../../api/types";
import { ErrorNotice, Notice } from "../../components/Feedback";
import { SelectField, TextField } from "../../components/Field";
import { createIdempotentOperation } from "../../lib/idempotency";
import { serviceLevelLabels } from "../../lib/labels";
import { queryKeys } from "../../lib/queryKeys";
import { useHubs } from "../../lib/reference";

interface PartyForm {
  fullName: string;
  companyName: string;
  phone: string;
  email: string;
  line1: string;
  line2: string;
  district: string;
  city: string;
  postalCode: string;
}

interface PackageForm {
  weightGrams: string;
  lengthMm: string;
  widthMm: string;
  heightMm: string;
  contentDescription: string;
}

interface FormState {
  serviceLevel: ServiceLevel;
  externalReference: string;
  originHubCode: string;
  destinationHubCode: string;
  sender: PartyForm;
  recipient: PartyForm;
  packages: PackageForm[];
  declaredAmount: string;
  declaredCurrency: string;
}

type Errors = Record<string, string[]>;

const emptyParty: PartyForm = { fullName: "", companyName: "", phone: "", email: "", line1: "", line2: "", district: "", city: "", postalCode: "" };
const emptyPackage: PackageForm = { weightGrams: "", lengthMm: "", widthMm: "", heightMm: "", contentDescription: "" };
const maxPackages = 20;

const optional = (value: string): string | null => (value.trim() === "" ? null : value.trim());

function toParty(form: PartyForm): PartyRequest {
  return {
    fullName: form.fullName.trim(),
    companyName: optional(form.companyName),
    phone: form.phone.trim(),
    email: optional(form.email),
    address: {
      line1: form.line1.trim(),
      line2: optional(form.line2),
      district: optional(form.district),
      city: form.city.trim(),
      postalCode: optional(form.postalCode),
      countryCode: "TR",
    },
  };
}

/** Form → istek gövdesi. Yalnız gövdeyi kurmayı engelleyen hatalar burada yakalanır; kurallar sunucuda doğrulanır. */
export function buildCreateRequest(form: FormState): { body: CreateShipmentRequest | null; errors: Errors } {
  const errors: Errors = {};
  const required = (key: string, value: string) => {
    if (value.trim() === "") errors[key] = ["Zorunludur."];
  };
  required("originHubCode", form.originHubCode);
  required("destinationHubCode", form.destinationHubCode);
  for (const role of ["sender", "recipient"] as const) {
    required(`${role}.fullName`, form[role].fullName);
    required(`${role}.phone`, form[role].phone);
    required(`${role}.address.line1`, form[role].line1);
    required(`${role}.address.city`, form[role].city);
    required(`${role}.address.postalCode`, form[role].postalCode);
  }

  const integer = (key: string, value: string): number => {
    if (!/^[0-9]{1,6}$/.test(value.trim())) {
      errors[key] = ["Tam sayı olmalı."];
      return 0;
    }
    return Number(value.trim());
  };
  const packages = form.packages.map((pkg, index) => ({
    weightGrams: integer(`packages[${index}].weightGrams`, pkg.weightGrams),
    lengthMm: integer(`packages[${index}].lengthMm`, pkg.lengthMm),
    widthMm: integer(`packages[${index}].widthMm`, pkg.widthMm),
    heightMm: integer(`packages[${index}].heightMm`, pkg.heightMm),
    contentDescription: optional(pkg.contentDescription),
  }));

  let declaredValue: CreateShipmentRequest["declaredValue"] = null;
  if (form.declaredAmount.trim() !== "") {
    const normalized = form.declaredAmount.trim().replace(",", ".");
    if (!/^[0-9]{1,10}(\.[0-9]{1,2})?$/.test(normalized)) {
      errors["declaredValue.amount"] = ["En fazla 2 ondalıklı bir tutar olmalı (ör. 1850,00)."];
    } else {
      declaredValue = { amount: Number(normalized), currency: form.declaredCurrency };
    }
  }

  if (Object.keys(errors).length > 0) return { body: null, errors };
  return {
    body: {
      serviceLevel: form.serviceLevel,
      externalReference: optional(form.externalReference),
      originHubCode: form.originHubCode,
      destinationHubCode: form.destinationHubCode,
      sender: toParty(form.sender),
      recipient: toParty(form.recipient),
      packages,
      declaredValue,
    },
    errors,
  };
}

/** Sunucu anahtarları iki biçimde gelir: `sender.phone` (kural) ve `$.sender.phone` (JSON ayrıştırma). */
function normalizeServerErrors(errors: Errors): Errors {
  const result: Errors = {};
  for (const [key, messages] of Object.entries(errors)) {
    result[key.startsWith("$.") ? key.slice(2) : key] = messages;
  }
  return result;
}

type SubmitState =
  | { kind: "idle" }
  | { kind: "submitting" }
  | { kind: "unconfirmed"; reason: "network" | "in-progress" | "commit-unknown" }
  | { kind: "failed"; error: unknown };

function PartyFields({
  role,
  legend,
  value,
  errors,
  onChange,
}: {
  role: "sender" | "recipient";
  legend: string;
  value: PartyForm;
  errors: Errors;
  onChange: (next: PartyForm) => void;
}) {
  const set = (key: keyof PartyForm) => (event: { target: { value: string } }) => onChange({ ...value, [key]: event.target.value });
  return (
    <fieldset>
      <legend>{legend}</legend>
      <div className="form-grid">
        <TextField label="Ad soyad" name={`${role}.fullName`} required maxLength={150} autoComplete="off" value={value.fullName} errors={errors[`${role}.fullName`]} onChange={set("fullName")} />
        <TextField label="Firma (isteğe bağlı)" name={`${role}.companyName`} maxLength={150} autoComplete="off" value={value.companyName} errors={errors[`${role}.companyName`]} onChange={set("companyName")} />
        <TextField
          label="Telefon"
          name={`${role}.phone`}
          type="tel"
          required
          inputMode="tel"
          autoComplete="off"
          hint="Uluslararası biçim, ör. +905321234567"
          value={value.phone}
          errors={errors[`${role}.phone`]}
          onChange={set("phone")}
        />
        <TextField label="E-posta (isteğe bağlı)" name={`${role}.email`} type="email" maxLength={254} autoComplete="off" value={value.email} errors={errors[`${role}.email`]} onChange={set("email")} />
        <TextField label="Adres satırı 1" name={`${role}.address.line1`} required maxLength={200} autoComplete="off" value={value.line1} errors={errors[`${role}.address.line1`]} onChange={set("line1")} />
        <TextField label="Adres satırı 2 (isteğe bağlı)" name={`${role}.address.line2`} maxLength={200} autoComplete="off" value={value.line2} errors={errors[`${role}.address.line2`]} onChange={set("line2")} />
        <TextField label="İlçe (isteğe bağlı)" name={`${role}.address.district`} maxLength={100} autoComplete="off" value={value.district} errors={errors[`${role}.address.district`]} onChange={set("district")} />
        <TextField label="Şehir" name={`${role}.address.city`} required maxLength={100} autoComplete="off" value={value.city} errors={errors[`${role}.address.city`]} onChange={set("city")} />
        <TextField
          label="Posta kodu"
          name={`${role}.address.postalCode`}
          required
          inputMode="numeric"
          maxLength={5}
          autoComplete="off"
          hint="5 hane"
          value={value.postalCode}
          errors={errors[`${role}.address.postalCode`]}
          onChange={set("postalCode")}
        />
      </div>
    </fieldset>
  );
}

export function CreateShipmentPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const hubs = useHubs();
  // İşlem anahtarı durumu bileşen ömrü boyunca tek örnektir.
  const [operation] = useState(() => createIdempotentOperation());
  const [form, setForm] = useState<FormState>({
    serviceLevel: "Standard",
    externalReference: "",
    originHubCode: "",
    destinationHubCode: "",
    sender: emptyParty,
    recipient: emptyParty,
    packages: [emptyPackage],
    declaredAmount: "",
    declaredCurrency: "TRY",
  });
  const [errors, setErrors] = useState<Errors>({});
  const [submit, setSubmit] = useState<SubmitState>({ kind: "idle" });
  const activeHubs = (hubs.data ?? []).filter((hub) => hub.isActive);

  const draft = buildCreateRequest(form);
  // Yanıtı alınamayan denemeden sonra alanlar değiştiyse bu YENİ bir işlemdir (yeni Idempotency-Key).
  const changedAfterUnconfirmed = submit.kind === "unconfirmed" && draft.body !== null && operation.differsFromUnconfirmed(draft.body);

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    if (submit.kind === "submitting") return;
    if (!draft.body) {
      setErrors(draft.errors);
      setSubmit({ kind: "idle" });
      return;
    }
    setErrors({});
    const { key } = operation.keyFor(draft.body);
    setSubmit({ kind: "submitting" });
    try {
      const result = await shipmentsApi.create(draft.body, key);
      operation.complete();
      void queryClient.invalidateQueries({ queryKey: queryKeys.shipmentsRoot });
      void queryClient.invalidateQueries({ queryKey: queryKeys.summaryRoot });
      void navigate(`/shipments/${result.data.id}`, { state: { created: true, replayed: result.replayed } });
    } catch (error) {
      if (error instanceof NetworkError) {
        operation.markUnconfirmed();
        setSubmit({ kind: "unconfirmed", reason: "network" });
      } else if (error instanceof ApiError && error.code === "idempotency-request-in-progress") {
        operation.markUnconfirmed();
        setSubmit({ kind: "unconfirmed", reason: "in-progress" });
      } else if (error instanceof ApiError && error.code === "commit-outcome-unknown") {
        operation.markUnconfirmed();
        setSubmit({ kind: "unconfirmed", reason: "commit-unknown" });
      } else if (error instanceof ApiError && error.code === "validation") {
        // Doğrulama hatası saklanmaz; düzeltilen gövde yeni işlem anahtarı alır.
        operation.complete();
        setErrors(normalizeServerErrors(error.fieldErrors));
        setSubmit({ kind: "idle" });
      } else {
        if (error instanceof ApiError && error.status < 500) operation.complete();
        setSubmit({ kind: "failed", error });
      }
    }
  };

  const setPackage = (index: number, key: keyof PackageForm, value: string) =>
    setForm((current) => ({ ...current, packages: current.packages.map((pkg, i) => (i === index ? { ...pkg, [key]: value } : pkg)) }));

  const errorCount = Object.keys(errors).length;
  const hubProblem =
    submit.kind === "failed" && submit.error instanceof ApiError && (submit.error.code === "unknown-hub" || submit.error.code === "hub-inactive")
      ? submit.error
      : null;

  return (
    <>
      <div className="page-head">
        <h1>Gönderi oluştur</h1>
        <Link to="/shipments">← Gönderiler</Link>
      </div>

      {hubs.isError && <ErrorNotice error={hubs.error} subject="Hub listesi" onRetry={() => void hubs.refetch()} />}

      <form onSubmit={(event) => void onSubmit(event)} noValidate aria-label="Gönderi oluşturma formu">
        <fieldset>
          <legend>Gönderi</legend>
          <div className="form-grid">
            <SelectField label="Servis" name="serviceLevel" value={form.serviceLevel} errors={errors.serviceLevel} onChange={(event) => setForm({ ...form, serviceLevel: event.target.value as ServiceLevel })}>
              {(Object.keys(serviceLevelLabels) as ServiceLevel[]).map((level) => (
                <option key={level} value={level}>
                  {serviceLevelLabels[level]}
                </option>
              ))}
            </SelectField>
            <SelectField label="Çıkış hub'ı" name="originHubCode" required value={form.originHubCode} errors={errors.originHubCode} onChange={(event) => setForm({ ...form, originHubCode: event.target.value })}>
              <option value="">Seçin…</option>
              {activeHubs.map((hub) => (
                <option key={hub.code} value={hub.code}>
                  {hub.code} · {hub.name}
                </option>
              ))}
            </SelectField>
            <SelectField label="Varış hub'ı" name="destinationHubCode" required value={form.destinationHubCode} errors={errors.destinationHubCode} onChange={(event) => setForm({ ...form, destinationHubCode: event.target.value })}>
              <option value="">Seçin…</option>
              {activeHubs.map((hub) => (
                <option key={hub.code} value={hub.code}>
                  {hub.code} · {hub.name}
                </option>
              ))}
            </SelectField>
            <TextField
              label="Sipariş referansı (isteğe bağlı)"
              name="externalReference"
              maxLength={64}
              autoComplete="off"
              value={form.externalReference}
              errors={errors.externalReference}
              onChange={(event) => setForm({ ...form, externalReference: event.target.value })}
            />
          </div>
        </fieldset>

        <PartyFields role="sender" legend="Gönderen" value={form.sender} errors={errors} onChange={(sender) => setForm({ ...form, sender })} />
        <PartyFields role="recipient" legend="Alıcı" value={form.recipient} errors={errors} onChange={(recipient) => setForm({ ...form, recipient })} />

        <fieldset>
          <legend>Koliler ({form.packages.length})</legend>
          {errors.packages && (
            <p className="field-error" role="alert">
              {errors.packages.join(" ")}
            </p>
          )}
          {form.packages.map((pkg, index) => (
            <div className="pkg-row" key={index} role="group" aria-label={`Koli ${index + 1}`}>
              <TextField label={`Koli ${index + 1} ağırlık (g)`} name={`packages[${index}].weightGrams`} inputMode="numeric" required value={pkg.weightGrams} errors={errors[`packages[${index}].weightGrams`]} onChange={(event) => setPackage(index, "weightGrams", event.target.value)} />
              <TextField label="Boy (mm)" name={`packages[${index}].lengthMm`} inputMode="numeric" required value={pkg.lengthMm} errors={errors[`packages[${index}].lengthMm`]} onChange={(event) => setPackage(index, "lengthMm", event.target.value)} />
              <TextField label="En (mm)" name={`packages[${index}].widthMm`} inputMode="numeric" required value={pkg.widthMm} errors={errors[`packages[${index}].widthMm`]} onChange={(event) => setPackage(index, "widthMm", event.target.value)} />
              <TextField label="Yükseklik (mm)" name={`packages[${index}].heightMm`} inputMode="numeric" required value={pkg.heightMm} errors={errors[`packages[${index}].heightMm`]} onChange={(event) => setPackage(index, "heightMm", event.target.value)} />
              <TextField label="İçerik (isteğe bağlı)" name={`packages[${index}].contentDescription`} maxLength={200} autoComplete="off" value={pkg.contentDescription} errors={errors[`packages[${index}].contentDescription`]} onChange={(event) => setPackage(index, "contentDescription", event.target.value)} />
              <button
                type="button"
                className="btn"
                disabled={form.packages.length === 1}
                aria-label={`Koli ${index + 1} satırını kaldır`}
                onClick={() => setForm((current) => ({ ...current, packages: current.packages.filter((_, i) => i !== index) }))}
              >
                Kaldır
              </button>
            </div>
          ))}
          <button
            type="button"
            className="btn"
            disabled={form.packages.length >= maxPackages}
            onClick={() => setForm((current) => ({ ...current, packages: [...current.packages, emptyPackage] }))}
          >
            + Koli ekle
          </button>
          <span className="hint muted"> En fazla {maxPackages} koli.</span>
        </fieldset>

        <fieldset>
          <legend>Beyan edilen değer (isteğe bağlı)</legend>
          <div className="form-grid">
            <TextField label="Tutar" name="declaredValue.amount" inputMode="decimal" autoComplete="off" value={form.declaredAmount} errors={errors["declaredValue.amount"]} onChange={(event) => setForm({ ...form, declaredAmount: event.target.value })} />
            <SelectField label="Para birimi" name="declaredValue.currency" value={form.declaredCurrency} errors={errors["declaredValue.currency"]} onChange={(event) => setForm({ ...form, declaredCurrency: event.target.value })}>
              <option value="TRY">TRY</option>
              <option value="EUR">EUR</option>
              <option value="USD">USD</option>
            </SelectField>
          </div>
        </fieldset>

        {errorCount > 0 && (
          <Notice tone="critical" role="alert">
            <p>{errorCount} alan düzeltilmeli. Hatalar ilgili alanların altında gösteriliyor.</p>
          </Notice>
        )}

        {submit.kind === "unconfirmed" && (
          <Notice tone="warn" role="alert" icon="plug">
            <p>
              {submit.reason === "in-progress"
                ? "Önceki istek hâlâ işleniyor. Birkaç saniye sonra yeniden deneyin."
                : "Yanıt alınamadı. İstek sunucuya ulaşmış olabilir."}{" "}
              Aynı bilgilerle yeniden denemek güvenlidir: aynı işlem anahtarı kullanılır ve ikinci bir gönderi oluşmaz.
            </p>
            {changedAfterUnconfirmed && (
              <p>
                <strong>Alanları değiştirdiniz.</strong> Bu artık yeni bir işlem sayılır; önceki istek tamamlandıysa ikinci bir gönderi oluşabilir. Göndermeden önce{" "}
                <Link to="/shipments" target="_blank" rel="noreferrer">
                  listeden kontrol edin
                </Link>
                .
              </p>
            )}
          </Notice>
        )}

        {hubProblem && (
          <Notice tone="critical" role="alert">
            <p>
              {hubProblem.code === "hub-inactive" ? "Seçilen hub aktif değil" : "Seçilen hub bulunamadı"}
              {Array.isArray(hubProblem.problem.hubCodes) ? `: ${(hubProblem.problem.hubCodes as string[]).join(", ")}` : ""}. Hub seçimini güncelleyin.
            </p>
          </Notice>
        )}
        {submit.kind === "failed" && !hubProblem && <ErrorNotice error={submit.error} subject="Gönderi" verb="oluşturulamadı" />}

        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={submit.kind === "submitting"}>
            {submit.kind === "submitting"
              ? "Gönderiliyor…"
              : submit.kind === "unconfirmed"
                ? changedAfterUnconfirmed
                  ? "Yeni işlem olarak gönder"
                  : "Aynı işlemi yeniden dene"
                : "Gönderiyi oluştur"}
          </button>
          <Link className="btn" to="/shipments">
            Vazgeç
          </Link>
        </div>
      </form>
    </>
  );
}
