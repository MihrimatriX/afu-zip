# AFU Zip Explorer eklentisini derler ve uygulama klasörüne koyar; sertifika verilirse Windows 11 yeni menüsü için
# sparse paketi (AfuZip.ContextMenu.msix) de üretip imzalar. İmzasız paket kurulamadığı için sertifikasız
# derlemede yalnızca DLL konur: klasik menüde dinamik başlıklar çalışır, yeni menü atlanır.
#
#   ./tools/build-shell.ps1 -Platform x64 -Out publish/AfuZip -Version 0.7.0
#   ./tools/build-shell.ps1 -Platform x64 -Out publish/AfuZip -Version 0.7.0 -Publisher "CN=..." -Certificate imza.pfx -Password ...
#   ./tools/build-shell.ps1 -Platform x64 -Out publish/AfuZip -Version 0.7.0 -Publisher "CN=..." -PackOnly   # imzayı dış servis atar
param(
  [Parameter(Mandatory)] [ValidateSet('x64', 'ARM64')] [string] $Platform,
  [Parameter(Mandatory)] [string] $Out,
  [string] $Version = '0.0.0',
  [string] $Publisher = 'CN=AFU Zip',
  [string] $Certificate,
  [string] $Password,
  [switch] $PackOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$build = Join-Path $root "artifacts/shell-$Platform"
New-Item -ItemType Directory -Force $Out | Out-Null

cmake -S (Join-Path $root 'src/AfuZip.ShellExtension') -B $build -A $Platform "-DAFUZIP_PUBLISHER=$Publisher"
if ($LASTEXITCODE) { throw "cmake yapılandırması başarısız" }
cmake --build $build --config Release
if ($LASTEXITCODE) { throw "Eklenti derlenemedi" }
Copy-Item (Join-Path $build 'Release/AfuZip.ShellExtension.dll') $Out -Force
Write-Host "Eklenti: $(Join-Path $Out 'AfuZip.ShellExtension.dll')"

if (-not $Certificate -and -not $PackOnly) { return }

# --- Sparse paket -------------------------------------------------------------------------------------------
$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $kit) { throw "Windows SDK (makeappx) bulunamadı" }
$signtool = Join-Path $kit.DirectoryName 'signtool.exe'

$stage = Join-Path $build 'msix'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse (Join-Path $PSScriptRoot 'sparse/Assets') $stage

# Paket sürümü dört parçalı ve yalnızca sayı: "0.7.0-beta" → "0.7.0.0".
$numbers = @(($Version -replace '[^0-9.].*$', '').Split('.') | Where-Object { $_ -ne '' })
while ($numbers.Count -lt 4) { $numbers += '0' }
$packageVersion = ($numbers[0..3] -join '.')
$arch = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
$manifest = Get-Content (Join-Path $PSScriptRoot 'sparse/AppxManifest.xml') -Raw
$manifest = $manifest.Replace('$PUBLISHER$', [System.Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('$VERSION$', $packageVersion).Replace('$ARCH$', $arch)
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))

$msix = Join-Path $Out 'AfuZip.ContextMenu.msix'
# /nv: dış konumdaki dosyalar (DLL, exe) paketin içinde olmadığı için içerik doğrulaması atlanır.
& $kit.FullName pack /d $stage /p $msix /nv /o
if ($LASTEXITCODE) { throw "makeappx başarısız" }
if ($PackOnly) {
  Write-Host "İmzasız sparse paket (imza servisine gidecek): $msix"
  return
}
& $signtool sign /fd SHA256 /f $Certificate /p $Password $msix
if ($LASTEXITCODE) { throw "signtool başarısız" }
Write-Host "Sparse paket: $msix ($packageVersion, $arch, $Publisher)"
