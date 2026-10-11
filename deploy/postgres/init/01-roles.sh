#!/bin/sh
# PostgreSQL ilk başlatmada (boş volume) bir kez çalışır: migration sahibi rol ve çalışma zamanı rolü.
# Parolalar Compose secret dosyalarından okunur; komut satırına veya loga yazılmaz.
set -eu

owner_password="$(cat "$LST_OWNER_PASSWORD_FILE")"
app_password="$(cat "$LST_APP_PASSWORD_FILE")"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v owner_password="$owner_password" -v app_password="$app_password" <<'SQL'
-- Migration'ları uygulayan, tabloların sahibi olan rol (superuser değil).
CREATE ROLE lst_owner LOGIN PASSWORD :'owner_password';
-- Uygulamanın bağlandığı rol: DDL yok; tablo yetkilerini migrator db/runtime-role-grants.sql ile verir.
CREATE ROLE lst_app LOGIN PASSWORD :'app_password';

ALTER DATABASE :"DBNAME" OWNER TO lst_owner;
REVOKE ALL ON DATABASE :"DBNAME" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"DBNAME" TO lst_owner, lst_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
