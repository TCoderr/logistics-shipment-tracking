"""LST migration doğrulaması (yalnız standart kütüphane + docker + dotnet ef).

Tek kullanımlık, bu betiğin kendi oluşturduğu bir PostgreSQL konteynerinde:
  1. Boş veritabanına idempotent script uygulanır (yeni kurulum); ikinci uygulama hiçbir şey değiştirmez.
  2. Önceki dilim şemalarında (Dilim 1 ve Dilim 5) örnek veri içeren veritabanları en son sürüme yükseltilir:
     veri korunur, yeni NOT NULL kolonlar varsayılanla dolar, geçmiş tablosu tamdır.
  3. Yükseltilen şemalar yeni kurulum şemasıyla aynıdır (pg_dump --schema-only karşılaştırması).

Konteyner rastgele adlı ve rastgele parolalıdır, port yayınlamaz ve betik sonunda silinir. Başka konteyner/volume'a dokunulmaz.
Parola ve bağlantı bilgisi çıktıya yazılmaz. Kullanım (önce Release build):
  python scripts/migration_upgrade_check.py
"""
from __future__ import annotations

import hashlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
IMAGE = "postgres:18.6"
SLICE1 = "20261009205708_InitialSlice1"
SLICE5 = "20261009224843_Slice5AttemptsExceptionsReturns"


def find(tool: str, fallback: str) -> str:
    return shutil.which(tool) or fallback


