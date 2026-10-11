"""LST Compose yığını duman testi (yalnız standart kütüphane).

Kontroller:
  1. /health/live ve /health/ready 200
  2. HTTPS cookie + CSRF: anonim token -> login -> kimlikli token -> create -> byte-byte replay -> detay (ETag eşit)
  3. Çalışma zamanı DB rolü audit tablosunda UPDATE/DELETE/TRUNCATE yapamaz
  4. API konteyneri yeniden oluşturulunca veri ve mevcut auth cookie geçerli kalır (kalıcı Data Protection anahtarları)
  5. PostgreSQL durdurulunca live 200 / ready 503; yeniden başlayınca ready 200

Secret, cookie, CSRF token ve parola çıktıya yazılmaz. Kullanım:
  python scripts/stack_smoke.py --project lst-smoke --local-dir .local-smoke --port 18443
"""
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class Check:
    def __init__(self) -> None:
        self.results: list[tuple[str, bool, str]] = []

    def record(self, name: str, ok: bool, detail: str = "") -> None:
        self.results.append((name, ok, detail))
        print(json.dumps({"check": name, "ok": ok, "detail": detail}, ensure_ascii=False), flush=True)
        if not ok:
            raise SystemExit(f"FAILED: {name}")


class Client:
    """HTTPS istemcisi: yalnız localhost için sertifika doğrulaması kapalı (geliştirici sertifikası)."""

    def __init__(self, base: str) -> None:
        self.base = base
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=context),
            urllib.request.HTTPCookieProcessor(self.jar),
        )

    def request(self, method: str, path: str, body: bytes | None = None, headers: dict[str, str] | None = None):
        req = urllib.request.Request(self.base + path, data=body, method=method, headers=headers or {})
        try:
            with self.opener.open(req, timeout=10) as resp:
                return resp.status, dict(resp.headers), resp.read()
        except urllib.error.HTTPError as err:
            return err.code, dict(err.headers), err.read()
        except (urllib.error.URLError, ConnectionError, TimeoutError) as err:
            return 0, {}, str(err).encode()


def docker() -> str:
    candidate = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/DockerDesktop/resources/bin/docker.exe"
    return str(candidate) if candidate.exists() else "docker"


