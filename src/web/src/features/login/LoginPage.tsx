import { useState, type FormEvent } from "react";
import { useLocation } from "react-router-dom";
import { ApiError, NetworkError } from "../../api/http";
import { useAuth } from "../../auth/AuthContext";
import { Notice } from "../../components/Feedback";
import { TextField } from "../../components/Field";

export function LoginPage() {
  const { login } = useAuth();
  const location = useLocation();
  const expired = (location.state as { expired?: boolean } | null)?.expired === true;
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setFieldErrors({});
    try {
      await login(userName, password);
    } catch (failure) {
      // Parola hiçbir koşulda saklanmaz/loglanmaz; hata sonrası alan temizlenir.
      setPassword("");
      if (failure instanceof ApiError && failure.code === "invalid-credentials") {
        setError("Kullanıcı adı veya parola hatalı ya da hesap geçici olarak kilitli.");
      } else if (failure instanceof ApiError && failure.code === "validation") {
        setFieldErrors(failure.fieldErrors);
      } else if (failure instanceof ApiError && failure.status === 429) {
        setError("Çok fazla deneme yapıldı. Kısa süre sonra tekrar deneyin.");
      } else if (failure instanceof NetworkError) {
        setError("Sunucuya ulaşılamadı. Bağlantınızı kontrol edip yeniden deneyin.");
      } else {
        setError("Giriş yapılamadı. Yeniden deneyin.");
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main id="main" className="login">
      <form className="panel" onSubmit={(event) => void onSubmit(event)} noValidate>
        <h1>LST Operasyon</h1>
        {expired && !error && (
          <Notice tone="warn" role="status">
            <p>Oturumunuz sona erdi. Devam etmek için yeniden giriş yapın.</p>
          </Notice>
        )}
        {error && (
          <Notice tone="critical" role="alert">
            <p>{error}</p>
          </Notice>
        )}
        <TextField
          label="Kullanıcı adı"
          name="userName"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          required
          value={userName}
          errors={fieldErrors.userName}
          onChange={(event) => setUserName(event.target.value)}
        />
        <TextField
          label="Parola"
          name="password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          errors={fieldErrors.password}
          onChange={(event) => setPassword(event.target.value)}
        />
        <button type="submit" className="btn btn-primary" disabled={submitting}>
          {submitting ? "Giriş yapılıyor…" : "Giriş yap"}
        </button>
      </form>
    </main>
  );
}
