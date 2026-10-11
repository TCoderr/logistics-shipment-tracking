import type { QueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";

// Canlı kanal yalnız "bir şey değişti" ipucu taşır; veri her zaman HTTP ile yeniden okunur (DB otoritedir).
// - Mesaj kaybı varsayılır: (yeniden) bağlanınca tüm etkin sorgular tazelenir; ayrıca periyodik mutabakat vardır.
// - Sıra veya "kaçırılanları geri getirme" garantisi yoktur; bu yüzden istemci bir akış konumu tutmaz.

export type ConnectionState = "connecting" | "connected" | "reconnecting" | "disconnected";

export interface ShipmentChangedMessage {
  shipmentId: string;
  status: string;
}

export interface ExceptionChangedMessage {
  exceptionId: string;
  shipmentId: string;
  status: string;
}

/** @microsoft/signalr HubConnection'ın kullanılan alt kümesi (testte sahte bağlantı verilebilsin). */
export interface HubConnectionLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(event: string, handler: (...args: never[]) => void): void;
  onreconnecting(handler: () => void): void;
  onreconnected(handler: () => void): void;
  onclose(handler: () => void): void;
}

export interface RealtimeSubject {
  isOps: boolean;
  hubCodes: readonly string[];
}

export interface RealtimeClientOptions {
  createConnection: () => HubConnectionLike;
  queryClient: QueryClient;
  subject: RealtimeSubject;
  /** Oturumun hâlâ geçerli olup olmadığını yeniden yoklar (bağlantı reddedilirse veya abonelik iptal edilirse). */
  recheckSession: () => void;
  debounceMs?: number;
  reconcileMs?: number;
  restartDelaysMs?: readonly number[];
}

export const defaultRestartDelaysMs = [0, 2_000, 5_000, 10_000, 15_000] as const;

interface PendingInvalidation {
  shipmentIds: Set<string>;
  exceptions: boolean;
}

/** Bekleyen ipuçlarından tazelenecek sorgu anahtarları (saf fonksiyon). */
export function invalidationTargets(pending: PendingInvalidation): readonly (readonly unknown[])[] {
  if (pending.shipmentIds.size === 0 && !pending.exceptions) return [];
  const targets: (readonly unknown[])[] = [[...queryKeys.shipmentsRoot, "list"], queryKeys.summaryRoot];
  for (const id of pending.shipmentIds) {
    targets.push(queryKeys.shipment(id), queryKeys.shipmentAudit(id), queryKeys.shipmentReceipts(id));
  }
  if (pending.exceptions) targets.push(queryKeys.exceptionsRoot);
  return targets;
}

export class RealtimeClient {
  private readonly options: Required<Omit<RealtimeClientOptions, "createConnection" | "queryClient" | "subject" | "recheckSession">> & RealtimeClientOptions;
  private connection: HubConnectionLike | null = null;
  private state: ConnectionState = "connecting";
  private readonly listeners = new Set<() => void>();
  private readonly shipmentSubscriptions = new Map<string, number>();
  private pending: PendingInvalidation = { shipmentIds: new Set(), exceptions: false };
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private reconcileTimer: ReturnType<typeof setInterval> | null = null;
  private restartAttempt = 0;
  private stopped = false;

  constructor(options: RealtimeClientOptions) {
    this.options = { debounceMs: 250, reconcileMs: 60_000, restartDelaysMs: defaultRestartDelaysMs, ...options };
  }

  getState = (): ConnectionState => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  start(): void {
    this.stopped = false;
    // Periyodik mutabakat: gözlenmeyen değişiklikleri (kaçan mesaj, durum değiştirmeyen güncelleme) telafi eder.
    // Bu bir yoklamadır; canlı kanalın çalıştığı anlamına gelmez ve arayüzde ayrı gösterilir.
    this.reconcileTimer = setInterval(() => void this.options.queryClient.invalidateQueries(), this.options.reconcileMs);
    void this.connect();
  }

  stop(): void {
    this.stopped = true;
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    if (this.restartTimer) clearTimeout(this.restartTimer);
    if (this.reconcileTimer) clearInterval(this.reconcileTimer);
    this.debounceTimer = this.restartTimer = this.reconcileTimer = null;
    const connection = this.connection;
    this.connection = null;
    void connection?.stop().catch(() => undefined);
  }

  /** Kullanıcı isteğiyle hemen yeniden bağlanmayı dener. */
  reconnectNow(): void {
    if (this.stopped || this.state === "connected" || this.state === "connecting") return;
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartAttempt = 0;
    void this.connect();
  }

  /** Bir gönderinin ipuçlarına abone olur; dönen fonksiyon aboneliği bırakır. Sunucu kapsamı ayrıca doğrular. */
  watchShipment(shipmentId: string): () => void {
    const count = this.shipmentSubscriptions.get(shipmentId) ?? 0;
    this.shipmentSubscriptions.set(shipmentId, count + 1);
    if (count === 0 && this.state === "connected") void this.invokeQuietly("SubscribeShipment", shipmentId);
    return () => {
      const remaining = (this.shipmentSubscriptions.get(shipmentId) ?? 1) - 1;
      if (remaining > 0) {
        this.shipmentSubscriptions.set(shipmentId, remaining);
        return;
      }
      this.shipmentSubscriptions.delete(shipmentId);
      if (this.state === "connected") void this.invokeQuietly("UnsubscribeShipment", shipmentId);
    };
  }

