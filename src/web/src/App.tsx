import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import { AuthProvider, useAuth, type SessionUser } from "./auth/AuthContext";
import { Notice } from "./components/Feedback";
import { Layout } from "./components/Layout";
import { ExceptionsPage } from "./features/exceptions/ExceptionsPage";
import { LoginPage } from "./features/login/LoginPage";
import { PublicTrackingPage } from "./features/public/PublicTrackingPage";
import { RealtimeProvider } from "./realtime/RealtimeProvider";
import { ScanPage } from "./features/scan/ScanPage";
import { CreateShipmentPage } from "./features/shipments/CreateShipmentPage";
import { ShipmentDetailPage } from "./features/shipments/ShipmentDetailPage";
import { ShipmentListPage } from "./features/shipments/ShipmentListPage";
import { SummaryPage } from "./features/summary/SummaryPage";
import type { ReactNode } from "react";

/** Yetkisiz rota doğrudan URL ile açılırsa gösterilir. Butonu gizlemek güvenlik değildir; API ayrıca reddeder. */
function Forbidden() {
  return (
    <Notice tone="neutral" role="alert" icon="lock">
      <p>Bu işlem için yetkiniz yok. Hub ataması için operasyon yöneticinize başvurun.</p>
    </Notice>
  );
}

function NotFound() {
  return (
    <Notice tone="neutral" role="alert">
      <p>Sayfa bulunamadı.</p>
    </Notice>
  );
}

function Guard({ user, allow, children }: { user: SessionUser; allow: (user: SessionUser) => boolean; children: ReactNode }) {
  return allow(user) ? children : <Forbidden />;
}

function InternalApp() {
  const { state, retry } = useAuth();
  const location = useLocation();

  if (state.status === "loading") {
    return (
      <main id="main" className="login" aria-busy="true">
        <p role="status" className="muted">
          Oturum kontrol ediliyor…
        </p>
      </main>
    );
  }

  if (state.status === "anonymous") {
    if (state.reason === "unavailable") {
      return (
        <main id="main" className="login">
          <Notice tone="critical" role="alert" icon="plug">
            <p>Sunucuya ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.</p>
            <div className="notice-actions">
              <button type="button" className="btn" onClick={retry}>
                Yeniden dene
              </button>
            </div>
          </Notice>
        </main>
      );
    }
    if (location.pathname !== "/login") {
      return <Navigate to="/login" replace state={{ from: `${location.pathname}${location.search}`, expired: state.reason === "expired" }} />;
    }
    return <LoginPage />;
  }

  const user = state.user;
  const home = user.isOps ? "/shipments" : user.isHubOperator ? "/scan" : "/shipments";
  if (location.pathname === "/login") {
    const from = (location.state as { from?: string } | null)?.from;
    return <Navigate to={from && from !== "/login" ? from : home} replace />;
  }

  // Rol veya hub ataması değişirse canlı bağlantı yeni yetkiyle baştan kurulur.
  const realtimeKey = `${user.id}|${user.roles.join(",")}|${user.hubCodes.join(",")}`;
  return (
    <RealtimeProvider key={realtimeKey} user={user}>
    <Layout user={user}>
      <Routes>
        <Route path="/" element={<Navigate to={home} replace />} />
        <Route path="/shipments" element={<ShipmentListPage />} />
        <Route
          path="/shipments/new"
          element={
            <Guard user={user} allow={(u) => u.isOps}>
              <CreateShipmentPage />
            </Guard>
          }
        />
        <Route path="/shipments/:shipmentId" element={<ShipmentDetailPage />} />
        <Route
          path="/exceptions"
          element={
            <Guard user={user} allow={(u) => u.isOps}>
              <ExceptionsPage />
            </Guard>
          }
        />
        <Route
          path="/summary"
          element={
            <Guard user={user} allow={(u) => u.isOps}>
              <SummaryPage />
            </Guard>
          }
        />
        <Route
          path="/scan"
          element={
            <Guard user={user} allow={(u) => u.isHubOperator}>
              <ScanPage />
            </Guard>
          }
        />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </Layout>
    </RealtimeProvider>
  );
}

export function App() {
  return (
    <Routes>
      {/* Public takip: oturum sağlayıcısı, operasyon kabuğu ve iç API çağrısı yok. */}
      <Route path="/t/:code" element={<PublicTrackingPage />} />
      <Route
        path="*"
        element={
          <AuthProvider>
            <InternalApp />
          </AuthProvider>
        }
      />
    </Routes>
  );
}
