#!/usr/bin/env bash
# İzole E2E yığını (CI/Linux ve Git Bash): rastgele secret'lar + geliştirici sertifikası + demo parolaları üretir,
# image'ı derler ve Compose yığınını sağlıklı olana kadar bekler. scripts/local-stack.ps1'in bash eşdeğeridir.
# Değerler ekrana yazılmaz; yalnız LOCAL_DIR altındaki dosyalarda durur (git'e girmez, artifact olarak yüklenmez).
# Kullanım: scripts/ci-e2e-stack.sh [LOCAL_DIR=.local-e2e] [PROJECT=lst-e2e] [PORT=18443]
# Temizlik: docker compose -p <PROJECT> down -v --remove-orphans  (yalnız bu projenin kaynakları)
set -euo pipefail

LOCAL_DIR="${1:-.local-e2e}"
PROJECT="${2:-lst-e2e}"
PORT="${3:-18443}"

cd "$(dirname "$0")/.."
umask 077
mkdir -p "$LOCAL_DIR/secrets" "$LOCAL_DIR/certs" "$LOCAL_DIR/seed"

random_hex() { openssl rand -hex "$1"; }

# Dosya yoksa üretir; varsa dokunmaz (mevcut volume ile uyum bozulmasın). Sonda satır sonu yoktur.
ensure_secret() {
  local path="$LOCAL_DIR/secrets/$1"
  if [ ! -s "$path" ]; then printf '%s' "$(random_hex "${2:-18}")" > "$path"; fi
}

ensure_secret postgres_superuser_password
ensure_secret lst_owner_password
ensure_secret lst_app_password
ensure_secret simex_webhook_secret 24
ensure_secret https_certificate_password

owner="$(cat "$LOCAL_DIR/secrets/lst_owner_password")"
app="$(cat "$LOCAL_DIR/secrets/lst_app_password")"
printf '%s' "Host=postgres;Port=5432;Database=lst;Username=lst_owner;Password=${owner};Timeout=5;Gss Encryption Mode=Disable" > "$LOCAL_DIR/secrets/migrator_connection"
printf '%s' "Host=postgres;Port=5432;Database=lst;Username=lst_app;Password=${app};Timeout=3;Command Timeout=15;Gss Encryption Mode=Disable" > "$LOCAL_DIR/secrets/app_connection"
unset owner app

if [ ! -s "$LOCAL_DIR/certs/lst-dev.pfx" ]; then
  dotnet dev-certs https --export-path "$LOCAL_DIR/certs/lst-dev.pfx" --password "$(cat "$LOCAL_DIR/secrets/https_certificate_password")" --format Pfx > /dev/null
fi

# Demo kullanıcı parolaları (Identity politikası: >= 12 karakter, büyük/küçük harf, rakam, sembol).
for user in ops.agent ops.manager hub.ist hub.ank hub.izm; do
  path="$LOCAL_DIR/seed/Seed__Passwords__${user}"
  if [ ! -s "$path" ]; then printf '%s' "Lst!$(random_hex 10)aA9" > "$path"; fi
done

# Konteynerler root olmayan kullanıcılarla çalışır ve bu dosyaları salt-okunur bağlar: tek kullanımlık CI dizininde okunabilir yapılır.
chmod -R a+rX "$LOCAL_DIR"

export LST_LOCAL_DIR="./${LOCAL_DIR#./}"
export LST_HTTPS_PORT="$PORT"
export LST_SEED_DEMO=true
export LST_SIMULATION=true

docker compose -p "$PROJECT" up -d --build --wait
docker compose -p "$PROJECT" ps
echo "E2E yığını hazır: https://localhost:${PORT} (proje ${PROJECT}, yerel dizin ${LOCAL_DIR}). Değerler ekrana yazılmadı."
