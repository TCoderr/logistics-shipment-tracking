import { useIsFetching, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, useSyncExternalStore, type ReactNode } from "react";
import { NavLink, useNavigate } from "react-router-dom";
import { exceptionsApi } from "../api/endpoints";
import { useAuth, type SessionUser } from "../auth/AuthContext";
import { formatTime } from "../lib/format";
import { roleLabels } from "../lib/labels";
import { queryKeys } from "../lib/queryKeys";
import { useConnectionState } from "../realtime/RealtimeProvider";
import { useNow } from "../lib/useNow";
import { Icon } from "./Icon";

const staleAfterMs = 5 * 60_000;

/** Etkin sorgular arasında en son başarılı veri zamanı (yoksa 0). */
function useLastRefresh(): number {
  const queryClient = useQueryClient();
  const cache = queryClient.getQueryCache();
  return useSyncExternalStore(
    (onChange) => cache.subscribe(onChange),
    () =>
      cache
        .findAll({ type: "active" })
        .reduce((latest, query) => Math.max(latest, query.state.dataUpdatedAt), 0),
  );
}

function useTheme(): [string, () => void] {
  const [theme, setTheme] = useState<string>(() => {
    try {
      return window.localStorage.getItem("lst.theme") ?? "";
    } catch {
      return "";
    }
  });
  useEffect(() => {
    if (theme) document.documentElement.setAttribute("data-theme", theme);
    else document.documentElement.removeAttribute("data-theme");
  }, [theme]);
  const toggle = () => {
    const prefersDark = window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
    const current = theme || (prefersDark ? "dark" : "light");
    const next = current === "dark" ? "light" : "dark";
    try {
      window.localStorage.setItem("lst.theme", next);
    } catch {
      // Depolama kapalıysa tema yalnız bu oturumda geçerlidir.
    }
    setTheme(next);
  };
  return [theme, toggle];
}

const connectionTexts = {
  connecting: "Canlı bağlantı kuruluyor…",
  connected: "Canlı güncelleme açık",
  reconnecting: "Bağlantı koptu · yeniden bağlanılıyor… Görünen veri bayat olabilir",
  disconnected: "Canlı güncelleme kapalı · Görünen veri bayat olabilir",
} as const;

/**
 * Canlı bağlantı ve veri tazeliği şeridi. "Canlı güncelleme açık" yalnız WebSocket/SignalR bağlantısı kuruluyken gösterilir;
 * kopukken veri yalnız yoklama ve elle yenilemeyle tazelenir ve bu açıkça söylenir.
 */
function StatusBar() {
  const queryClient = useQueryClient();
  const connection = useConnectionState();
  const lastRefresh = useLastRefresh();
  const fetching = useIsFetching();
  const now = useNow(15_000);
  const live = connection?.state === "connected" || connection?.state === "connecting";
  const stale = !live || (lastRefresh > 0 && now - lastRefresh > staleAfterMs);

  return (
    <div className={`statusbar${stale ? " is-stale" : ""}`} role="status" aria-live="polite" data-connection={connection?.state ?? "off"}>
      <span className="conn">
        <Icon name={connection?.state === "connected" ? "dot" : "plug"} />
        {connection ? connectionTexts[connection.state] : "Canlı güncelleme kapalı"}
      </span>
      {connection?.state === "disconnected" && (
        <button type="button" className="btn" onClick={connection.reconnect}>
          Yeniden bağlan
        </button>
      )}
      <span>
        {lastRefresh > 0 ? (
          <>
            Son yenileme <time dateTime={new Date(lastRefresh).toISOString()}>{formatTime(new Date(lastRefresh))}</time>
            {stale && " · veri 5 dakikadan eski"}
          </>
        ) : (
          "Veri bekleniyor"
        )}
      </span>
      <button type="button" className="btn" onClick={() => void queryClient.invalidateQueries()} disabled={fetching > 0}>
        {fetching > 0 ? "Yenileniyor…" : "Yenile"}
      </button>
    </div>
  );
}

function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName);
}

/** `g s` gönderiler, `g e` istisnalar (yalnız odak bir metin alanında değilken). */
function useNavigationShortcuts(user: SessionUser) {
  const navigate = useNavigate();
  useEffect(() => {
    let pendingG = 0;
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey || isTypingTarget(event.target)) return;
      const now = Date.now();
      if (event.key === "g") {
        pendingG = now;
        return;
      }
      if (now - pendingG < 1200) {
        if (event.key === "s") void navigate("/shipments");
        if (event.key === "e" && user.isOps) void navigate("/exceptions");
      }
      pendingG = 0;
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [navigate, user.isOps]);
}

export function Layout({ user, children }: { user: SessionUser; children: ReactNode }) {
  const { logout } = useAuth();
  const [theme, toggleTheme] = useTheme();
  const [loggingOut, setLoggingOut] = useState(false);
  useNavigationShortcuts(user);

  // Sekmedeki sayaç: açık istisna sayısı gerçek kuyruktan (ilk sayfa); 50'den fazlaysa "50+".
  const openExceptions = useQuery({
    queryKey: [...queryKeys.exceptionsRoot, "badge"],
    queryFn: ({ signal }) => exceptionsApi.list({}, null, signal),
    enabled: user.isOps,
    select: (page) => (page.nextCursor ? `${page.items.length}+` : String(page.items.length)),
  });

  const roles = user.roles.map((role) => roleLabels[role] ?? role).join(", ");

  return (
    <>
      <a className="skip-link" href="#main">
        İçeriğe geç
      </a>
      <header className="topbar">
        <div className="brand">
          LST <span>Operasyon</span>
        </div>
        <nav aria-label="Ekranlar">
          <ul className="nav">
            <li>
              <NavLink to="/shipments">Gönderiler</NavLink>
            </li>
            {user.isHubOperator && (
              <li>
                <NavLink to="/scan">Tarama</NavLink>
              </li>
            )}
            {user.isOps && (
              <li>
                <NavLink to="/exceptions">
                  İstisnalar
                  {openExceptions.data !== undefined && (
                    <span className="count" aria-label={`${openExceptions.data} açık`}>
                      {openExceptions.data}
                    </span>
                  )}
                </NavLink>
              </li>
            )}
            {user.isOps && (
              <li>
                <NavLink to="/summary">Özet</NavLink>
              </li>
            )}
          </ul>
        </nav>
        <div className="topbar-right">
          <span>
            {user.displayName || "Kullanıcı"} · <span className="muted">{roles || "Rol yok"}</span>
            {user.hubCodes.length > 0 && <span className="muted mono"> · {user.hubCodes.join(", ")}</span>}
          </span>
          <button type="button" className="btn" onClick={toggleTheme} aria-pressed={theme === "dark"}>
            Koyu tema
          </button>
          <button
            type="button"
            className="btn"
            disabled={loggingOut}
            onClick={() => {
              setLoggingOut(true);
              void logout().finally(() => setLoggingOut(false));
            }}
          >
            Çıkış
          </button>
        </div>
      </header>
      <StatusBar />
      <main id="main" tabIndex={-1}>
        {children}
      </main>
    </>
  );
}
