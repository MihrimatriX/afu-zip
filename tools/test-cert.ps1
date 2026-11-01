# Yalnızca CI ve yerel deneme için: kendinden imzalı bir kod imzalama sertifikası oluşturur ve bu bilgisayarda
# güvenilir yapar (LocalMachine\TrustedPeople, yönetici gerekir). Sparse paket bununla imzalanınca Windows 11'in yeni
# menüsü bu bilgisayarda denenebilir. Dağıtılan sürüm böyle imzalanmaz.
param(
  [string] $Subject = 'CN=AFU Zip Test',
  [Parameter(Mandatory)] [string] $Out,
  [string] $Password = 'afuzip-test'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature -FriendlyName 'AFU Zip test' `
  -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$pfx = Join-Path $Out 'test.pfx'
$cer = Join-Path $Out 'test.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $Password -Force -AsPlainText) | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Write-Host "Test sertifikası: $pfx ($Subject)"