def compose(project: str, local_dir: str, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    relative = local_dir.replace("\\", "/")
    env = dict(os.environ, LST_LOCAL_DIR=relative if relative.startswith("./") else "./" + relative)
    return subprocess.run([docker(), "compose", "-p", project, *args], cwd=ROOT, env=env,
                          capture_output=True, text=True, check=check)


def wait_for(client: Client, path: str, status: int, seconds: int) -> bool:
    deadline = time.time() + seconds
    while time.time() < deadline:
        code, _, _ = client.request("GET", path)
        if code == status:
            return True
        time.sleep(1)
    return False


def shipment_body() -> bytes:
    suffix = uuid.uuid4().hex[:8]
    return json.dumps({
        "serviceLevel": "Standard",
        "externalReference": f"SMOKE-{suffix}",
        "originHubCode": "IST-01",
        "destinationHubCode": "ANK-02",
        "sender": {"fullName": "Smoke Sender", "phone": "+902124445566",
                   "address": {"line1": "Smoke Cd. 1", "city": "İstanbul", "postalCode": "34200"}},
        "recipient": {"fullName": "Smoke Recipient", "phone": "+905321112233",
                      "address": {"line1": "Smoke Sk. 2", "city": "Ankara", "postalCode": "06420"}},
        "packages": [{"weightGrams": 1200, "lengthMm": 300, "widthMm": 200, "heightMm": 100}],
    }).encode()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", default="lst-smoke")
    parser.add_argument("--local-dir", default=".local-smoke")
    parser.add_argument("--port", type=int, default=18443)
    parser.add_argument("--user", default="ops.agent")
    args = parser.parse_args()

    # Compose yeniden oluşturma komutları yığını başlatan değerlerle aynı port/seed ayarını kullanmalı.
    os.environ["LST_HTTPS_PORT"] = str(args.port)
    os.environ["LST_SEED_DEMO"] = "true"

    check = Check()
    client = Client(f"https://localhost:{args.port}")
    password = (ROOT / args.local_dir / "seed" / f"Seed__Passwords__{args.user}").read_text(encoding="utf-8")

    check.record("health.live", wait_for(client, "/health/live", 200, 60))
    check.record("health.ready", wait_for(client, "/health/ready", 200, 60))

    code, headers, body = client.request("GET", "/api/auth/csrf")
    anonymous = json.loads(body)["csrfToken"] if code == 200 else ""
    check.record("csrf.anonymous", code == 200 and headers.get("Cache-Control") == "no-store")

    code, _, _ = client.request("POST", "/api/auth/login", json.dumps({"userName": args.user, "password": "wrong-password-123"}).encode(),
                                {"Content-Type": "application/json", "X-CSRF-TOKEN": anonymous})
    check.record("login.wrong_password_generic_401", code == 401)

    code, _, _ = client.request("POST", "/api/auth/login", json.dumps({"userName": args.user, "password": password}).encode(),
                                {"Content-Type": "application/json"})
    check.record("login.without_csrf_rejected", code == 400)

    code, _, _ = client.request("POST", "/api/auth/login", json.dumps({"userName": args.user, "password": password}).encode(),
                                {"Content-Type": "application/json", "X-CSRF-TOKEN": anonymous})
    cookie_names = sorted(c.name for c in client.jar)
    check.record("login.ok", code == 204 and "__Host-lst.auth" in cookie_names, f"cookies={cookie_names}")

    code, _, body = client.request("GET", "/api/auth/me")
    token = json.loads(body)["csrfToken"] if code == 200 else ""
    check.record("me.authenticated_token", code == 200 and token != anonymous)

    payload = shipment_body()
    key = "smoke-" + uuid.uuid4().hex
    create_headers = {"Content-Type": "application/json", "Idempotency-Key": key, "X-CSRF-TOKEN": token}
    code1, h1, b1 = client.request("POST", "/api/shipments", payload, create_headers)
    code2, h2, b2 = client.request("POST", "/api/shipments", payload, create_headers)
    check.record("create.201", code1 == 201)
    check.record("replay.identical", code2 == 201 and b1 == b2 and h1.get("ETag") == h2.get("ETag")
                 and h1.get("Location") == h2.get("Location") and h2.get("Idempotency-Replayed") == "true")

    location = h1.get("Location", "")
    code, hd, _ = client.request("GET", location)
    check.record("detail.etag_matches", code == 200 and hd.get("ETag") == h1.get("ETag"))

    for sql in ("UPDATE platform.audit_entries SET action = action",
                "DELETE FROM platform.audit_entries",
                "TRUNCATE platform.audit_entries"):
        shell = ('PGPASSWORD="$(cat /run/secrets/lst_app_password)" '
                 f'psql -h 127.0.0.1 -U lst_app -d lst -v ON_ERROR_STOP=1 -c "{sql}"')
        result = compose(args.project, args.local_dir, "exec", "-T", "postgres", "sh", "-c", shell, check=False)
        check.record(f"runtime_role.denied[{sql.split()[0]}]",
                     result.returncode != 0 and "permission denied" in result.stderr)
    count = compose(args.project, args.local_dir, "exec", "-T", "postgres", "sh", "-c",
                    'PGPASSWORD="$(cat /run/secrets/lst_app_password)" psql -h 127.0.0.1 -U lst_app -d lst -tAc '
                    '"SELECT count(*) FROM platform.audit_entries WHERE action = \'ShipmentCreated\'"', check=False)
    check.record("runtime_role.can_read_audit", count.returncode == 0 and int(count.stdout.strip() or 0) >= 1)

    compose(args.project, args.local_dir, "up", "-d", "--force-recreate", "--no-deps", "api")
    check.record("api.recreated_ready", wait_for(client, "/health/ready", 200, 90))
    code, hd, _ = client.request("GET", location)
    check.record("api.recreated_cookie_and_data_preserved", code == 200 and hd.get("ETag") == h1.get("ETag"))

    compose(args.project, args.local_dir, "stop", "postgres")
    live_code, _, _ = client.request("GET", "/health/live")
    check.record("outage.ready_503", wait_for(client, "/health/ready", 503, 30))
    check.record("outage.live_200", live_code == 200)
    compose(args.project, args.local_dir, "start", "postgres")
    check.record("recovery.ready_200", wait_for(client, "/health/ready", 200, 90))
    code, _, _ = client.request("GET", location)
    check.record("recovery.detail_200", code == 200)

    print(json.dumps({"summary": {"passed": sum(1 for r in check.results if r[1]), "total": len(check.results)}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
