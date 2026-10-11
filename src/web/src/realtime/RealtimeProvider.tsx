import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useContext, useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { sessionQueryKey, type SessionUser } from "../auth/AuthContext";
import { RealtimeClient, defaultRestartDelaysMs, type ConnectionState, type HubConnectionLike } from "./RealtimeClient";

const RealtimeContext = createContext<RealtimeClient | null>(null);

function createHubConnection(): HubConnectionLike {
  return (
    new HubConnectionBuilder()
      // Aynı origin: oturum cookie'si tarayıcı tarafından eklenir; URL'de veya depoda token taşınmaz.
      .withUrl("/realtime/operations")
      // Kısa kopmalarda kütüphane kendisi yeniden bağlanır; tükenirse RealtimeClient yeniden başlatır.
      .withAutomaticReconnect([...defaultRestartDelaysMs])
      // Kütüphane günlükleri bağlantı kimliği içeren URL'ler yazabilir: console'a hiçbir şey yazılmaz.
      .configureLogging(LogLevel.None)
      .build()
  );
}

/** Oturumlu kullanıcı için canlı bağlantıyı kurar; çıkışta/bileşen kalkınca kapatır. */
export function RealtimeProvider({ user, children }: { user: SessionUser; children: ReactNode }) {
  const queryClient = useQueryClient();
  const [client] = useState(
    () =>
      new RealtimeClient({
        createConnection: createHubConnection,
        queryClient,
        subject: { isOps: user.isOps, hubCodes: user.hubCodes },
        recheckSession: () => void queryClient.invalidateQueries({ queryKey: sessionQueryKey }),
      }),
  );

  useEffect(() => {
    client.start();
    return () => client.stop();
  }, [client]);

  // Kullanıcı veya yetkisi (rol/hub) değişirse sağlayıcı `key` ile yeniden kurulur (bkz. App).
  return <RealtimeContext.Provider value={client}>{children}</RealtimeContext.Provider>;
}

/** Bağlantı durumu; sağlayıcı yoksa (testler, public sayfa) `null`. */
export function useConnectionState(): { state: ConnectionState; reconnect: () => void } | null {
  const client = useContext(RealtimeContext);
  const state = useSyncExternalStore(
    (onChange) => client?.subscribe(onChange) ?? (() => undefined),
    () => client?.getState() ?? null,
  );
  return client && state ? { state, reconnect: () => client.reconnectNow() } : null;
}

/** Açık gönderi detayı için tek gönderi aboneliği (sunucu kapsamı doğrular). */
export function useShipmentWatch(shipmentId: string | null): void {
  const client = useContext(RealtimeContext);
  useEffect(() => {
    if (!client || !shipmentId) return;
    return client.watchShipment(shipmentId);
  }, [client, shipmentId]);
}
