import { describe, expect, it } from "vitest";
import { createIdempotentOperation } from "./idempotency";
import { businessToday, formatDuration, formatRatio, localInputToOffsetIso } from "./format";

function sequence() {
  let next = 0;
  return () => `key-${++next}`;
}

describe("istemci idempotency anahtar politikası", () => {
  it("aynı gövde yeniden gönderilirken aynı anahtarı kullanır (yanıt kaybı güvenli)", () => {
    const operation = createIdempotentOperation(sequence());
    const body = { recipient: "A", packages: [{ weightGrams: 100 }] };

    const first = operation.keyFor(body);
    operation.markUnconfirmed();
    const retry = operation.keyFor({ recipient: "A", packages: [{ weightGrams: 100 }] });

    expect(first).toEqual({ key: "key-1", isRetry: false });
    expect(retry).toEqual({ key: "key-1", isRetry: true });
    expect(operation.hasUnconfirmedAttempt()).toBe(true);
  });

  it("gövde değişince yeni işlem sayar ve yeni anahtar üretir (eski anahtar farklı gövdeyle gönderilmez)", () => {
    const operation = createIdempotentOperation(sequence());
    operation.keyFor({ city: "Ankara" });
    operation.markUnconfirmed();

    expect(operation.differsFromUnconfirmed({ city: "Ankara" })).toBe(false);
    expect(operation.differsFromUnconfirmed({ city: "İzmir" })).toBe(true);

    const changed = operation.keyFor({ city: "İzmir" });
    expect(changed).toEqual({ key: "key-2", isRetry: false });
    expect(operation.hasUnconfirmedAttempt()).toBe(false);
  });

  it("işlem tamamlanınca anahtarı bırakır; aynı gövde artık yeni işlemdir", () => {
    const operation = createIdempotentOperation(sequence());
    operation.keyFor({ a: 1 });
    operation.complete();

    expect(operation.keyFor({ a: 1 })).toEqual({ key: "key-2", isRetry: false });
  });
});

describe("biçimlendirme", () => {
  it("küçük paydada oran yerine pay/payda gösterir, payda yoksa veri yok der", () => {
    expect(formatRatio(7, 9)).toEqual({ primary: "7 / 9", secondary: null });
    expect(formatRatio(0, 0)).toEqual({ primary: "—", secondary: "veri yok" });
    const large = formatRatio(1412, 1538);
    expect(large.primary).toMatch(/91,8/);
    expect(large.secondary).toBe("1.412 / 1.538");
  });

  it("iş gününü tarayıcı saat diliminden bağımsız olarak Europe/Istanbul'a göre hesaplar", () => {
    // 21:30 UTC = ertesi gün 00:30 İstanbul.
    expect(businessToday(new Date("2026-10-11T21:30:00Z"))).toBe("2026-10-12");
    expect(businessToday(new Date("2026-10-11T20:59:00Z"))).toBe("2026-10-11");
  });

  it("süreleri dakika/saat/gün olarak yazar", () => {
    expect(formatDuration(40 * 60_000)).toBe("40 dk");
    expect(formatDuration(100 * 60_000)).toBe("1 sa 40 dk");
    expect(formatDuration(62 * 3_600_000)).toBe("2 gün 14 sa");
  });

  it("yerel tarih-saat girdisini ofsetli ISO'ya çevirir, geçersiz girdide null döner", () => {
    expect(localInputToOffsetIso("2026-10-10T12:47")).toMatch(/^2026-10-10T\d{2}:47:00\.000Z$/);
    expect(localInputToOffsetIso("")).toBeNull();
  });
});
