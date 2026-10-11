import { QueryClient } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RealtimeClient, invalidationTargets, type HubConnectionLike } from "./RealtimeClient";

class FakeConnection implements HubConnectionLike {
  readonly invocations: { method: string; args: unknown[] }[] = [];
  readonly handlers = new Map<string, (...args: never[]) => void>();
  startResult: () => Promise<void> = () => Promise.resolve();
  rejectInvoke: string[] = [];
  stopped = false;
  private reconnecting: (() => void) | null = null;
  private reconnected: (() => void) | null = null;
  private closed: (() => void) | null = null;

  start = () => this.startResult();
  stop = () => {
    this.stopped = true;
    return Promise.resolve();
  };
  invoke = (method: string, ...args: unknown[]) => {
    this.invocations.push({ method, args });
    return this.rejectInvoke.includes(method) ? Promise.reject(new Error("HubException: forbidden")) : Promise.resolve(undefined);
  };
  on(event: string, handler: (...args: never[]) => void) {
    this.handlers.set(event, handler);
  }
  onreconnecting(handler: () => void) {
    this.reconnecting = handler;
  }
  onreconnected(handler: () => void) {
    this.reconnected = handler;
  }
  onclose(handler: () => void) {
    this.closed = handler;
  }

  emit(event: string, payload: unknown) {
    (this.handlers.get(event) as ((payload: unknown) => void) | undefined)?.(payload);
  }
  dropAndReconnecting = () => this.reconnecting?.();
  comeBack = () => this.reconnected?.();
  close = () => this.closed?.();
  methods = () => this.invocations.map((call) => [call.method, ...call.args].join(" "));
}

function setup(subject: { isOps: boolean; hubCodes: string[] }) {
  const queryClient = new QueryClient();
  const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
  const connections: FakeConnection[] = [];
  const recheckSession = vi.fn();
  let prepare: (connection: FakeConnection) => void = () => undefined;
  const client = new RealtimeClient({
    createConnection: () => {
      const connection = new FakeConnection();
      prepare(connection);
      connections.push(connection);
      return connection;
    },
    queryClient,
    subject,
    recheckSession,
    debounceMs: 250,
    reconcileMs: 60_000,
    restartDelaysMs: [0, 2_000, 5_000],
  });
  const states: string[] = [];
  client.subscribe(() => states.push(client.getState()));
  const keys = () => invalidate.mock.calls.map((call) => JSON.stringify(call[0]?.queryKey ?? "ALL"));
  return { client, connections, invalidate, recheckSession, states, keys, onCreate: (fn: (connection: FakeConnection) => void) => (prepare = fn) };
}

