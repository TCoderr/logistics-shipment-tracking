// Tüm zaman gösterimleri iş saat dilimiyle (Europe/Istanbul) yapılır; API UTC döner.
const zone = "Europe/Istanbul";
const locale = "tr-TR";

const dateTime = new Intl.DateTimeFormat(locale, {
  timeZone: zone,
  day: "2-digit",
  month: "short",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});
const dateTimeFull = new Intl.DateTimeFormat(locale, {
  timeZone: zone,
  day: "2-digit",
  month: "long",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});
const timeOnly = new Intl.DateTimeFormat(locale, { timeZone: zone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const dayShort = new Intl.DateTimeFormat(locale, { timeZone: "UTC", day: "2-digit", month: "short", weekday: "short" });
const dayLong = new Intl.DateTimeFormat(locale, { timeZone: "UTC", day: "numeric", month: "long", weekday: "long" });
const number = new Intl.NumberFormat(locale);
const percent = new Intl.NumberFormat(locale, { style: "percent", minimumFractionDigits: 1, maximumFractionDigits: 1 });

export function formatDateTime(iso: string): string {
  return dateTime.format(new Date(iso));
}

export function formatDateTimeFull(iso: string): string {
  return dateTimeFull.format(new Date(iso));
}

export function formatTime(value: string | Date): string {
  return timeOnly.format(typeof value === "string" ? new Date(value) : value);
}

/** `2026-10-12` biçimli iş tarihi (saat dilimsiz) → "12 Eki Pzt". */
export function formatBusinessDate(isoDate: string, long = false): string {
  const parsed = new Date(`${isoDate}T00:00:00Z`);
  return (long ? dayLong : dayShort).format(parsed);
}

export function formatNumber(value: number): string {
  return number.format(value);
}

/** Payda küçükse (<20) oran yanıltıcıdır: "7 / 9" gösterilir (docs/07 §6). */
export function formatRatio(numerator: number, denominator: number): { primary: string; secondary: string | null } {
  if (denominator === 0) return { primary: "—", secondary: "veri yok" };
  if (denominator < 20) return { primary: `${formatNumber(numerator)} / ${formatNumber(denominator)}`, secondary: null };
  return {
    primary: percent.format(numerator / denominator),
    secondary: `${formatNumber(numerator)} / ${formatNumber(denominator)}`,
  };
}

/** Bugünün iş tarihi (Europe/Istanbul), `YYYY-MM-DD`. */
export function businessToday(now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  const get = (type: string) => parts.find((part) => part.type === type)?.value ?? "";
  return `${get("year")}-${get("month")}-${get("day")}`;
}

export function daysBetween(fromIsoDate: string, toIsoDate: string): number {
  const from = Date.parse(`${fromIsoDate}T00:00:00Z`);
  const to = Date.parse(`${toIsoDate}T00:00:00Z`);
  return Math.round((to - from) / 86_400_000);
}

/** Vadeye kalan/geçen süre: "2 sa geçti", "1 sa 40 dk", "3 gün". */
export function formatDuration(milliseconds: number): string {
  const totalMinutes = Math.max(0, Math.round(Math.abs(milliseconds) / 60_000));
  if (totalMinutes < 60) return `${totalMinutes} dk`;
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours < 24) return minutes === 0 ? `${hours} sa` : `${hours} sa ${minutes} dk`;
  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours === 0 ? `${days} gün` : `${days} gün ${restHours} sa`;
}

export function formatWeight(grams: number): string {
  return grams >= 1000 ? `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(grams / 1000)} kg` : `${grams} g`;
}

/** Yerel `datetime-local` girdi değeri (tarayıcı saat dilimi) → ofsetli ISO-8601 (API ofsetsiz zamanı reddeder). */
export function localInputToOffsetIso(value: string): string | null {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

export function nowAsLocalInput(now: Date = new Date()): string {
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`;
}