  private setState(next: ConnectionState): void {
    if (this.state === next) return;
    this.state = next;
    for (const listener of this.listeners) listener();
  }

  private async connect(): Promise<void> {
    if (this.stopped) return;
    this.setState(this.restartAttempt === 0 && this.connection === null ? "connecting" : "reconnecting");
    const connection = this.options.createConnection();
    this.connection = connection;

    connection.on("ShipmentChanged", (message: ShipmentChangedMessage) => this.onShipmentChanged(message));
    connection.on("ExceptionChanged", (message: ExceptionChangedMessage) => this.onExceptionChanged(message));
    connection.on("SubscriptionRevoked", () => this.onRevoked());
    connection.onreconnecting(() => {
      if (this.connection !== connection) return;
      this.setState("reconnecting");
      // Kopma sunucu kaynaklı olabilir (çıkış, bilet süresi): oturum hemen HTTP ile yoklanır.
      this.options.recheckSession();
    });
    connection.onreconnected(() => {
      if (this.connection === connection) void this.onConnected(connection);
    });
    connection.onclose(() => {
      if (this.connection !== connection || this.stopped) return;
      // Sunucu bağlantıyı kapattı (çıkış, bilet süresi, yetki iptali) veya otomatik yeniden bağlanma tükendi.
      this.setState("disconnected");
      this.options.recheckSession();
      this.scheduleRestart();
    });

    try {
      await connection.start();
    } catch {
      if (this.connection !== connection || this.stopped) return;
      // Bağlantı kurulamadı: ağ sorunu olabilir ya da oturum geçersizdir (401). Oturum HTTP ile yeniden yoklanır.
      this.setState("disconnected");
      this.options.recheckSession();
      this.scheduleRestart();
      return;
    }
    if (this.connection !== connection || this.stopped) {
      void connection.stop().catch(() => undefined);
      return;
    }
    await this.onConnected(connection);
  }

  private scheduleRestart(): void {
    if (this.stopped) return;
    const delays = this.options.restartDelaysMs;
    const delay = delays[Math.min(this.restartAttempt, delays.length - 1)] ?? 15_000;
    this.restartAttempt += 1;
    if (this.restartTimer) clearTimeout(this.restartTimer);
    this.restartTimer = setTimeout(() => void this.connect(), delay);
  }

  private async onConnected(connection: HubConnectionLike): Promise<void> {
    // Abonelikler sunucuda yetkilendirilir; reddedilen abonelik sessizce atlanır (arayüz zaten o veriyi göstermez).
    if (this.options.subject.isOps) await this.invokeQuietly("SubscribeOperations");
    for (const hubCode of this.options.subject.hubCodes) await this.invokeQuietly("SubscribeHub", hubCode);
    for (const shipmentId of this.shipmentSubscriptions.keys()) await this.invokeQuietly("SubscribeShipment", shipmentId);
    if (this.connection !== connection || this.stopped) return;
    this.restartAttempt = 0;
    this.setState("connected");
    // Yetkili yeniden eşitleme: kopukluk sırasında (veya abonelikten önce) olan değişiklikler mesaj olarak GELMEZ;
    // bu yüzden etkin tüm sorgular sunucudan yeniden okunur.
    void this.options.queryClient.invalidateQueries();
  }

  private async invokeQuietly(method: string, ...args: unknown[]): Promise<void> {
    try {
      await this.connection?.invoke(method, ...args);
    } catch {
      // Yetkisiz/bulunamayan abonelik veya o anda kopmuş bağlantı: yeniden bağlanınca tekrar denenir.
    }
  }

  private onShipmentChanged(message: ShipmentChangedMessage): void {
    if (typeof message?.shipmentId !== "string") return;
    this.pending.shipmentIds.add(message.shipmentId);
    this.scheduleFlush();
  }

  private onExceptionChanged(message: ExceptionChangedMessage): void {
    if (typeof message?.shipmentId !== "string") return;
    this.pending.shipmentIds.add(message.shipmentId);
    this.pending.exceptions = true;
    this.scheduleFlush();
  }

  private onRevoked(): void {
    // Yetki değişti: oturum (roller/hub'lar) ve tüm veriler sunucudan yeniden okunur.
    this.options.recheckSession();
    void this.options.queryClient.invalidateQueries();
  }

  /** Art arda gelen (ve yinelenen) ipuçları tek tazelemede birleştirilir. */
  private scheduleFlush(): void {
    if (this.debounceTimer) return;
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = null;
      const pending = this.pending;
      this.pending = { shipmentIds: new Set(), exceptions: false };
      for (const queryKey of invalidationTargets(pending)) {
        void this.options.queryClient.invalidateQueries({ queryKey });
      }
    }, this.options.debounceMs);
  }
}
