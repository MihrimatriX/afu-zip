# Üçüncü taraf bileşenler

AFU Zip'in kendi kodu [MIT lisansı](LICENSE) ile dağıtılır. Aşağıdaki bileşenler kendi lisanslarıyla gelir.

## Uygulamayla dağıtılmayan, çalışma anında yüklenen

| Bileşen | Lisans | Not |
| --- | --- | --- |
| [7-Zip](https://www.7-zip.org/) (`7z.dll`) | GNU LGPL 2.1 veya sonrası, BSD 3-clause, unRAR kısıtı | Depoda ve derleme çıktısında yer almaz. Kullanıcının kurduğu 7-Zip veya [7-Zip ZS](https://github.com/mcmilk/7-Zip-zstd) klasöründen dinamik olarak yüklenir, bu yüzden LGPL'nin "kütüphaneyle bağlanan eser" hükmü kapsamındadır. |
| unRAR kodu (7-Zip içinde) | unRAR lisansı | RAR arşivleri yalnızca okunur. unRAR lisansı, kodun RAR sıkıştırma algoritmasını yeniden oluşturmak için kullanılmasını yasaklar; AFU Zip RAR oluşturmaz. |

İleride bir kurulum paketi `7z.dll`'yi içerirse, LGPL gereği 7-Zip'in lisans metni ve kaynak kodunun nereden alınacağı paketle birlikte verilecek.

## Uygulamayla dağıtılan

### .NET paketleri

| Paket | Lisans |
| --- | --- |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [Microsoft.Extensions.Hosting](https://github.com/dotnet/runtime) | MIT |
| [System.IO.Hashing](https://github.com/dotnet/runtime) | MIT |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | MIT |

### Önizleme web katmanı (WebView2 içinde)

| Paket | Lisans |
| --- | --- |
| [Monaco Editor](https://github.com/microsoft/monaco-editor) | MIT |
| [markdown-it](https://github.com/markdown-it/markdown-it) | MIT |
| [DOMPurify](https://github.com/cure53/DOMPurify) | Apache 2.0 veya MPL 2.0 (seçimli); AFU Zip Apache 2.0'ı seçer |

Microsoft Edge WebView2 Runtime, Windows 11 ile birlikte gelir ve uygulamayla dağıtılmaz.

## Yalnızca geliştirme ve test

| Paket | Lisans |
| --- | --- |
| [xUnit.net v3](https://github.com/xunit/xunit) | Apache 2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | Microsoft yazılım lisansı |
| [TypeScript](https://github.com/microsoft/TypeScript) | Apache 2.0 |
| [Vite](https://github.com/vitejs/vite) | MIT |

Tam lisans metinleri her paketin kendi deposunda ve NuGet/npm paketinin içindedir.
