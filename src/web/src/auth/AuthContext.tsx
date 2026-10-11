import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { authApi } from "../api/endpoints";
import { ApiError, setCsrfToken, setUnauthenticatedHandler } from "../api/http";
import type { CurrentUser, Role } from "../api/types";

export interface SessionUser {
  id: string;
  displayName: string;
  roles: Role[];
  hubCodes: string[];
  isOps: boolean;
  isManager: boolean;
  isHubOperator: boolean;
}

export type AuthState =
  | { status: "loading" }
  | { status: "anonymous"; reason: "none" | "expired" | "unavailable" }
  | { status: "authenticated"; user: SessionUser };

export interface AuthContextValue {
  state: AuthState;
  login: (userName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  retry: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);
const sessionKey = ["auth", "session"] as const;
/** Oturum sorgusunun anahtarı: canlı kanal, bağlantı reddedilince oturumu bununla yeniden yoklatır. */
export const sessionQueryKey = sessionKey;

function toSessionUser(me: CurrentUser): SessionUser {
  const roles = me.roles;
  return {
    id: me.id,
    displayName: me.displayName,
    roles,
    hubCodes: me.hubCodes,
    isOps: roles.includes("OpsAgent") || roles.includes("OpsManager"),
    isManager: roles.includes("OpsManager"),
    isHubOperator: roles.includes("HubOperator"),
  };
}

/** Oturum sorgusu: 200 → kullanıcı, 401 → null (anonim). CSRF token'ı yalnız bellekte tutulur, önbelleğe yazılmaz. */
async function fetchSession(signal?: AbortSignal): Promise<SessionUser | null> {
  try {
    const me = await authApi.me(signal);
    setCsrfToken(me.data.csrfToken);
    return toSessionUser(me.data);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      setCsrfToken(null);
      return null;
    }
    throw error;
  }
}

/** Oturum dışındaki tüm sunucu verisi önbellekten atılır (başka kullanıcının verisi ekranda kalmasın). */
function dropUserData(queryClient: QueryClient): void {
  queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== sessionKey[0] });
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [expired, setExpired] = useState(false);
  // Bu sekmede geçerli bir oturum görüldü mü? Oturum, açık bir çıkış olmadan kaybolursa "süresi doldu" sayılır.
  // Kayıp iki yoldan fark edilebilir (herhangi bir istekte 401 veya oturum sorgusunun yenilenmesi); sıra önemli değildir.
  const hadSession = useRef(false);

  const loadSession = useCallback(async (signal?: AbortSignal) => {
    const user = await fetchSession(signal);
    if (user) {
      hadSession.current = true;
    } else if (hadSession.current) {
      hadSession.current = false;
      setExpired(true);
    }
    return user;
  }, []);

  const session = useQuery({
    queryKey: sessionKey,
    queryFn: ({ signal }) => loadSession(signal),
    retry: false,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });

  useEffect(() => {
    // Oturum sunucuda sona erdiyse (herhangi bir istekte 401) veriler atılır ve giriş ekranına dönülür.
    setUnauthenticatedHandler(() => {
      if (hadSession.current) {
        hadSession.current = false;
        setExpired(true);
      }
      setCsrfToken(null);
      queryClient.setQueryData(sessionKey, null);
      dropUserData(queryClient);
    });
    return () => setUnauthenticatedHandler(null);
  }, [queryClient]);

  const login = useCallback(
    async (userName: string, password: string) => {
      await authApi.login(userName, password);
      setExpired(false);
      dropUserData(queryClient);
      // Girişten sonra kullanıcıya bağlı yeni CSRF token'ı /me'den alınır.
      await queryClient.fetchQuery({ queryKey: sessionKey, queryFn: ({ signal }) => loadSession(signal), staleTime: 0 });
    },
    [queryClient, loadSession],
  );

  const logout = useCallback(async () => {
    // Açık çıkış "süresi doldu" değildir.
    hadSession.current = false;
    try {
      await authApi.logout();
    } finally {
      setCsrfToken(null);
      setExpired(false);
      queryClient.setQueryData(sessionKey, null);
      dropUserData(queryClient);
    }
  }, [queryClient]);

  const retry = useCallback(() => {
    void queryClient.resetQueries({ queryKey: sessionKey });
  }, [queryClient]);

  const state = useMemo<AuthState>(() => {
    if (session.isPending) return { status: "loading" };
    if (session.data) return { status: "authenticated", user: session.data };
    if (session.isError) return { status: "anonymous", reason: "unavailable" };
    return { status: "anonymous", reason: expired ? "expired" : "none" };
  }, [session.isPending, session.data, session.isError, expired]);

  const value = useMemo(() => ({ state, login, logout, retry }), [state, login, logout, retry]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth, AuthProvider içinde kullanılmalı.");
  return context;
}

export function useSessionUser(): SessionUser {
  const { state } = useAuth();
  if (state.status !== "authenticated") throw new Error("Oturum gerekli.");
  return state.user;
}
