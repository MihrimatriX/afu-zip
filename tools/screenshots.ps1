# AFU Zip'in gerçek ekran görüntülerini alır (CI'daki Windows makinesinde): karşılama, ayarlar, oluşturma diyaloğu,
# arşiv sekmesi, menüler, koyu tema ve sağ tık ilerleme penceresi. UI Automation ile tıklar, ekrandan yakalar.
param([Parameter(Mandatory)] [string] $Exe, [string] $Out = "screenshots", [string] $Work = "shotwork")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out, $Work | Out-Null
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Native {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
      dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string d, int m, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int f);
}
"@
$UIA = [System.Windows.Automation.AutomationElement]

# Sanal ekran genelde 1024x768: mümkünse büyüt.
try {
  $dm = New-Object Native+DEVMODE; $dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)
  [Native]::EnumDisplaySettings($null, -1, [ref]$dm) | Out-Null
  $dm.dmPelsWidth = 1920; $dm.dmPelsHeight = 1080; $dm.dmFields = 0x80000 -bor 0x100000
  Write-Host "Ekran çözünürlüğü değişimi: $([Native]::ChangeDisplaySettings([ref]$dm, 0))"
} catch { Write-Host "Çözünürlük değiştirilemedi: $_" }
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "Ekran: $($screen.Width)x$($screen.Height)"

