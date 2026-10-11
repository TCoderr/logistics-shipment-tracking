<#
.SYNOPSIS
  LST yerel Docker yığını: secret/sertifika hazırlığı, başlatma, durdurma.
.DESCRIPTION
  init   : LocalDir altında rastgele parolalar, bağlantı dizeleri ve HTTPS sertifikası (mevcut .NET geliştirici sertifikasının
           PFX dışa aktarımı; sertifika deposu değişmez). -SeedDemo ile demo kullanıcı parolaları da üretilir.
           Değerler ekrana yazılmaz; yalnız dosyalarda (git'e girmez) durur.
  up     : image build + compose up (sağlıklı olana kadar bekler).
  down   : yığını durdurur (volume'lar kalır). -Purge yalnız bu projenin volume'larını siler.
  status : compose ps.
.EXAMPLE
  .\scripts\local-stack.ps1 init -SeedDemo; .\scripts\local-stack.ps1 up -SeedDemo [-Simulation]
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('init', 'up', 'down', 'status')] [string]$Action,
    [string]$Project = 'lst',
    [string]$LocalDir = '.local',
    [int]$Port = 8443,
    [switch]$SeedDemo,
    [switch]$Simulation,
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$local = Join-Path $root $LocalDir

function New-Secret([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return ([Convert]::ToBase64String($buffer)).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
}

function Write-SecretFile([string]$Path, [string]$Value) {
    # Sonda satır sonu yok: key-per-file ve *_FILE okuyucuları değeri olduğu gibi alır.
    [System.IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
}

function Ensure-Secret([string]$Name, [scriptblock]$Factory) {
    $path = Join-Path $local "secrets\$Name"
    if (-not (Test-Path $path)) { Write-SecretFile $path (& $Factory) }
    return [System.IO.File]::ReadAllText($path)
}

function Find-Docker {
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'
    if (Test-Path $candidate) { return $candidate }
    throw 'docker CLI bulunamadı. Docker Desktop çalışıyor olmalı.'
}

function Set-ComposeEnvironment {
    $env:LST_LOCAL_DIR = './' + ($LocalDir -replace '^\.[\\/]', '').Replace('\', '/')
    $env:LST_HTTPS_PORT = "$Port"
    $env:LST_SEED_DEMO = $(if ($SeedDemo) { 'true' } else { 'false' })
    $env:LST_SIMULATION = $(if ($Simulation) { 'true' } else { 'false' })
}

switch ($Action) {
    'init' {
        foreach ($dir in 'secrets', 'certs', 'seed') { New-Item -ItemType Directory -Force (Join-Path $local $dir) | Out-Null }
        Ensure-Secret 'postgres_superuser_password' { New-Secret } | Out-Null
        $owner = Ensure-Secret 'lst_owner_password' { New-Secret }
        $app = Ensure-Secret 'lst_app_password' { New-Secret }
        # Simüle carrier (SIMEX) webhook HMAC secret'ı; en az 32 karakter.
        Ensure-Secret 'simex_webhook_secret' { New-Secret 36 } | Out-Null
        Write-SecretFile (Join-Path $local 'secrets\migrator_connection') "Host=postgres;Port=5432;Database=lst;Username=lst_owner;Password=$owner;Timeout=5;Gss Encryption Mode=Disable"
        Write-SecretFile (Join-Path $local 'secrets\app_connection') "Host=postgres;Port=5432;Database=lst;Username=lst_app;Password=$app;Timeout=3;Command Timeout=15;Gss Encryption Mode=Disable"

        $pfx = Join-Path $local 'certs\lst-dev.pfx'
        if (-not (Test-Path $pfx)) {
            $pfxPassword = Ensure-Secret 'https_certificate_password' { New-Secret }
            $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
            if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
            & $dotnet dev-certs https --export-path $pfx --password $pfxPassword --format Pfx | Out-Null
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pfx)) { throw 'Geliştirici sertifikası dışa aktarılamadı (dotnet dev-certs https --check ile kontrol edin).' }
        }

        if ($SeedDemo) {
            foreach ($user in 'ops.agent', 'ops.manager', 'hub.ist', 'hub.ank', 'hub.izm') {
                $path = Join-Path $local "seed\Seed__Passwords__$user"
                # Identity politikası: >= 12 karakter, büyük/küçük harf, rakam, sembol.
                if (-not (Test-Path $path)) { Write-SecretFile $path ("Lst!" + (New-Secret 15) + "aA9") }
            }
        }
        Write-Host "Hazır: $LocalDir (secrets, certs$(if ($SeedDemo) { ', seed' })). Değerler ekrana yazılmadı."
    }
    'up' {
        $docker = Find-Docker
        if (-not (Test-Path (Join-Path $local 'secrets\app_connection'))) { throw "Önce: .\scripts\local-stack.ps1 init -LocalDir $LocalDir" }
        Set-ComposeEnvironment
        & $docker compose -p $Project up -d --build --wait
        $upExit = $LASTEXITCODE
        if ($upExit -ne 0) { & $docker compose -p $Project ps -a; throw "compose up başarısız (exit $upExit)" }
        & $docker compose -p $Project ps
        Write-Host "API: https://localhost:$Port  (OpenAPI Development'ta: /openapi/v1.json)"
    }
    'down' {
        $docker = Find-Docker
        Set-ComposeEnvironment
        if ($Purge) { & $docker compose -p $Project down -v --remove-orphans } else { & $docker compose -p $Project down --remove-orphans }
    }
    'status' {
        $docker = Find-Docker
        Set-ComposeEnvironment
        & $docker compose -p $Project ps -a
    }
}
