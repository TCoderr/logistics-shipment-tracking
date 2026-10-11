"""Üçüncü taraf lisans bildirimlerini üretir (yalnız standart kütüphane).

Kaynaklar:
  - Web çalışma zamanı bağımlılıkları: src/web/package-lock.json + node_modules/<paket>/{package.json, LICENSE*}
  - API çalışma zamanı NuGet paketleri: src/Lst.Api/obj/project.assets.json + NuGet paket önbelleğindeki .nuspec dosyaları
Çıktılar:
  - THIRD-PARTY-NOTICES.md             (özet tablo: ad, sürüm, lisans, telif satırı)
  - src/web/public/third-party-notices.txt  (derlenmiş arayüzle birlikte dağıtılan tam lisans metinleri)
Lisans metinleri paketlerin kendi dosyalarından aynen alınır; hiçbir telif bilgisi uydurulmaz. Bulunamayan alan "belirtilmemiş" yazılır.
Kullanım: python scripts/third_party_notices.py   (önce: npm ci ve dotnet restore)
"""
from __future__ import annotations

import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "src" / "web"


def web_packages() -> list[dict]:
    lock = json.loads((WEB / "package-lock.json").read_text(encoding="utf-8"))
    packages = []
    for path, meta in lock.get("packages", {}).items():
        # Kök paket ve geliştirme bağımlılıkları dağıtılan pakete girmez.
        if not path or meta.get("dev") or meta.get("devOptional") or meta.get("optional"):
            continue
        directory = WEB / path
        manifest_path = directory / "package.json"
        if not manifest_path.exists():
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        license_file = next((f for f in sorted(directory.iterdir()) if f.is_file() and re.match(r"(?i)^(licen[sc]e|copying)(\.|$)", f.name)), None)
        text = license_file.read_text(encoding="utf-8", errors="replace").strip() if license_file else ""
        packages.append({
            "name": manifest.get("name", path.split("node_modules/")[-1]),
            "version": manifest.get("version", meta.get("version", "")),
            "license": manifest.get("license") if isinstance(manifest.get("license"), str) else "belirtilmemiş",
            "copyright": first_copyright(text),
            "text": text,
        })
    return sorted(packages, key=lambda p: p["name"].lower())


def first_copyright(text: str) -> str:
    for line in text.splitlines():
        if re.search(r"(?i)copyright\s*(\(c\)|©|\d{4})", line):
            return line.strip()
    return "belirtilmemiş"


def nuget_packages() -> list[dict]:
    assets_path = ROOT / "src" / "Lst.Api" / "obj" / "project.assets.json"
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    roots = [Path(p) for p in assets.get("packageFolders", {})] or [Path(os.path.expanduser("~")) / ".nuget" / "packages"]
    # Yalnız çalışma zamanına giren paketler (derleme/analiz paketleri hariç).
    runtime = set()
    for target in assets.get("targets", {}).values():
        for key, value in target.items():
            if value.get("type") == "package" and (value.get("runtime") or value.get("runtimeTargets")):
                if any(not name.endswith("_._") for name in (value.get("runtime") or {})) or value.get("runtimeTargets"):
                    runtime.add(key)
    packages = []
    for key in sorted(runtime, key=str.lower):
        name, version = key.split("/")
        nuspec = next((root / name.lower() / version / f"{name.lower()}.nuspec" for root in roots if (root / name.lower() / version / f"{name.lower()}.nuspec").exists()), None)
        license_value, authors, copyright_value, project_url = "belirtilmemiş", "belirtilmemiş", "belirtilmemiş", ""
        if nuspec:
            metadata = next((element for element in ET.parse(nuspec).getroot() if element.tag.endswith("metadata")), None)
            if metadata is not None:
                fields = {child.tag.split("}")[-1]: (child.text or "").strip() for child in metadata}
                license_value = fields.get("license") or fields.get("licenseUrl") or license_value
                authors = fields.get("authors") or authors
                copyright_value = fields.get("copyright") or copyright_value
                project_url = fields.get("projectUrl", "")
        packages.append({"name": name, "version": version, "license": license_value, "authors": authors, "copyright": copyright_value, "url": project_url})
    return packages


def cell(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ")


def main() -> int:
    web = web_packages()
    nuget = nuget_packages()
    summary = [
        "# Üçüncü taraf bileşenler ve lisansları",
        "",
        "Bu dosya `scripts/third_party_notices.py` ile üretilir; elle düzenlemeyin. Yalnız **dağıtılan** bileşenleri kapsar:",
        "derlenmiş arayüz paketine giren npm paketleri ve API image'ına giren NuGet paketleri. Geliştirme/test araçları (Vite, TypeScript, ESLint,",
        "Vitest, Playwright, xUnit, Testcontainers vb.) dağıtılmaz ve burada listelenmez. Taban image'lar (.NET, Node, PostgreSQL) kendi lisanslarıyla gelir.",
        "Web paketlerinin tam lisans metinleri arayüzle birlikte `/third-party-notices.txt` adresinden sunulur.",
        "",
        f"## Web (npm) — {len(web)} paket",
        "",
        "| Paket | Sürüm | Lisans | Telif satırı (paketin lisans dosyasından) |",
        "|---|---|---|---|",
    ]
    summary += [f"| {cell(p['name'])} | {p['version']} | {cell(p['license'])} | {cell(p['copyright'])} |" for p in web]
    summary += [
        "",
        f"## API (NuGet, çalışma zamanı) — {len(nuget)} paket",
        "",
        "| Paket | Sürüm | Lisans (nuspec) | Yazar (nuspec) | Telif (nuspec) |",
        "|---|---|---|---|---|",
    ]
    summary += [f"| {cell(p['name'])} | {p['version']} | {cell(p['license'])} | {cell(p['authors'])} | {cell(p['copyright'])} |" for p in nuget]
    (ROOT / "THIRD-PARTY-NOTICES.md").write_text("\n".join(summary) + "\n", encoding="utf-8", newline="\n")

    full = ["LST web arayuzu ile dagitilan ucuncu taraf yazilimlarin lisans metinleri.", "Bu dosya scripts/third_party_notices.py ile uretilir.", ""]
    missing = []
    for p in web:
        full += ["=" * 78, f"{p['name']} {p['version']} ({p['license']})", "=" * 78, p["text"] or "(Pakette lisans dosyasi bulunamadi; lisans: " + p["license"] + ")", ""]
        if not p["text"]:
            missing.append(p["name"])
    public = WEB / "public"
    public.mkdir(exist_ok=True)
    (public / "third-party-notices.txt").write_text("\n".join(full), encoding="utf-8", newline="\n")

    print(json.dumps({"web": len(web), "nuget": len(nuget), "webLicenses": sorted({p["license"] for p in web}),
                      "webWithoutLicenseFile": missing, "nugetWithoutLicense": [p["name"] for p in nuget if p["license"] == "belirtilmemiş"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
