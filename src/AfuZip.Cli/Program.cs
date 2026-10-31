using System.CommandLine;
using System.Diagnostics;
using System.Text;
using AfuZip.Cli;
using AfuZip.Core;
using AfuZip.Engine.Managed;
using AfuZip.Engine.SevenZip;

Console.OutputEncoding = Encoding.UTF8;

var sevenZip = new SevenZipEngine();
var managed = new ManagedEngine();

// --- Ortak seçenekler ---------------------------------------------------------------------------
var archiveArg = new Argument<FileInfo>("arşiv") { Description = "Arşiv dosyasının yolu" };
var passwordOpt = new Option<string?>("--password", "-p") { Description = "Arşiv parolası (verilmezse gerektiğinde sorulur)" };
var engineOpt = new Option<string>("--engine", "-e")
{
    Description = "Motor: auto, 7z veya managed",
    DefaultValueFactory = _ => "auto",
};
engineOpt.AcceptOnlyFromAmong("auto", "7z", "managed");

ArchiveOpener OpenerFor(ParseResult result) => new(result.GetValue(engineOpt) switch
{
    "7z" => new EngineRouter([sevenZip]),
    "managed" => new EngineRouter([managed]),
    _ => new EngineRouter([sevenZip, managed]),
});

OpenOptions OpenOptionsFor(ParseResult result) =>
    new(Passwords: new ConsolePasswordProvider(result.GetValue(passwordOpt)));

Progress<ArchiveProgress>? ConsoleProgress()
{
    // Çıktı bir dosyaya veya başka bir programa yönlendirildiyse canlı ilerleme satırı basılmaz.
    if (Console.IsOutputRedirected)
        return null;

    var lastPercent = -1;
    return new Progress<ArchiveProgress>(p =>
    {
        if (p.Fraction is not { } f) return;
        var percent = (int)(f * 100);
        if (percent == lastPercent) return;
        lastPercent = percent;
        var line = $"\r%{percent,3} {p.CurrentItem}";
        var width = Math.Max(Console.WindowWidth - 1, 1);
        Console.Write(line.Length > width ? line[..width] : line.PadRight(width));
    });
}

void EndProgressLine()
{
    if (!Console.IsOutputRedirected)
        Console.WriteLine();
}

// Arşiv açıldı ama sorunlu (eksik cilt, yarım indirme...): 7z.exe gibi stderr'e uyarı basılır.
void PrintWarnings(IArchiveReader reader)
{
    foreach (var warning in reader.Warnings)
        Console.Error.WriteLine($"UYARI: {warning}");
}

// --- afuzip formats ----------------------------------------------------------------------------------
var formats = new Command("formats", "Kullanılabilir motorları ve formatları listeler");
formats.SetAction(_ =>
{
    Console.WriteLine("managed  : ZIP, TAR, tar.gz, GZip (okuma + yazma + test)");
    if (!sevenZip.IsAvailable)
    {
        Console.WriteLine("7-zip    : 7z.dll bulunamadı. Aranan konumlar:");
        foreach (var path in SevenZipLibrary.CandidatePaths())
            Console.WriteLine($"           {path}");
        return 0;
    }

    Console.WriteLine($"7-zip    : {sevenZip.LibraryPath} ({sevenZip.Formats.Count} format)");
    foreach (var f in sevenZip.Formats.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"           {f.Name,-12} {(f.CanUpdate ? "okuma+yazma" : "okuma      ")}  {string.Join(' ', f.Extensions)}");
    return 0;
});

// --- afuzip sniff ------------------------------------------------------------------------------------
var sniff = new Command("sniff", "Dosyanın formatını içerik imzasından tespit eder") { archiveArg };
sniff.SetAction(result =>
{
    var file = result.GetValue(archiveArg)!;
    using var stream = file.OpenRead();
    var header = new byte[FormatSniffer.HeaderLength];
    var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
    var bySignature = FormatSniffer.Sniff(header.AsSpan(0, read));
    var byExtension = FormatSniffer.FromExtension(file.Name);

    Console.WriteLine($"İmzaya göre  : {bySignature.DisplayName()}");
    Console.WriteLine($"Uzantıya göre: {byExtension.DisplayName()}");
    if (bySignature != ArchiveFormat.Unknown && byExtension != ArchiveFormat.Unknown && bySignature != byExtension)
        Console.WriteLine("Uyarı: uzantı içerikle uyuşmuyor.");
    return 0;
});