DOCKER = find("docker", str(Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/DockerDesktop/resources/bin/docker.exe"))
DOTNET = find("dotnet", r"C:\Program Files\dotnet\dotnet.exe")
results: list[tuple[str, bool, str]] = []


def record(name: str, ok: bool, detail: str = "") -> None:
    results.append((name, ok, detail))
    print(json.dumps({"check": name, "ok": ok, "detail": detail}, ensure_ascii=False), flush=True)
    if not ok:
        raise SystemExit(f"FAILED: {name}")


def run(args: list[str], *, stdin: bytes | None = None, check: bool = True) -> subprocess.CompletedProcess:
    completed = subprocess.run(args, input=stdin, capture_output=True, cwd=ROOT)
    if check and completed.returncode != 0:
        # Hata metni parola içermez (parola yalnız konteyner ortamındadır); yine de kısaltılır.
        raise SystemExit(f"command failed ({args[0]} {args[1] if len(args) > 1 else ''}): {completed.stderr.decode('utf-8', 'replace')[-600:]}")
    return completed


class Database:
    def __init__(self, container: str) -> None:
        self.container = container

    def psql(self, database: str, sql: str, *, tuples: bool = False) -> str:
        args = [DOCKER, "exec", "-i", self.container, "psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-q"]
        if tuples:
            args += ["-At"]
        return run(args, stdin=sql.encode("utf-8")).stdout.decode("utf-8").strip()

    def apply(self, database: str, script: Path) -> None:
        self.psql(database, script.read_text(encoding="utf-8-sig"))

    def create(self, database: str) -> None:
        self.psql("postgres", f'CREATE DATABASE "{database}";')

    def schema_lines(self, database: str) -> list[str]:
        """Şema dökümünün satırları. Oturuma özgü belirteçler ve yorumlar atılır; satır içi boşluklar tekilleştirilir
        (idempotent script, fonksiyon gövdelerini farklı girintiyle yazar; bu anlamsal bir fark değildir)."""
        dump = run([DOCKER, "exec", self.container, "pg_dump", "-U", "postgres", "-d", database, "--schema-only", "--no-owner", "--no-privileges"]).stdout
        lines = []
        for line in dump.decode("utf-8").splitlines():
            normalized = " ".join(line.split())
            if normalized and not normalized.startswith(("--", "\\restrict", "\\unrestrict")):
                lines.append(normalized)
        return lines

    def schema_hash(self, database: str) -> str:
        return hashlib.sha256("\n".join(self.schema_lines(database)).encode("utf-8")).hexdigest()

    def schema_diff(self, database: str, reference: str) -> str:
        """Şema farkının kısa özeti (yalnız DDL satırları; veri içermez)."""
        only = sorted(set(self.schema_lines(database)) ^ set(self.schema_lines(reference)))
        return "" if not only else f"{len(only)} farklı satır; ör. " + " || ".join(line[:110] for line in only[:4])

    def scalar(self, database: str, sql: str) -> str:
        return self.psql(database, sql, tuples=True)

    def insert(self, database: str, table: str, values: dict[str, str]) -> None:
        """NOT NULL ve varsayılansız kolonları tipine göre doldurur; `values` SQL ifadeleridir (CHECK kurallarına uyan değerler)."""
        schema, name = table.split(".")
        columns = self.scalar(database, f"""
            SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY ordinal_position)
              FROM information_schema.columns
             WHERE table_schema = '{schema}' AND table_name = '{name}' AND is_nullable = 'NO' AND column_default IS NULL
               AND is_identity = 'NO' AND is_generated = 'NEVER'""")
        row = dict(values)
        for item in filter(None, columns.split(",")):
            column, data_type = item.split(":", 1)
            if column in row:
                continue
            row[column] = {
                "uuid": "gen_random_uuid()", "boolean": "false", "integer": "1", "smallint": "1", "bigint": "1", "numeric": "1",
                "timestamp with time zone": "now()", "date": "current_date", "bytea": "'\\x00'::bytea", "jsonb": "'{}'::jsonb",
            }.get(data_type, "'x'")
        self.psql(database, f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join(row.values())});")


def seed(db: Database, database: str) -> dict[str, str]:
    """Dilim 1 şemasında geçerli örnek veri: 2 hub, 1 gönderi, 2 taraf, 1 koli, 1 olay. Değerler sentetiktir."""
    ids = {key: str(uuid.uuid4()) for key in ("hub1", "hub2", "shipment", "package")}
    db.insert(database, "operations.hubs", {"id": f"'{ids['hub1']}'", "code": "'MIG-01'", "name": "'Migration Hub 1'", "city": "'Test'", "is_active": "true"})
    db.insert(database, "operations.hubs", {"id": f"'{ids['hub2']}'", "code": "'MIG-02'", "name": "'Migration Hub 2'", "city": "'Test'", "is_active": "true"})
    db.insert(database, "shipments.shipments", {
        "id": f"'{ids['shipment']}'", "tracking_number": "'LS0000000018'", "public_tracking_code": "'0123456789ABCDEFGHJKMNPQRS'",
        "kind": "'Forward'", "service_level": "'Standard'", "status": "'Created'", "origin_hub_id": f"'{ids['hub1']}'",
        "destination_hub_id": f"'{ids['hub2']}'", "package_count": "1", "delivery_attempt_count": "0",
    })
    for role in ("Sender", "Recipient"):
        db.insert(database, "shipments.shipment_parties", {
            "shipment_id": f"'{ids['shipment']}'", "role": f"'{role}'", "full_name": "'Sentetik Kisi'", "phone_e164": "'+905320000000'",
            "phone_last4": "'0000'", "address_line1": "'Sentetik Adres 1'", "city": "'Test'", "postal_code": "'06000'", "country_code": "'TR'",
        })
    db.insert(database, "shipments.packages", {
        "id": f"'{ids['package']}'", "shipment_id": f"'{ids['shipment']}'", "sequence": "1", "label_code": "'LS0000000018-01'",
        "weight_grams": "100", "length_mm": "100", "width_mm": "100", "height_mm": "100",
    })
    db.insert(database, "shipments.tracking_events", {
        "shipment_id": f"'{ids['shipment']}'", "event_type": "'Created'", "source": "'Operator'", "outcome": "'Applied'", "status_after": "'Created'",
    })
    return ids


def verify_upgraded(db: Database, database: str, ids: dict[str, str], label: str, expected_migrations: int) -> None:
    history = db.scalar(database, 'SELECT count(*) FROM platform.__ef_migrations_history')
    record(f"{label}: tüm migration'lar uygulandı", history == str(expected_migrations), f"history={history}")
    row = db.scalar(database, f"""
        SELECT tracking_number || '|' || status || '|' || has_departed || '|' || extra_attempts_granted || '|' || (pii_redacted_at IS NULL)
          FROM shipments.shipments WHERE id = '{ids['shipment']}'""")
    record(f"{label}: gönderi korundu, yeni kolonlar varsayılanla doldu", row == "LS0000000018|Created|false|0|true", row)
    counts = db.scalar(database, f"""
        SELECT (SELECT count(*) FROM shipments.shipment_parties WHERE shipment_id = '{ids['shipment']}' AND full_name = 'Sentetik Kisi')
            || '|' || (SELECT count(*) FROM shipments.packages WHERE shipment_id = '{ids['shipment']}' AND current_hub_id IS NULL)
            || '|' || (SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = '{ids['shipment']}')
            || '|' || (SELECT count(*) FROM operations.hubs WHERE code LIKE 'MIG-%')
            || '|' || (SELECT count(*) FROM tracking.carriers WHERE code = 'SIMEX')""")
    record(f"{label}: taraflar, koli, olay, hub korundu; referans veri eklendi", counts == "2|1|1|2|1", counts)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    name = "lst-migcheck-" + secrets.token_hex(4)
    work = Path(tempfile.mkdtemp(prefix="lst-migcheck-"))
    try:
        ef = [DOTNET, "ef", "migrations", "script", "-p", "src/Lst.Platform", "-s", "src/Lst.Api", "--no-build", "--configuration", "Release"]
        full, slice1, slice5 = work / "full.sql", work / "slice1.sql", work / "slice5.sql"
        run(ef + ["--idempotent", "-o", str(full)])
        run(ef + ["0", SLICE1, "-o", str(slice1)])
        run(ef + ["0", SLICE5, "-o", str(slice5)])
        total = len(list((ROOT / "src/Lst.Platform/Persistence/Migrations").glob("*.Designer.cs")))
        record("migration script'leri üretildi", full.stat().st_size > 0 and total >= 7, f"migrations={total}")

        run([DOCKER, "run", "-d", "--name", name, "--label", "lst.task=migration-check", "-e", "POSTGRES_PASSWORD=" + secrets.token_urlsafe(24), IMAGE])
        db = Database(name)
        for _ in range(60):
            if run([DOCKER, "exec", name, "pg_isready", "-U", "postgres", "-h", "127.0.0.1"], check=False).returncode == 0:
                break
            time.sleep(1)
        else:
            raise SystemExit("PostgreSQL hazır olmadı")
        time.sleep(1)
        version = db.scalar("postgres", "SHOW server_version")
        record("tek kullanımlık PostgreSQL 18", version.startswith("18."), version)

        # 1) Yeni kurulum (boş DB) + idempotentlik
        db.create("lst_fresh")
        db.apply("lst_fresh", full)
        fresh_hash = db.schema_hash("lst_fresh")
        history = db.scalar("lst_fresh", 'SELECT count(*) FROM platform.__ef_migrations_history')
        record("boş DB: idempotent script ile kurulum", history == str(total), f"history={history}")
        db.apply("lst_fresh", full)
        record("boş DB: script ikinci kez uygulanınca şema değişmez", db.schema_hash("lst_fresh") == fresh_hash)

        # 2) Önceki dilim şemalarından yükseltme (veriyle)
        for label, script in (("Dilim 1 → son", slice1), ("Dilim 5 → son", slice5)):
            database = "lst_from_" + ("s1" if script is slice1 else "s5")
            db.create(database)
            db.apply(database, script)
            before = db.scalar(database, 'SELECT count(*) FROM platform.__ef_migrations_history')
            ids = seed(db, database)
            db.apply(database, full)
            record(f"{label}: yükseltme hatasız", True, f"history {before} → {total}")
            verify_upgraded(db, database, ids, label, total)
            # Örnek veri şemayı etkilemez: yükseltilmiş şema yeni kurulumla aynı olmalı.
            record(f"{label}: şema yeni kurulumla aynı", db.schema_hash(database) == fresh_hash, db.schema_diff(database, "lst_fresh"))
            db.apply(database, full)
            record(f"{label}: yeniden uygulama değişiklik yapmaz", db.schema_hash(database) == fresh_hash)
    finally:
        # Yalnız bu betiğin oluşturduğu konteyner (ad + etiket) silinir.
        owned = run([DOCKER, "ps", "-aq", "--filter", f"name=^{name}$", "--filter", "label=lst.task=migration-check"], check=False).stdout.decode().strip()
        if owned:
            run([DOCKER, "rm", "-f", "-v", name], check=False)
        shutil.rmtree(work, ignore_errors=True)

    print(json.dumps({"checks": len(results), "passed": sum(1 for _, ok, _ in results if ok)}), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
