<p align="center">
  <img src="docs/assets/logo.svg" width="96" height="96" alt="AFU Zip logosu">
</p>

<h1 align="center">AFU Zip</h1>

<p align="center">
  Windows için modern arşiv yöneticisi: 7-Zip'in motoru, Explorer'ın tanıdıklığı, güçlü bir önizleyici.<br>
  <a href="https://mihrimatrix.github.io/afu-zip/"><b>Tanıtım sitesi</b></a> ·
  <a href="#hızlı-başlangıç">Hızlı başlangıç</a> ·
  <a href="#yol-haritası">Yol haritası</a>
</p>

<p align="center">
  <a href="https://github.com/MihrimatriX/afu-zip/actions/workflows/ci.yml"><img src="https://github.com/MihrimatriX/afu-zip/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/WinUI-3-0078D4" alt="WinUI 3">
  <img src="https://img.shields.io/badge/lisans-MIT-green" alt="MIT">
</p>

> **Geliştirme önizlemesi (v0.4).** Kurulum gerektirmeyen paketler [Releases](https://github.com/MihrimatriX/afu-zip/releases)
> sayfasında: zip'i açıp `AfuZip.exe`'yi çalıştırın. Hata bulursanız [issue açın](https://github.com/MihrimatriX/afu-zip/issues).

- **Her biçimi açar:** 7z, ZIP, RAR, TAR ve sıkıştırılmış türleri, ISO, CAB, WIM ve 7-Zip'in tanıdığı ~50 biçim; iç içe arşivler aynı sekmede.
- **Diske yazmadan önizler:** kod (Monaco), Markdown, JSON/XML ağacı, SVG, PDF, resim, video/ses, hex. Tür içerikten anlaşılır.
- **Amaç seçerek sıkıştırır:** Hızlı, Dengeli, Maksimum, Paylaşım, Geliştirici; boyut ve süre tahminiyle. AES-256, cilt bölme.
- **Arşivi yerinde düzenler:** ekle, sil, yeniden adlandır; yarıda kalan işlem eski arşivi bozmaz.
- **Windows ile bütünleşir:** Explorer sağ tık menüsü (buraya çıkar, klasöre çıkar, 7z/zip olarak sıkıştır), "Birlikte aç",
  Explorer'a sürükleyip bırakma, dışarıda düzenlenen dosyayı arşive geri yazma, görev çubuğunda ilerleme, bildirimler.
- **Güvenli varsayılanlar:** zip slip, zip bomb, ADS ve aygıt adlarına karşı korumalı çıkarma; Mark-of-the-Web korunur.

## Gereksinimler

- Windows 11, x64 veya ARM64
- .NET 10 SDK
- Visual Studio 2026 (WinUI 3 / "Windows application development" iş yükü) veya yalnızca `dotnet` CLI
- WebView2 Runtime (Windows 11'de hazır gelir): kod, Markdown, JSON/XML ve PDF önizlemesi için
- Node.js 22+: uygulama derlenirken önizleme web katmanı (`src/AfuZip.Preview.Web`) `npm` ile otomatik üretilir
- 7-Zip kurulu olmalı (`C:\Program Files\7-Zip\7z.dll`) ya da 7z.dll uygulamanın yanında durmalı.
  Zstd/Brotli/LZ4 için [7-Zip ZS](https://github.com/mcmilk/7-Zip-zstd) önerilir; o da otomatik bulunur.
  Farklı bir konum için `AFUZIP_7Z_DLL` ortam değişkenini ayarlayın.

## Hızlı başlangıç

```powershell
dotnet test --project tests/AfuZip.Tests     # Windows'ta 7z.dll testleri de çalışır
dotnet run --project src/AfuZip.App          # arayüz

dotnet run --project src/AfuZip.Cli -- formats              # motorlar ve 7z.dll'in tanıdığı formatlar
dotnet run --project src/AfuZip.Cli -- list ornek.7z         # içerik (tar.xz, .7z.001 otomatik açılır)
dotnet run --project src/AfuZip.Cli -- x ornek.rar           # akıllı çıkarma: arşivin yanına
dotnet run --project src/AfuZip.Cli -- x ornek.rar -o cikti  # belirli klasöre
dotnet run --project src/AfuZip.Cli -- t ornek.zip           # CRC testi, diske yazmaz
dotnet run --project src/AfuZip.Cli -- sniff supheli.pdf     # içerik imzasından format tespiti

# Oluşturma ve düzenleme (7z.dll ile)
dotnet run --project src/AfuZip.Cli -- a yedek.7z Belgeler                   # Dengeli preset (uzantıdan)
dotnet run --project src/AfuZip.Cli -- a yedek.7z Belgeler -m max -x 9       # preset ve seviye
dotnet run --project src/AfuZip.Cli -- a gizli.7z Belgeler -p "Parola" -he   # AES-256 + dosya adlarını şifrele
dotnet run --project src/AfuZip.Cli -- a buyuk.7z Videolar -v 4g             # ciltlere böl: buyuk.7z.001, .002…
dotnet run --project src/AfuZip.Cli -- a proje.tar.zst src                   # tar + zstd (7-Zip ZS gerekir)
dotnet run --project src/AfuZip.Cli -- a yedek.7z yeni.txt                   # arşiv varsa içine ekler
dotnet run --project src/AfuZip.Cli -- d yedek.7z Belgeler/eski              # dosya veya klasör sil
dotnet run --project src/AfuZip.Cli -- rn yedek.7z Belgeler/notlar.txt plan.txt
```

Parola gerekiyorsa CLI gizli girişle sorar; `--password` ile de verilebilir. Yanlışsa tekrar sorulur.

## Önizleme (Faz 2)

Seçilen dosya diske yazılmadan açılır ve türü uzantıdan değil **içerik imzasından** belirlenir
(yanlış uzantılı PNG de resim olarak görünür, `.ts` TypeScript ise kod, MPEG-TS ise video olur).

| İçerik | Görünüm |
| --- | --- |
| Kod ve metin | Monaco (VS Code editörü): ~80 dilde renklendirme, katlama, Ctrl+F, büyük dosyada ilk 5 MB |
| Markdown | İşlenmiş görünüm (kod blokları renkli, arşivdeki göreli resimler görünür) ↔ kaynak |
| JSON, XML | Açılır-kapanır ağaç; üzerine gelince yol (`$.oyuncular[3].ad`, `/Project/ItemGroup[2]`) ↔ kaynak |
| SVG | Damalı zeminde çizim (SVG'deki betikler çalışmaz) ↔ kaynak |
| PDF | WebView2'nin yerleşik PDF görüntüleyicisi |
| Resim | PNG, JPG, GIF (animasyonlu), BMP, WebP, ICO, TIFF, HEIC/AVIF (codec varsa); panele sığdır, çift tık %100 |
| Video ve ses | Windows'un oynatıcısı (MP4, MKV, WebM, MOV, MP3, FLAC, WAV, OGG...) |
| Diğer her şey | Sanallaştırılmış hex görünümü (GB'lık dosyada da anında) |

- **Metin kodlaması:** BOM → UTF-8 doğrulaması → BOM'suz UTF-16 → Windows-1254. Başlıktaki seçiciyle elle değiştirilebilir
  (Windows-1254, ISO-8859-9, DOS-857, UTF-16...); yeniden çıkarma yapılmaz.
- **Solid blok önbelleği:** solid 7z/RAR'da ilk önizlemede blok tek geçişte çözülür; aynı bloktaki sonraki dosyalar anında gelir.
  Önbellek 512 MB (küçükler bellekte, büyükler geçici klasörde), tüm sekmeler paylaşır.
- **Büyük ve şifreli dosyalar** seçilince hemen açılmaz: 64 MB üstü ve parolalı girdiler için "Önizle" düğmesi çıkar.
- **Güvenlik:** web katmanı sıkı CSP ile çalışır; arşivdeki HTML kod olarak gösterilir, Markdown'da ham HTML kapalıdır ve
  çıktı DOMPurify'dan geçer, uzak resimler (izleme pikselleri) yüklenmez, dış bağlantılar sistem tarayıcısında açılır.
- **Panel genişliği** ayraçla değişir; Alt+P paneli açar/kapatır.

## Oluşturma ve düzenleme (Faz 3)

- **Arşiv oluştur** (Ctrl+N, sekme çubuğundaki "Oluştur" ya da arşiv olmayan dosyaları pencereye sürükleyin):
  kullanıcı codec değil **amaç** seçer.

  | Preset | Biçim | Ne zaman |
  | --- | --- | --- |
  | Hızlı | 7z · ZSTD 3 (ana 7-Zip'te hızlı LZMA2) | Büyük dosyalar, günlük yedek |
  | Dengeli | 7z · LZMA2 5 · solid | Genel kullanım |
  | Maksimum | 7z · LZMA2 9 · solid | Arşivleme, dağıtım |
  | Paylaşım | zip · Deflate 6 | Karşı tarafın ne kullandığı bilinmiyorsa |
  | Geliştirici | tar.zst · 19 | Linux, CI, paketler (7-Zip ZS gerekir) |

  Seçim değişince **boyut ve süre tahmini** güncellenir; içerik zaten sıkıştırılmışsa (video, jpg, zip) uyarır.
  Gelişmiş ayarlarda biçim (7z, zip, tar, tar.gz/xz/bz2/zst), yöntem, seviye, solid, iş parçacığı ve
  ciltlere bölme (100 MB, 700 MB, FAT32 için 4 GB, özel) var.
- **Şifreleme:** AES-256 (7z ve zip); 7z'de dosya adları da şifrelenebilir. Parola hiçbir yere kaydedilmez.
- **Arşivi düzenleme** (7z, zip, tar, wim): Ekle (veya listeye sürükle), Sil (Del), Yeniden adlandır (F2).
  Klasör yeniden adlandırılınca içindekiler de taşınır. Değişmeyen girdiler yeniden sıkıştırılmaz.
  Aynı adlı dosya varsa sorulur (değiştir / atla / vazgeç). Şifreli arşivde parola düzenlemeden önce doğrulanır;
  yeni dosyalar aynı parolayla şifrelenir.
- **Güvenli yazma:** arşiv önce aynı klasörde geçici adla yazılır, diske tam yazıldıktan sonra eskisinin yerine konur
  (ReplaceFile: "internetten geldi" işareti ve izinler korunur); iptal veya hata eski arşivi bozmaz. Hatalı (eksik cilt,
  yarım indirme) ve ciltli arşivler düzenlenmez. Oluşturma diyaloğu var olan arşivin üzerine yazılacaksa uyarır.

## Windows entegrasyonu (Faz 4)

Uygulamada **Ayarlar → Explorer sağ tık menüsü** açılınca (yalnızca geçerli kullanıcı için, yönetici izni gerekmez):

| Nerede | Komutlar |
| --- | --- |
| Arşivlere sağ tık → **AFU Zip** | AFU Zip'te aç, Buraya çıkar (akıllı), Arşiv adıyla klasöre çıkar, Arşivi test et |
| Her dosya ve klasöre sağ tık → **AFU Zip ile sıkıştır** | Arşive ekle…, 7z olarak sıkıştır, zip olarak sıkıştır |
| "Birlikte aç" ve Ayarlar → Varsayılan uygulamalar | 23 arşiv uzantısı (zip tabanlı belgeler hariç) |

- Sağ tık işleri ana pencereyi açmaz: küçük bir ilerleme penceresi çıkar, iş bitince **"Klasörü aç"** düğmeli bir bildirim
  gösterip kapanır. Birden çok dosya seçip "7z olarak sıkıştır" denince tek arşiv oluşur.
- Windows 11'de klasik menü öğeleri "Daha fazla seçenek göster" altındadır; yeni menüye girmek paket kimliği (MSIX) ister.
- **Explorer'a sürükle:** listeden dosya veya klasörü masaüstüne/Explorer'a sürükleyin; bırakıldığı anda çıkarılır.
- **Dışarıda düzenle:** çift tıklayıp varsayılan programda açtığınız dosyayı kaydedince "Arşivde güncelle" çubuğu çıkar.
- **Görev çubuğu:** etkin işlerin ilerlemesi uygulama düğmesinde görünür (duraklatılınca sarı, hata olunca kırmızı).
- Komut satırından da: `AfuZip.exe --extract-here|--extract-folder|--test|--add|--compress <preset> yol...`

## Arayüz (Faz 1)

- **Sekmeler:** her arşiv ayrı sekmede; aynı arşiv ikinci kez açılırsa o sekmeye geçilir. Çoklu seçimle veya sürükleyerek birden fazla arşiv açılabilir.
- **Üç panel:** solda klasör ağacı ve arşiv bilgisi, ortada sıralanabilir liste (klasör boyutları dahil), sağda metin/resim önizlemesi.
- **İç içe arşivler:** arşivdeki bir arşive çift tıklayınca aynı sekmede açılır; breadcrumb `dış.zip › iç.tar › klasör` biçiminde, her parça tıklanabilir.
- **Arama:** arşivin tamamında, Türkçe büyük/küçük harf kurallarıyla.
- **Çıkarma:** buraya akıllı çıkar, seçilenleri buraya çıkar, seçilenleri/tümünü klasöre çıkar. Seçili klasörlerin tüm içeriği dahil edilir.
- **Test et:** CRC doğrulaması, hiçbir şey yazılmaz.
- **Sorunlu arşiv uyarısı:** eksik cilt, yarım indirme, hatalı başlık gibi durumlar sessizce geçilmez; durum satırında ve arşiv bilgisinde ⚠ ile gösterilir (CLI'da `UYARI:` satırı, `t` komutu hata koduyla döner).
- **Görev kuyruğu:** çıkarma ve test arka planda; ilerleme, hız, kalan süre, duraklat/devam/iptal. Görevler ayrı bir okuyucuyla çalışır, sekmede gezinmeyi bekletmez.
- **Parola diyaloğu:** gerektiğinde açılır, yanlışsa uyarıyla tekrar sorar; doğru parola oturum boyunca hatırlanır.
- **Varsayılan uygulamayla açma:** çift tıklanan dosya geçici klasöre çıkarılıp açılır. Programlar ve betikler güvenlik için doğrudan açılmaz.
- **Son açılanlar** karşılama ekranında.

| Kısayol | İşlev |
| --- | --- |
| Ctrl+O | Arşiv aç |
| Ctrl+N | Arşiv oluştur |
| Del | Seçilenleri arşivden sil (liste odaktayken) |
| F2 | Yeniden adlandır |
| Ctrl+W | Sekmeyi kapat |
| Ctrl+F | Arşivde ara |
| Ctrl+E | Buraya akıllı çıkar (seçim varsa seçilenleri) |
| Alt+P | Önizleme panelini aç/kapat (Explorer ile aynı) |
| Enter / çift tık | Klasöre gir, iç arşivi aç, dosyayı aç |
| Backspace, Alt+↑ | Yukarı (iç arşivin kökündeyse dış arşive döner) |

## Yapı

| Proje | Sorumluluk |
| --- | --- |
| `AfuZip.Core` | Modeller ve arayüzler, format tespiti, güvenli yol, çıkarma oturumu, akıllı çıkarma, klasör ağacı (sıralama, arama, klasör boyutları), iç içe arşivler, motor yönlendirme, görev kuyruğu, presetler |
| `AfuZip.Engine.SevenZip` | 7z.dll'i source-generated COM (`[GeneratedComInterface]`) ile C#'tan sürer: okuma, çıkarma, test, parola, çok ciltli arşivler; IOutArchive ile oluşturma ve güncelleme |
| `AfuZip.Engine.Managed` | Native bağımlılığı olmayan yedek motor: ZIP, TAR, tar.gz, GZip (okuma, yazma, CRC testi) |
| `AfuZip.Core/Editing`, `Compression` | Düzenleme planlayıcısı, presetler, boyut/süre tahmini, geçici dosya + ciltli yazma |
| `AfuZip.Core/Preview` | Önizleme: içerik türü tespiti, metin kodlaması, hex belgesi, LRU + solid blok önbelleği |
| `AfuZip.Preview.Web` | TypeScript + Vite: Monaco, Markdown (markdown-it + DOMPurify), JSON/XML ağacı, SVG; WebView2'de çalışır |
| `AfuZip.Cli` | `afuzip` komutu: formats, sniff, list, x, t, a, d, rn |
| `AfuZip.App` | WinUI 3 (Windows App SDK 2.6): sekmeler, önizleme paneli (WebView2, resim, medya, hex), görev kuyruğu, parola diyaloğu |
| `AfuZip.Tests` | xUnit v3: 188 test (162'si her platformda, 26'sı yalnızca Windows'ta 7z.dll ile) |

## Faz 1 çıkış kriteri

> 7z, ZIP, RAR, tar ailesi ve ISO açılır; 10 GB'lık arşiv ilerleme ve iptalle çıkarılır; kötü niyetli korpusun tamamı engellenir.

- [x] Kötü niyetli korpus (zip slip, symlink, ADS, aygıt adları, zip bomb, boyut limiti) her platformda engelleniyor
- [x] ZIP, TAR, tar.gz yönetilen motorla; iç içe arşiv ve `.zip.gz` otomatik açma test edildi
- [x] Görev kuyruğu: sıra, iptal, duraklatma, hata yakalama test edildi
- [ ] 7z, RAR, ISO, `.7z.001`, tar.xz, test modu, solid seçmeli çıkarma ve yarım arşiv uyarısı: `SevenZipEngineTests` Windows'ta çalıştırılacak
- [ ] Arayüz Windows'ta derlenip denenecek (XAML derleyicisi yalnızca Windows'ta çalışır)
- [ ] 10 GB'lık arşivle ilerleme/duraklatma/iptal elle denenecek

## Faz 2 çıkış kriteri

> Solid olmayan arşivde 20 MB altı bir dosya 300 ms içinde görünür; listedeki türlerin hepsi önizlenir.

- [x] Tür tespiti, metin kodlaması, hex, LRU ve solid blok önbelleği: birim testleri her platformda
- [x] Web katmanı (Monaco, Markdown, JSON/XML ağacı, SVG) headless Chromium'da açık/koyu temada denendi; CSP ihlali yok
- [ ] Solid 7z blok önbelleği ve `ReadEntriesAsync`: `SevenZipEngineTests.SolidBlock_IsCachedInOnePass` Windows'ta
- [ ] WebView2 entegrasyonu (sanal host, içerik sunucusu, PDF), medya ve resim yüzeyleri Windows'ta elle denenecek
- [ ] 300 ms hedefi: Debug çıktısında her önizleme için `[önizleme] … toplam N ms` satırı yazılır

## Faz 3 çıkış kriteri

> 7z, zip ve tar ailesi oluşturulur; AES-256 ve cilt bölme çalışır; arşiv içinde ekle/sil/yeniden adlandır yapılır
> ve yarıda kalan işlem var olan arşivi bozmaz.

- [x] Düzenleme planlayıcısı, geçici dosya + ciltli yazma (7z'nin başa dönüp başlık yazması dahil), 7-Zip ayar eşlemesi,
      preset ve tahmin: birim testleri her platformda
- [ ] `SevenZipEngineTests`: oluştur–aç–çıkar (7z, zip, tar, tar.gz), başlık şifreleme, zip AES, ciltler, uçtan uca
      sil → yeniden adlandır → ekle (+ `7z t`), şifreli arşivi düzenleme, commit edilmeyen güncelleme, ciltli arşivi
      düzenleme reddi — Windows'ta çalıştırılacak
- [ ] Oluşturma diyaloğu ve sekmedeki düzenleme komutları Windows'ta elle denenecek

## Güvenlik varsayılanları

Her motor çıkarmayı `ExtractionSession` üzerinden yapar:

- `..` içeren yollar reddedilir; mutlak yollar, `:` (ADS), `CON`/`NUL` gibi aygıt adları ve sondaki nokta/boşluklar zararsız hale getirilir
- Sembolik linkler oluşturulmaz
- Toplam boyut ve genişleme oranı sınırı (zip bomb); yarım kalan dosya silinir
- İnternetten gelen arşivin Mark-of-the-Web işareti çıkarılan dosyalara, iç içe arşivlere de aktarılır
- Çok ciltli arşivler yalnızca ilk cildin klasöründeki dosyaları cilt olarak açabilir

## Lisans

AFU Zip [MIT lisansı](LICENSE) ile dağıtılır. 7z.dll bu depoya eklenmez; kullanıcının 7-Zip kurulumundan değiştirilmeden,
dinamik olarak yüklenir (GNU LGPL 2.1 + unRAR kısıtı). RAR yalnızca okunur, çünkü unRAR lisansı kodun RAR sıkıştırıcısı
yapmak için kullanılmasını yasaklar. Kullanılan diğer paketler ve lisansları: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Yol haritası

| Faz | İçerik | Durum |
| --- | --- | --- |
| 0 | 7z.dll köprüsü (source-generated COM), yönetilen yedek motor, CLI | ✅ |
| 1 | Okuma: sekmeler, klasör ağacı, arama, iç içe arşivler, görev kuyruğu, parola | ✅ |
| 2 | Önizleme v1: Monaco, Markdown, JSON/XML, PDF, medya, hex, solid blok önbelleği | ✅ |
| 3 | Oluşturma ve düzenleme: presetler, tahmin, AES-256, ciltler, ekle/sil/yeniden adlandır | ✅ |
| 4 | Windows entegrasyonu: sağ tık menüsü, Birlikte aç, dışarı sürükle, dışarıda düzenle, bildirimler, görev çubuğu, taşınabilir paket | ✅ |
| 5 | Önizleme v2 ve eklentiler: font, 3D model, PE dosyaları, sistem önizleyicileri | sırada |
| 6 | Sertleştirme ve 1.0 yayını | |

### Sıradaki adım: Faz 5 (Önizleme v2)

Yazı tipi, 3D model (glTF/OBJ/FBX), PE dosyası (sürüm bilgisi, imza, içe aktarılan DLL'ler) önizlemeleri ve Windows'un kendi
önizleme işleyicileri (Office, Photoshop eklentileri). Sonra Faz 6'da MSIX paketi ve Windows 11'in yeni sağ tık menüsü.