// --- afuzip list -------------------------------------------------------------------------------------
var list = new Command("list", "Arşivin içeriğini listeler") { archiveArg, passwordOpt, engineOpt };
list.Aliases.Add("l");
list.SetAction(async (result, ct) =>
{
    var file = result.GetValue(archiveArg)!;
    var sw = Stopwatch.StartNew();
    await using var reader = await OpenerFor(result).OpenAsync(file.FullName, OpenOptionsFor(result), ct);
    var openTime = sw.Elapsed;

    Console.WriteLine($"{reader.DisplayName}");
    Console.WriteLine($"{reader.FormatName} · {reader.EngineName} motoru · {reader.Entries.Count:N0} girdi" +
                      (reader.IsSolid ? " · solid" : "") + $" · {openTime.TotalMilliseconds:N0} ms");
    PrintWarnings(reader);
    Console.WriteLine();
    Console.WriteLine($"{"Değiştirilme",-17} {"Boyut",10} {"Sıkışık",10} {"Oran",5} {"Yöntem",-14} Ad");
    Console.WriteLine(new string('-', 80));

    long size = 0, packed = 0;
    foreach (var e in reader.Entries)
    {
        size += e.Size;
        packed += e.PackedSize ?? 0;
        var modified = e.Modified?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
        var flags = (e.IsEncrypted ? "*" : "") + (e.IsSymlink ? "@" : "");
        Console.WriteLine(
            $"{modified,-17} {(e.IsDirectory ? "<KLASÖR>" : ByteSize.Format(e.Size)),10} " +
            $"{(e.PackedSize is { } p ? ByteSize.Format(p) : ""),10} {ByteSize.FormatRatio(e.Ratio),5} " +
            $"{e.Method ?? "",-14} {e.Path}{flags}");
    }

    Console.WriteLine(new string('-', 80));
    Console.WriteLine($"Toplam: {ByteSize.Format(size)}" + (packed > 0 ? $", sıkışık {ByteSize.Format(packed)}" : "") +
                      "   (* şifreli, @ sembolik link)");
    return 0;
});

// --- afuzip extract ----------------------------------------------------------------------------------
var outputOpt = new Option<DirectoryInfo?>("--output", "-o")
{
    Description = "Hedef klasör. Verilmezse akıllı çıkarma: tek kök klasör varsa arşivin yanına, yoksa arşiv adıyla yeni klasöre.",
};
var overwriteOpt = new Option<OverwritePolicy>("--overwrite")
{
    Description = "Var olan dosyalar: Overwrite, Skip, Rename",
    DefaultValueFactory = _ => OverwritePolicy.Rename,
};

var extract = new Command("extract", "Arşivi güvenli şekilde çıkarır") { archiveArg, outputOpt, passwordOpt, overwriteOpt, engineOpt };
extract.Aliases.Add("x");
extract.SetAction(async (result, ct) =>
{
    var file = result.GetValue(archiveArg)!;
    await using var reader = await OpenerFor(result).OpenAsync(file.FullName, OpenOptionsFor(result), ct);
    PrintWarnings(reader);

    var destination = result.GetValue(outputOpt)?.FullName
        ?? SmartExtract.ResolveDestination(file.Name, reader.Entries, file.DirectoryName!);
    var options = new ExtractionOptions(destination)
    {
        Overwrite = result.GetValue(overwriteOpt),
        Passwords = OpenOptionsFor(result).Passwords,
    };

    var sw = Stopwatch.StartNew();
    var outcome = await reader.ExtractAsync(null, options, ConsoleProgress(), ct);
    EndProgressLine();
    Console.WriteLine($"{outcome} ({ByteSize.Format(outcome.BytesWritten)}, {sw.Elapsed.TotalSeconds:N1} sn) → {destination}");

    foreach (var issue in outcome.Blocked)
        Console.WriteLine($"  ENGELLENDİ  {issue.EntryPath}: {issue.Reason}");
    foreach (var issue in outcome.Failed)
        Console.WriteLine($"  HATA        {issue.EntryPath}: {issue.Reason}");

    return outcome.Success ? 0 : 2;
});

// --- afuzip test -------------------------------------------------------------------------------------
var test = new Command("test", "Arşivi diske yazmadan açar ve CRC'leri doğrular") { archiveArg, passwordOpt, engineOpt };
test.Aliases.Add("t");
test.SetAction(async (result, ct) =>
{
    var file = result.GetValue(archiveArg)!;
    await using var reader = await OpenerFor(result).OpenAsync(file.FullName, OpenOptionsFor(result), ct);
    PrintWarnings(reader);

    var sw = Stopwatch.StartNew();
    var outcome = await reader.TestAsync(null, ConsoleProgress(), ct);
    EndProgressLine();
    Console.WriteLine($"{outcome} ({sw.Elapsed.TotalSeconds:N1} sn)");
    foreach (var issue in outcome.Failed)
        Console.WriteLine($"  HATA  {issue.EntryPath}: {issue.Reason}");
    // Başlık düzeyindeki sorunlar (eksik cilt, beklenmedik son) testi de başarısız sayar.
    return outcome.Success && reader.Warnings.Count == 0 ? 0 : 2;
});

