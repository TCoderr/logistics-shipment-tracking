# syntax=docker/dockerfile:1
# LST API image: tek image, iki kullanım (API sunucusu ve tek seferlik `--migrate` komutu).
# Secret, sertifika ve parola image katmanlarına girmez; çalışma zamanında salt-okunur dosya olarak bağlanır.

# ---- build: resmi .NET SDK (global.json ile aynı sürüm) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src

# Bağımlılık katmanı: yalnız proje/kilit dosyaları değişince restore yeniden çalışır.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY db/runtime-role-grants.sql db/
COPY src/Lst.Api/Lst.Api.csproj src/Lst.Api/packages.lock.json src/Lst.Api/
COPY src/Lst.Platform/Lst.Platform.csproj src/Lst.Platform/packages.lock.json src/Lst.Platform/
COPY src/Lst.Modules.Identity/Lst.Modules.Identity.csproj src/Lst.Modules.Identity/packages.lock.json src/Lst.Modules.Identity/
COPY src/Lst.Modules.Shipments/Lst.Modules.Shipments.csproj src/Lst.Modules.Shipments/packages.lock.json src/Lst.Modules.Shipments/
COPY src/Lst.Modules.Operations/Lst.Modules.Operations.csproj src/Lst.Modules.Operations/packages.lock.json src/Lst.Modules.Operations/
COPY src/Lst.Modules.Tracking/Lst.Modules.Tracking.csproj src/Lst.Modules.Tracking/packages.lock.json src/Lst.Modules.Tracking/
RUN dotnet restore src/Lst.Api/Lst.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Lst.Api/Lst.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

# ---- web: operasyon paneli (React SPA) statik çıktısı; resmi Node image'ı, kilit dosyasından kurulum ----
FROM node:24.18.0-bookworm-slim AS web
ENV CI=1 \
    NPM_CONFIG_UPDATE_NOTIFIER=false
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/web/ ./
RUN npm run build

# ---- runtime: resmi ASP.NET Core runtime, root olmayan kullanıcı ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app

# Data Protection anahtarları için yazılabilir tek dizin (volume buraya bağlanır); uygulama dosyaları root'a ait ve salt-okunur.
RUN mkdir -p /var/lib/lst/dataprotection \
    && chown -R "$APP_UID" /var/lib/lst \
    && chmod 0700 /var/lib/lst/dataprotection

COPY --from=build --chown=root:root /app/publish/ ./
# SPA aynı origin'den sunulur (wwwroot); kaynak haritası ve node_modules image'a girmez.
COPY --from=web --chown=root:root /web/dist/ ./wwwroot/

ENV ASPNETCORE_URLS=https://+:8443 \
    DataProtection__KeysPath=/var/lib/lst/dataprotection \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["dotnet", "Lst.Api.dll"]