function Wait-Window($proc) {
  for ($i = 0; $i -lt 60; $i++) { $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { return $proc.MainWindowHandle }; Start-Sleep -Milliseconds 500 }
  throw "Pencere açılmadı"
}
function Fit($hwnd, $w = 1280, $h = 820) {
  $w = [Math]::Min($w, $screen.Width); $h = [Math]::Min($h, $screen.Height - 40)
  [Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $w, $h, 0x0040) | Out-Null
}
function Shot($hwnd, $name) {
  [Native]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 700
  $r = New-Object Native+RECT; [Native]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose(); Write-Host "Kaydedildi: $name"
}
function Find($hwnd, $name) {
  $root = $UIA::FromHandle($hwnd)
  $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
  for ($i = 0; $i -lt 30; $i++) { $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if ($el) { return $el }; Start-Sleep -Milliseconds 500 }
  throw "Arayüzde bulunamadı: $name"
}
# Öğenin kendisi ya da en yakın atası desteklediği desenle çalıştırılır (metin bloğu → düğme, liste öğesi).
function Act($el, $pattern) {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  for ($e = $el; $e; $e = $walker.GetParent($e)) {
    $p = $null
    if ($e.TryGetCurrentPattern($pattern, [ref]$p)) { return $p }
  }
  throw "Desen yok: $pattern"
}
function Click($hwnd, $name) { (Act (Find $hwnd $name) ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Select-Entry($hwnd, $name) { (Act (Find $hwnd $name) ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
function Keys($hwnd, $keys) { [Native]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait($keys) }

# --- Örnek arşiv -------------------------------------------------------------------------------
$src = Join-Path $Work "proje"; New-Item -ItemType Directory -Force "$src/assets" | Out-Null
@"
# Neon Sokaklar

Siberpunk bir şehirde geçen aksiyon oyunu. Derlemek için ``dotnet build`` yeterli.

| Platform | Durum |
| --- | --- |
| Windows | Oynanabilir |
| Steam Deck | Test ediliyor |

- Hareket ve kamera sistemi
- Görev kaydı (JSON)
"@ | Set-Content -Encoding utf8 "$src/README.md"
@"
namespace Oyun;

/// Oyuncunun konumu ve hareketi.
public sealed class Oyuncu(string ad)
{
    const float Hiz = 5.5f;

    public string Ad => ad;
    public Vector3 Konum { get; set; }

    public void Ilerle(Vector3 yon, float dt) => Konum += yon * Hiz * dt;
}
"@ | Set-Content -Encoding utf8 "$src/Oyuncu.cs"
'{ "oyun": "Neon Sokaklar", "surum": "0.9.1", "ayarlar": { "cozunurluk": [2560, 1440], "vsync": true, "ses": null }, "oyuncular": [ { "ad": "Ada", "seviye": 12 }, { "ad": "Kerem", "seviye": 7 } ] }' | Set-Content -Encoding utf8 "$src/kayit.json"
Copy-Item docs/assets/og.png "$src/kapak.png"
Copy-Item docs/assets/logo.svg "$src/assets/logo.svg"
# Gerçekçi görünsün diye birkaç klasör ve dosya daha
New-Item -ItemType Directory -Force "$src/src/Oyun", "$src/src/Arayuz", "$src/assets/dokular", "$src/docs" | Out-Null
foreach ($n in 'Oyuncu', 'Dusman', 'Silah', 'Gorev', 'Kamera', 'Kayit') { Get-Content "$src/Oyuncu.cs" | Set-Content -Encoding utf8 "$src/src/Oyun/$n.cs" }
foreach ($n in 'AnaMenu', 'Envanter', 'Harita') { Get-Content "$src/Oyuncu.cs" | Set-Content -Encoding utf8 "$src/src/Arayuz/$n.cs" }
foreach ($n in 'sokak', 'neon', 'gokyuzu', 'zemin') { Copy-Item docs/assets/og.png "$src/assets/dokular/$n.png" }
Get-Content "$src/README.md" | Set-Content -Encoding utf8 "$src/docs/TASARIM.md"
$archive = Join-Path (Resolve-Path $Work) "proje.7z"
Push-Location $src; & "C:\Program Files\7-Zip\7z.exe" a $archive * | Out-Null; Pop-Location

# --- 1-3: karşılama, ayarlar, oluşturma ---------------------------------------------------------
$p = Start-Process -FilePath $Exe -PassThru
$h = Wait-Window $p; Fit $h; Start-Sleep -Seconds 3
Shot $h "01-karsilama"
# Ayarlar bir sekmede açılır; Ctrl+W ile kapanır.
try { Click $h "Ayarlar"; Start-Sleep -Seconds 2; Shot $h "02-ayarlar"; Keys $h "^w"; Start-Sleep -Seconds 1 } catch { Write-Host "Ayarlar: $_" }
try { Keys $h "^n"; Start-Sleep -Seconds 2; Shot $h "03-olustur"; Keys $h "{ESC}" } catch { Write-Host "Oluştur: $_" }
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(15000) | Out-Null

# --- 4-7: arşiv, sağ tık, ⋯ menüsü, test bildirimi ----------------------------------------------
$p = Start-Process -FilePath $Exe -ArgumentList "`"$archive`"" -PassThru
$h = Wait-Window $p; Fit $h; Start-Sleep -Seconds 5
Shot $h "04-arsiv"
# Sağ tık menüsü (klavyeden Shift+F10)
try { $el = Find $h "Oyuncu.cs"; (Act $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); $el.SetFocus(); Keys $h "+{F10}"; Start-Sleep -Seconds 1; Shot $h "05-sag-tik"; Keys $h "{ESC}" } catch { Write-Host "Sağ tık: $_" }
try { Click $h "Diğer"; Start-Sleep -Seconds 1; Shot $h "06-menu"; Keys $h "{ESC}" } catch { Write-Host "Menü: $_" }
# Test bitince alttaki kısa bildirim
try { Click $h "Test et"; Start-Sleep -Seconds 3; Shot $h "07-test" } catch { Write-Host "Test: $_" }
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(15000) | Out-Null

# --- 8: koyu tema, klasör ağacı ve tüm sütunlar açık (ayar dosyası elle yazılır) --------------------
$settings = Join-Path $env:LOCALAPPDATA "AfuZip\settings.json"
New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
'{ "Theme": "Dark", "ShowFolderTree": true, "ShowRatioColumn": true, "ShowMethodColumn": true, "ShowPackedColumn": true }' | Set-Content -Encoding utf8 $settings
$p = Start-Process -FilePath $Exe -ArgumentList "`"$archive`"" -PassThru
$h = Wait-Window $p; Fit $h; Start-Sleep -Seconds 5
try { Select-Entry $h "src"; Start-Sleep -Seconds 1; Shot $h "08-koyu" } catch { Write-Host "Koyu: $_"; Shot $h "08-koyu" }
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(15000) | Out-Null
Remove-Item $settings -ErrorAction SilentlyContinue

# --- 10-13: Explorer sağ tık menüsü: Windows 11 menüsü ve klasik menü ("Daha fazla seçenek göster") ---------------
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  public static void RightClick(int x, int y) { SetCursorPos(x, y); mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); }
  public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
  // Shift + sağ tık: Windows 11'de doğrudan klasik menü ("Daha fazla seçenek göster").
  public static void ShiftRightClick(int x, int y) { keybd_event(0x10, 0, 0, IntPtr.Zero); RightClick(x, y); keybd_event(0x10, 0, 2, IntPtr.Zero); }
}
"@
function ShotScreen($name) {
  $b = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
  $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $b.Save((Join-Path $Out "$name.png")); $g.Dispose(); $b.Dispose(); Write-Host "Kaydedildi: $name"
}
# Açık menüdeki "AFU Zip" öğesine tıklayıp alt menüsünü açar (yalnızca fareyle üzerine gelmek XAML menüsünde yetmiyor).
function Open-AfuZip($name) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'AFU Zip')),
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
  $item = $null
  for ($i = 0; $i -lt 10 -and -not $item; $i++) { $item = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if (-not $item) { Start-Sleep -Milliseconds 500 } }
  if (-not $item) { Write-Host "Menüde 'AFU Zip' bulunamadı ($name)"; ShotScreen $name; return }
  $r = $item.Current.BoundingRectangle
  Write-Host "AFU Zip menü öğesi ($name): $r"
  [Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
  Start-Sleep -Seconds 2
  ShotScreen $name
  # Alt menüdeki başlıklar (eklentiden gelen dinamik adlar) günlüğe
  $items = $UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
  Write-Host ("Menü öğeleri: " + (($items | ForEach-Object { $_.Current.Name }) -join ' | '))
}
try {
  $reg = Start-Process -FilePath $Exe -ArgumentList '--register' -PassThru -Wait
  Write-Host "--register çıkış kodu: $($reg.ExitCode)"
  Get-AppxPackage -Name AfuZip.ContextMenu | Format-List Name, Version, InstallLocation | Out-String | Write-Host
  # Uzantılar görünsün (proje klasörü ile proje.7z karışmasın); Explorer yeni paket kaydını okusun diye yeniden başlar.
  Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' HideFileExt 0
  Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 5
  if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe; Start-Sleep -Seconds 5 }
  Start-Process explorer.exe (Resolve-Path $Work).Path
  $cabinet = New-Object System.Windows.Automation.PropertyCondition($UIA::ClassNameProperty, 'CabinetWClass')
  $win = $null
  for ($i = 0; $i -lt 30 -and -not $win; $i++) { $win = $UIA::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cabinet); if (-not $win) { Start-Sleep -Milliseconds 500 } }
  if (-not $win) { throw "Explorer penceresi açılmadı" }
  $eh = [IntPtr]$win.Current.NativeWindowHandle
  [Native]::SetWindowPos($eh, [IntPtr]::Zero, 40, 40, 1100, 720, 0x0040) | Out-Null
  Start-Sleep -Seconds 2
  $file = Find $eh 'proje.7z'
  (Act $file ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
  $r = $file.Current.BoundingRectangle
  [Native]::SetForegroundWindow($eh) | Out-Null; Start-Sleep -Milliseconds 500
  $x = [int]($r.X + 40); $y = [int]($r.Y + $r.Height / 2)
  [Mouse]::RightClick($x, $y)
  Start-Sleep -Seconds 2
  ShotScreen '10-explorer-menu'
  Open-AfuZip '11-explorer-alt-menu'
  Keys $eh '{ESC}{ESC}{ESC}'; Start-Sleep -Seconds 1
  # Klasik menü ("Daha fazla seçenek göster" ile aynı): Shift + sağ tık
  [Native]::SetForegroundWindow($eh) | Out-Null; Start-Sleep -Milliseconds 500
  [Mouse]::ShiftRightClick($x, $y)
  Start-Sleep -Seconds 2
  ShotScreen '12-explorer-klasik'
  Open-AfuZip '13-explorer-klasik-alt'
  Keys $eh '{ESC}{ESC}{ESC}'
  try { ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() } catch { }
} catch { Write-Host "Explorer: $_"; ShotScreen '10-explorer-hata' }