// --- afuzip add --------------------------------------------------------------------------------------
var targetArg = new Argument<FileInfo>("hedef")
{
    Description = "Arşiv: .7z, .zip, .tar, .tar.gz, .tar.xz, .tar.bz2, .tar.zst, .wim; tek dosya için .gz/.xz/.bz2/.zst. Varsa içine eklenir.",
};
var sourcesArg = new Argument<string[]>("kaynaklar") { Description = "Dosya ve klasörler", Arity = ArgumentArity.OneOrMore };
var presetOpt = new Option<string?>("--preset", "-m") { Description = "fast, balanced, max, share, dev (verilmezse uzantıdan)" };
presetOpt.AcceptOnlyFromAmong([.. CompressionPreset.All.Select(p => p.Id)]);
var levelOpt = new Option<int?>("--level", "-x") { Description = "Sıkıştırma seviyesi: 0 (sakla) – 9 (zstd'de 1–22)" };
var headersOpt = new Option<bool>("--encrypt-headers", "-he") { Description = "7z: dosya adlarını da şifrele (parola gerekir)" };
var volumeOpt = new Option<string?>("--volume", "-v") { Description = "Ciltlere böl: 100m, 700m, 4g" };
var threadsOpt = new Option<int?>("--threads", "-mt") { Description = "İş parçacığı sayısı (verilmezse tüm çekirdekler)" };

var add = new Command("add", "Arşiv oluşturur; arşiv varsa dosyaları içine ekler")
{
    targetArg, sourcesArg, presetOpt, levelOpt, passwordOpt, headersOpt, volumeOpt, threadsOpt,
};
add.Aliases.Add("a");
add.SetAction(async (result, ct) =>
{
    var target = result.GetValue(targetArg)!;
    var sources = result.GetValue(sourcesArg)!;
    var password = result.GetValue(passwordOpt);
    var volume = result.GetValue(volumeOpt) is { } v ? ByteSize.Parse(v) : (long?)null;
    var sw = Stopwatch.StartNew();

    if (target.Exists && volume is null)
    {
        // 7-Zip'in "a" komutu gibi: var olan arşive ekle (aynı adlı dosyalar yenilenir).
        var name = target.Name.ToLowerInvariant();
        if (name.EndsWith(".gz") || name.EndsWith(".tgz") || name.EndsWith(".xz") || name.EndsWith(".bz2") || name.EndsWith(".zst"))
            throw new NotSupportedException($"{target.Name} tek akışlı bir sarmalayıcı; içine eklenemez. Yeniden oluşturun.");
        if (result.GetValue(headersOpt) || result.GetValue(threadsOpt) is not null)
            Console.Error.WriteLine("UYARI: -he ve -mt yalnızca yeni arşiv oluştururken kullanılır; var olan arşive eklerken yok sayıldı.");

        // Preset/seviye verildiyse yalnızca eklenen dosyalar için geçerli; verilmediyse arşivin biçiminin varsayılanı.
        var custom = result.GetValue(presetOpt) is not null || result.GetValue(levelOpt) is not null;
        var update = new ArchiveUpdate
        {
            Add = sources,
            Password = password,
            Preset = custom ? ResolvePreset(target.FullName, result.GetValue(presetOpt), result.GetValue(levelOpt)) : null,
        };
        using (var pending = await RequireSevenZip().PrepareUpdateAsync(target.FullName, update, OpenOptionsFor(result), ConsoleProgress(), ct))
        {
            EndProgressLine();
            pending.Commit();
        }
        target.Refresh();
        Console.WriteLine($"{target.Name} güncellendi: {ByteSize.Format(target.Length)} · {sw.Elapsed.TotalSeconds:N1} sn");
        return 0;
    }

    var preset = ResolvePreset(target.FullName, result.GetValue(presetOpt), result.GetValue(levelOpt));
    var needed = Capability.Write | (password is null ? Capability.None : Capability.Encrypt);
    var engine = new EngineRouter([sevenZip, managed]).Resolve(preset.Format, needed);
    if (volume is not null && engine != sevenZip)
        throw new NotSupportedException("Ciltlere bölme 7z.dll gerektirir (7-Zip kurun veya AFUZIP_7Z_DLL ayarlayın).");
    var request = new CreateRequest(target.FullName, sources, preset, password, result.GetValue(headersOpt), volume, result.GetValue(threadsOpt));
    await engine.CreateAsync(request, ConsoleProgress(), ct);
    EndProgressLine();

    var files = volume is null
        ? [target.FullName]
        : Directory.GetFiles(target.DirectoryName!, target.Name + ".*")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), $@"^{System.Text.RegularExpressions.Regex.Escape(target.Name)}\.\d{{3}}$"))
            .Order().ToArray();
    var size = files.Sum(f => new FileInfo(f).Length);
    var parts = files.Length > 1 ? $" · {files.Length} cilt" : "";
    Console.WriteLine($"{target.Name} oluşturuldu: {ByteSize.Format(size)}{parts} · {preset.DisplayName} ({preset.Method} {preset.Level}) · {engine.Name} · {sw.Elapsed.TotalSeconds:N1} sn");
    return 0;
});