describe("canlı bağlantı istemcisi", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("bağlanınca role göre abone olur, sonra tüm etkin sorguları sunucudan yeniden okutur (ilk eşitleme)", async () => {
    const ops = setup({ isOps: true, hubCodes: [] });
    ops.client.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(ops.connections[0]?.methods()).toEqual(["SubscribeOperations"]);
    expect(ops.client.getState()).toBe("connected");
    expect(ops.keys()).toEqual(['"ALL"']);

    const hub = setup({ isOps: false, hubCodes: ["IST-01", "ANK-02"] });
    hub.client.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(hub.connections[0]?.methods()).toEqual(["SubscribeHub IST-01", "SubscribeHub ANK-02"]);
    ops.client.stop();
    hub.client.stop();
  });

  it("art arda ve yinelenen ipuçlarını tek tazelemede birleştirir; yükteki veriyi kullanmaz, yalnız yeniden okutur", async () => {
    const { client, connections, invalidate, keys } = setup({ isOps: true, hubCodes: [] });
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    invalidate.mockClear();
    const connection = connections[0]!;

    connection.emit("ShipmentChanged", { shipmentId: "s1", status: "AtHub" });
    connection.emit("ShipmentChanged", { shipmentId: "s1", status: "AtHub" });
    connection.emit("ShipmentChanged", { shipmentId: "s2", status: "Delivered" });
    connection.emit("ExceptionChanged", { exceptionId: "e1", shipmentId: "s1", status: "Open" });
    connection.emit("ShipmentChanged", { status: "bozuk-yük" });
    expect(invalidate).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(250);
    expect(keys()).toEqual([
      '["shipments","list"]',
      '["summary"]',
      '["shipments","detail","s1"]',
      '["shipments","audit","s1"]',
      '["shipments","receipts","s1"]',
      '["shipments","detail","s2"]',
      '["shipments","audit","s2"]',
      '["shipments","receipts","s2"]',
      '["exceptions"]',
    ]);
    client.stop();
  });

  it("kopunca durumu bildirir ve oturumu yoklatır; yeniden bağlanınca abonelikleri yeniler ve yetkili eşitleme yapar", async () => {
    const { client, connections, invalidate, recheckSession, keys } = setup({ isOps: true, hubCodes: [] });
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    const release = client.watchShipment("s9");
    const connection = connections[0]!;
    expect(connection.methods()).toEqual(["SubscribeOperations", "SubscribeShipment s9"]);
    invalidate.mockClear();

    connection.dropAndReconnecting();
    expect(client.getState()).toBe("reconnecting");
    expect(recheckSession).toHaveBeenCalledTimes(1);
    // Kopukken mesaj gelmez; hiçbir tazeleme "canlı" diye sunulmaz.
    expect(invalidate).not.toHaveBeenCalled();

    connection.comeBack();
    await vi.advanceTimersByTimeAsync(0);
    expect(client.getState()).toBe("connected");
    expect(connection.methods().slice(2)).toEqual(["SubscribeOperations", "SubscribeShipment s9"]);
    expect(keys()).toEqual(['"ALL"']);

    release();
    expect(connection.methods().at(-1)).toBe("UnsubscribeShipment s9");
    client.stop();
  });

  it("sunucu bağlantıyı kapatırsa veya bağlantı kurulamazsa geri çekilerek yeniden dener ve oturumu yoklatır", async () => {
    const { client, connections, recheckSession, states, onCreate } = setup({ isOps: true, hubCodes: [] });
    client.start();
    await vi.advanceTimersByTimeAsync(0);

    // Sonraki iki bağlantı denemesi başarısız (ör. negotiate 401 veya ağ yok).
    let failures = 2;
    onCreate((connection) => {
      if (failures-- > 0) connection.startResult = () => Promise.reject(new Error("negotiate failed"));
    });
    connections[0]!.close();
    expect(client.getState()).toBe("disconnected");
    expect(recheckSession).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(0); // 1. yeniden deneme (0 ms) → başarısız
    expect(connections).toHaveLength(2);
    expect(client.getState()).toBe("disconnected");
    await vi.advanceTimersByTimeAsync(1_999);
    expect(connections).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1); // 2. deneme (2 sn) → başarısız
    expect(connections).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(5_000); // 3. deneme (5 sn) → başarılı
    expect(connections).toHaveLength(4);
    expect(client.getState()).toBe("connected");
    expect(recheckSession).toHaveBeenCalledTimes(3);
    expect(states).toEqual(["connected", "disconnected", "reconnecting", "disconnected", "reconnecting", "disconnected", "reconnecting", "connected"]);
    client.stop();
  });

  it("reddedilen abonelik bağlantıyı düşürmez; iptal bildirimi oturumu ve verileri yeniden okutur", async () => {
    const { client, connections, invalidate, recheckSession, keys, onCreate } = setup({ isOps: false, hubCodes: ["IST-01"] });
    onCreate((connection) => (connection.rejectInvoke = ["SubscribeHub"]));
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(client.getState()).toBe("connected");
    invalidate.mockClear();

    connections[0]!.emit("SubscriptionRevoked", { kind: "Hub", key: "IST-01" });
    expect(recheckSession).toHaveBeenCalledTimes(1);
    expect(keys()).toEqual(['"ALL"']);
    client.stop();
  });

  it("periyodik mutabakat bağlantı durumundan bağımsız çalışır; durdurulunca bağlantı kapanır ve yeniden deneme olmaz", async () => {
    const { client, connections, invalidate } = setup({ isOps: true, hubCodes: [] });
    client.start();
    await vi.advanceTimersByTimeAsync(0);
    invalidate.mockClear();

    await vi.advanceTimersByTimeAsync(60_000);
    expect(invalidate).toHaveBeenCalledTimes(1);

    client.stop();
    expect(connections[0]!.stopped).toBe(true);
    connections[0]!.close();
    await vi.advanceTimersByTimeAsync(120_000);
    expect(connections).toHaveLength(1);
    expect(invalidate).toHaveBeenCalledTimes(1);
  });

  it("ipucu yokken hiçbir sorguyu hedeflemez", () => {
    expect(invalidationTargets({ shipmentIds: new Set(), exceptions: false })).toEqual([]);
    expect(invalidationTargets({ shipmentIds: new Set(), exceptions: true }).map((key) => key.join("/"))).toEqual(["shipments/list", "summary", "exceptions"]);
  });
});
