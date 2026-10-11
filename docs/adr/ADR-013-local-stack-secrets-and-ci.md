# ADR-013 Yerel Docker yığını, secret'lar ve CI (Dilim 2)

Durum: Kabul edildi (2026-10-10)

## Bağlam
API'nin HTTPS cookie/CSRF sözleşmesi (`__Host-`, `Secure`), runtime/migration DB rol ayrımı ve idempotency davranışı yerel demo ve CI'da gevşetilmeden çalışmalı.

## Kararlar
1. **Tek image, iki kullanım.** `Dockerfile` multi-stage (`sdk:10.0.401` → `aspnet:10.0.12`), root olmayan `app` (UID 1654) kullanıcı. Aynı image API sunucusu ve tek seferlik `--migrate` komutu olarak çalışır. Üretim API başlangıcı migration yapmaz.
2. **Migration sahibi rol ayrı.** PostgreSQL init betiği (`deploy/postgres/init/01-roles.sh`) yalnız ilk başlatmada `lst_owner` (DB sahibi, superuser değil) ve `lst_app` (DDL yok) rollerini oluşturur. `--migrate` komutu `lst_owner` ile migration'ı ve ardından gömülü `db/runtime-role-grants.sql` yetkilerini uygular (`DatabaseMaintenance.MigrateAndGrantAsync`). Integration test fixture'ı aynı kodu kullanır.
3. **Secret'lar dosyadan.** Bağlantı dizeleri, PFX parolası ve demo parolaları Compose secret / salt-okunur bind mount olarak `/run/secrets` ve `/run/lst-seed` altına bağlanır; `AddKeyPerFile` ile okunur. Ortam değişkeni (docker inspect) ve image katmanında yoktur. `scripts/local-stack.ps1 init` değerleri rastgele üretir, ekrana yazmaz; dizin `.local*/` git'e girmez.
4. **HTTPS.** Kestrel doğrudan HTTPS sunar; sertifika mevcut .NET geliştirici sertifikasının PFX dışa aktarımıdır (sertifika deposu değişmez), salt-okunur bağlanır. TLS proxy eklenmedi (tek servis, ek bileşen gereksiz).
5. **Data Protection kalıcı.** `DataProtection:KeysPath` volume'a bağlıdır; API konteyneri yeniden oluşturulunca auth/antiforgery cookie'leri geçerli kalır (duman testiyle doğrulandı). Anahtarlar Linux'ta dosya sisteminde şifrelenmeden durur: yerel demo için kabul edildi; üretimde anahtar şifreleme (sertifika/KMS) gerekir.
6. **Demo seed açık opt-in.** `Seed:Enabled=true` yalnız Development'ta kabul edilir; aksi halde `--migrate` 3 koduyla reddeder. Parolası verilmeyen kullanıcı oluşturulmaz.
7. **Health.** Konteyner health check'i `--probe https://localhost:8443/health/live` (yalnız loopback için sertifika doğrulaması atlanır). `/health/ready` DB kontrolü 3 sn timeout; DB kesintisinde 503, iyileşince 200.
8. **Npgsql GSS.** Konteynerde Kerberos kütüphanesi olmadığı için bağlantı dizelerinde `Gss Encryption Mode=Disable`.
9. **CI.** `.github/workflows/ci.yml`: `contents: read`, resmi action'lar tam SHA ile (checkout v7.0.1, setup-dotnet v6.0.0, upload-artifact v7.0.1; iki haftadan yeni sürümler bilinçli alınmadı), kilitli restore, Release build, unit + integration (Testcontainers), pending model kontrolü, idempotent migration script, Docker build, 7 gün saklanan TRX/JUnit artifact'ları. **Remote koşu bu görevde yapılmadı.**
10. **Kilit dosyaları.** `RestorePackagesWithLockFile=true`; Docker ve CI `--locked-mode` restore yapar.

## Sonuçlar
- Compose ile yerel yığın: `scripts/local-stack.ps1 init -SeedDemo` → `up -SeedDemo`.
- İzole doğrulama farklı proje adı ve `LocalDir` ile yapılır; cleanup yalnız o projenin kaynaklarına dokunur (`down -Purge`).