# --- 9: sağ tık "klasöre çıkar" ilerleme penceresi ----------------------------------------------
# PPMd açması yavaş: pencere birkaç saniye görünsün diye büyük, metin benzeri bir arşiv.
$bigTxt = (Join-Path $Work 'buyuk.txt') -replace '\\', '/'
python -c "import random; w=['arsiv','oyun','seviye','kaplama','ses','model','sahne','kod','veri','harita']; r=random.Random(1); open('$bigTxt','w').write(' '.join(r.choice(w)+str(r.randrange(999)) for _ in range(15000000)))"
& "C:\Program Files\7-Zip\7z.exe" a "$Work/buyuk.7z" "$Work/buyuk.txt" -m0=PPMd -mx=9 | Out-Null
$p = Start-Process -FilePath $Exe -ArgumentList "--extract-folder", "`"$(Resolve-Path "$Work/buyuk.7z")`"" -PassThru
try { $h = Wait-Window $p; Start-Sleep -Milliseconds 2500; Shot $h "09-ilerleme" } catch { Write-Host "İlerleme: $_" }
$p.WaitForExit(180000) | Out-Null
Get-ChildItem $Out | Format-Table Name, Length
$log = Join-Path $env:LOCALAPPDATA "AfuZip\logs\afuzip.log"
if (Test-Path $log) { Write-Host "--- Uygulama günlüğü ---"; Get-Content $log | Write-Host }
