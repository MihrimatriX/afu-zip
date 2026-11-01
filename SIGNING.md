# İmza ve Windows 11 menüsü

AFU Zip'in sağ tık menüsü şu an 7-Zip gibi klasik menüde ("Daha fazla seçenek göster"). Üstteki yeni menüye girmek
(NanaZip gibi) ve kurulumda SmartScreen uyarısını kaldırmak için güvenilir bir kod imzalama sertifikası gerekiyor:
Windows 11 yeni menüye yalnızca imzalı paketlerin (MSIX) eklentisini alır.

## Hazır olanlar

- `src/AfuZip.ShellExtension`: Explorer eklentisi (`IExplorerCommand`). Klasik menüde bugün de kullanılıyor.
- `tools/sparse/AppxManifest.xml`: yeni menü için sparse paket. Program içermez; kurulu uygulama klasörüne bağlanır.
- `tools/build-shell.ps1`: eklentiyi derler; sertifika verilirse paketi üretip imzalar (`-PackOnly` ile yalnızca üretir).
- `AfuZip.exe --register`: paket uygulama klasöründeyse ve imzası güvenilirse yeni menüyü de kurar. Kurulum programı
  bunu kendiliğinden çağırır.
- CI: paket kendinden imzalı bir test sertifikasıyla imzalanıp kuruluyor ve Explorer menüsünün ekran görüntüsü alınıyor.

## Yapılacaklar (SignPath Foundation)

[SignPath Foundation](https://signpath.org/) açık kaynak projelere ücretsiz kod imzalama sağlıyor.

1. signpath.org üzerinden başvurun: depo `MihrimatriX/afu-zip`, lisans MIT, derlemeler GitHub Actions'ta.
2. Onaydan sonra SignPath'te proje ve imza politikası oluşturulur; depo ayarlarına şunlar eklenir:
   - Sır: `SIGNPATH_API_TOKEN`
   - Değişkenler: `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`
   - Değişken: `SIGNPATH_PUBLISHER`: sertifikanın konusu, birebir (ör. `CN=SignPath Foundation, O=..., C=US`).
     Paket kimliğindeki Publisher bununla aynı olmazsa Windows paketi kurmaz.
3. `release.yml`'ye imza adımları eklenir: paket `-Publisher $SIGNPATH_PUBLISHER -PackOnly` ile üretilir, SignPath'e
   gönderilip imzalanır, kurulum programı imzalı paketle derlenir ve o da imzalanır.

İmzalı sürüm çıkınca kurulum programı Windows 11'de "AFU Zip" öğesini doğrudan sağ tık menüsüne ekler; Windows 10'da ve
imzasız kurulumda klasik menü kullanılmaya devam eder.
