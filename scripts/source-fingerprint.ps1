# Kaynak durumunun parmak izi (git olmadığı için). bin/obj/TestResults/node_modules/.local ve doğrulama logları hariç.
# Kullanım: pwsh -File scripts/source-fingerprint.ps1  → dosya sayısı + birleşik SHA-256
param([string]$Root = (Resolve-Path "$PSScriptRoot\..").Path)

$excluded = '\\(bin|obj|TestResults|node_modules|dist|\.local[^\\]*|artifacts|playwright-report|test-results)\\|\\docs\\verification\\'
$files = Get-ChildItem -Path $Root -Recurse -File -Force |
    Where-Object { $_.FullName -notmatch $excluded } |
    Sort-Object { $_.FullName.Substring($Root.Length).ToLowerInvariant() }

$sha = [System.Security.Cryptography.SHA256]::Create()
$builder = New-Object System.Text.StringBuilder
foreach ($file in $files) {
    $relative = $file.FullName.Substring($Root.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
    [void]$builder.Append("$relative $hash`n")
}
$combined = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($builder.ToString()))).Replace('-', '').ToLowerInvariant()
"files=$($files.Count) sha256=$combined"