// --- afuzip delete / rename ----------------------------------------------------------------------------
var pathsArg = new Argument<string[]>("yollar") { Description = "Arşiv içindeki dosya veya klasör yolları", Arity = ArgumentArity.OneOrMore };
var delete = new Command("delete", "Arşivden dosya veya klasör siler") { archiveArg, pathsArg, passwordOpt };
delete.Aliases.Add("d");
delete.SetAction(async (result, ct) =>
{
    var file = result.GetValue(archiveArg)!;
    var update = new ArchiveUpdate { Delete = result.GetValue(pathsArg)!, Password = result.GetValue(passwordOpt) };
    await CommitUpdateAsync(file.FullName, update, result, ct);
    Console.WriteLine($"{file.Name}: silindi.");
    return 0;
});

var pathArg = new Argument<string>("yol") { Description = "Arşiv içindeki dosya veya klasör" };
var newNameArg = new Argument<string>("yeni-ad") { Description = "Yeni ad (yalnızca ad, yol değil)" };
var rename = new Command("rename", "Arşivdeki dosya veya klasörü yeniden adlandırır") { archiveArg, pathArg, newNameArg, passwordOpt };
rename.Aliases.Add("rn");
rename.SetAction(async (result, ct) =>
{
    var file = result.GetValue(archiveArg)!;
    var update = new ArchiveUpdate
    {
        Rename = new Dictionary<string, string> { [result.GetValue(pathArg)!] = result.GetValue(newNameArg)! },
        Password = result.GetValue(passwordOpt),
    };
    await CommitUpdateAsync(file.FullName, update, result, ct);
    Console.WriteLine($"{file.Name}: yeniden adlandırıldı.");
    return 0;
});

SevenZipEngine RequireSevenZip() =>
    sevenZip.IsAvailable ? sevenZip : throw new NotSupportedException("Arşiv düzenleme 7z.dll gerektirir (7-Zip kurun veya AFUZIP_7Z_DLL ayarlayın).");

async Task CommitUpdateAsync(string archive, ArchiveUpdate update, ParseResult result, CancellationToken ct)
{
    using var pending = await RequireSevenZip().PrepareUpdateAsync(archive, update, OpenOptionsFor(result), ConsoleProgress(), ct);
    EndProgressLine();
    pending.Commit();
}

/// <summary>Biçim uzantıdan; --preset yöntem ve seviyeyi verir, --level seviyeyi ezer.</summary>
static CompressionPreset ResolvePreset(string target, string? presetId, int? level)
{
    var preset = CompressionPreset.ForPath(target);
    if (presetId is not null)
    {
        var chosen = CompressionPreset.All.First(p => p.Id == presetId);
        if (chosen.Format != preset.Format || chosen.TarWrapped != preset.TarWrapped)
            throw new ArgumentException($"'{chosen.DisplayName}' preset'i {(chosen.TarWrapped ? "tar." : "")}{chosen.Format.DisplayName()} üretir, hedef ise " +
                $"{Path.GetFileName(target)}. Uzantıyı değiştirin veya preset vermeyin.");
        preset = chosen;
    }
    return level is { } x ? preset with { Level = x } : preset;
}

// --- Kök komut ------------------------------------------------------------------------------------
var root = new RootCommand("AFU Zip — arşiv yöneticisi komut satırı")
{
    formats, sniff, list, extract, test, add, delete, rename,
};

try
{
    // Varsayılan işleyici yığın izi basar; beklenen hatalar (parola, desteklenmeyen biçim) tek satır gösterilsin.
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception ex) when (ex is ArchivePasswordException or InvalidDataException or NotSupportedException or AggregateException or IOException
                               or ArgumentException or FormatException or UnauthorizedAccessException or OverflowException
                               or OperationCanceledException)
{
    Console.Error.WriteLine($"Hata: {ex.Message}");
    if (ex is AggregateException agg)
        foreach (var inner in agg.InnerExceptions)
            Console.Error.WriteLine($"  - {inner.Message}");
    return 1;
}
