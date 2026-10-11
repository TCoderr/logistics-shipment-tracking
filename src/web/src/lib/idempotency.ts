// İstemci tarafı Idempotency-Key politikası (docs/05 §3):
// - Bir "işlem" = belirli bir istek gövdesi. Aynı gövde yeniden gönderilirse (yanıt kaybı, zaman aşımı, 409 in-progress,
//   503 commit-outcome-unknown) AYNI anahtar kullanılır; sunucu ilk yanıtı aynen döner, ikinci kayıt oluşmaz.
// - Gövde değişirse bu YENİ bir işlemdir ve yeni anahtar üretilir. Eski anahtar farklı gövdeyle gönderilseydi sunucu
//   `422 idempotency-key-reused` dönerdi.
// - İşlem başarıyla sonuçlanınca anahtar bırakılır.

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

export interface IdempotentOperation {
  /** Verilen gövde için kullanılacak anahtar; gövde öncekinden farklıysa yeni anahtar üretilir. */
  keyFor(payload: unknown): { key: string; isRetry: boolean };
  /** Sonucu bilinmeyen (yanıtı alınamayan) bir deneme var mı? */
  hasUnconfirmedAttempt(): boolean;
  /** Verilen gövde, sonucu bilinmeyen denemeden farklı mı? */
  differsFromUnconfirmed(payload: unknown): boolean;
  markUnconfirmed(): void;
  complete(): void;
}

export function createIdempotentOperation(generate: () => string = newIdempotencyKey): IdempotentOperation {
  let current: { key: string; fingerprint: string; unconfirmed: boolean } | null = null;

  return {
    keyFor(payload) {
      const fingerprint = JSON.stringify(payload);
      if (current && current.fingerprint === fingerprint) {
        return { key: current.key, isRetry: true };
      }
      current = { key: generate(), fingerprint, unconfirmed: false };
      return { key: current.key, isRetry: false };
    },
    hasUnconfirmedAttempt() {
      return current?.unconfirmed ?? false;
    },
    differsFromUnconfirmed(payload) {
      return current !== null && current.unconfirmed && current.fingerprint !== JSON.stringify(payload);
    },
    markUnconfirmed() {
      if (current) current.unconfirmed = true;
    },
    complete() {
      current = null;
    },
  };
}
