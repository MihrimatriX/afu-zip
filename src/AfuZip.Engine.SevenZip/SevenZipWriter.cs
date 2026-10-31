using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;
using AfuZip.Engine.SevenZip.Interop;

namespace AfuZip.Engine.SevenZip;

/// <summary>
/// Ayarların 7-Zip'in anladığı adlara çevrilmesi. 7z.exe "-m" önekini atar (-mx=9 → "x", -m0=LZMA2 → "0") ve
/// tamamen rakamdan oluşan değerleri sayı (VT_UI4) olarak gönderir; işleyiciler "x" gibi ayarlarda metin kabul etmez.
/// </summary>
internal static class SevenZipProperties
{
    public static IReadOnlyList<(string Name, string Value)> For(string handler, CompressionPreset preset, bool encrypted,
        bool encryptHeaders, int? threads)
    {
        var mt = threads is > 0 ? threads.Value.ToString() : "on";
        var method = preset.Method.Equals("Store", StringComparison.OrdinalIgnoreCase) ? "Copy" : preset.Method;
        var level = preset.Level.ToString();

        return handler.ToLowerInvariant() switch
        {
            "7z" =>
            [
                ("x", level),
                ("0", method), // ilk kodlayıcı (7z.exe: -m0=LZMA2)
                ("s", preset.Solid ? "on" : "off"),
                ("mt", mt),
                .. encrypted && encryptHeaders ? [("he", "on")] : Array.Empty<(string, string)>(),
            ],
            "zip" =>
            [
                ("m", method),
                ("x", level),
                ("mt", mt),
                ("cu", "on"), // adlar her zaman UTF-8: Türkçe karakterler başka araçlarda bozulmasın
                .. encrypted ? [("em", "AES256")] : Array.Empty<(string, string)>(),
            ],
            "xz" or "bzip2" or "zstd" => [("x", level), ("mt", mt)],
            "gzip" => [("x", level)],
            _ => [],
        };
    }

    /// <summary>İstenen yöntem bu 7z.dll'de yoksa (ana 7-Zip'te ZSTD yok) en yakın yedek.</summary>
    public static CompressionPreset? Fallback(string handler, CompressionPreset preset) => handler.ToLowerInvariant() switch
    {
        "7z" when !preset.Method.Equals("LZMA2", StringComparison.OrdinalIgnoreCase) =>
            preset with { Method = "LZMA2", Level = Math.Clamp(preset.Level <= 3 ? 1 : preset.Level, 0, 9) },
        "zip" when !preset.Method.Equals("Deflate", StringComparison.OrdinalIgnoreCase) =>
            preset with { Method = "Deflate", Level = Math.Clamp(preset.Level, 0, 9) },
        _ => null,
    };

    /// <summary>7z.exe'nin ParseNumberString'i gibi: yalnızca rakamsa sayı.</summary>
    public static bool TryParseNumber(string value, out uint number) =>
        uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);

    public static string HandlerFor(ArchiveFormat format) =>
        SevenZipLibrary.HandlerName(format) ?? throw new NotSupportedException($"{format.DisplayName()} biçiminde yazılamıyor.");
}

/// <summary>7z.dll ile arşiv oluşturma ve var olan arşivi güncelleme (IOutArchive.UpdateItems).</summary>
internal static class SevenZipWriter
{
    public static void Create(SevenZipLibrary library, CreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var sources = SourceCollector.Collect(request.Sources);
        if (sources.Count == 0)
            throw new ArgumentException("Sıkıştırılacak dosya yok.");

        var preset = request.Preset;
        if (preset.TarWrapped)
        {
            CreateTarWrapped(library, request, sources, progress, ct);
            return;
        }

        // gzip, xz, bzip2, zstd tek akış biçimleridir: yalnızca bir dosya taşır.
        if (preset.Format is ArchiveFormat.GZip or ArchiveFormat.Xz or ArchiveFormat.BZip2 or ArchiveFormat.Zstd &&
            (sources.Count != 1 || sources[0].IsDirectory))
            throw new ArgumentException($"{preset.DisplayName} yalnızca tek bir dosyayı sıkıştırır; klasörler için tar.{Path.GetExtension(request.ArchivePath).TrimStart('.')} kullanın.");

        // Planlayıcı aynı adlı iki kaynağı (C:\a\x.txt ve D:\b\x.txt) tek girdiye indirir.
        var items = ArchiveUpdatePlanner.Plan([], new ArchiveUpdate(), sources);
        using var target = new OutputTarget(request.ArchivePath, request.VolumeSize);
        Write(library, SevenZipProperties.HandlerFor(preset.Format), items, source: null, preset, request.Password,
            request.EncryptHeaders, request.Threads, target.Stream, progress, ct);
        target.Commit();
    }

    /// <summary>Değişiklikleri geçici dosyaya yazar; yerine koymak çağıranın işi (önce kendi okuyucularını kapatır).</summary>
    public static OutputTarget PrepareUpdate(SevenZipLibrary library, string archivePath, ArchiveUpdate update, OpenOptions options,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        // Komut satırından verilen parola, parola sağlayıcısının (diyalog) önüne geçer.
        if (update.Password is { } given)
            options = options with { Passwords = new StaticPasswordProvider(given) };

        var reader = SevenZipReader.Open(library, archivePath, options, ct);
        try
        {
            var handler = reader.FormatName;
            if (handler.ToLowerInvariant() is "gzip" or "bzip2" or "xz" or "zstd")
                throw new NotSupportedException($"{handler} tek dosyalık bir sarmalayıcı; içindeki arşiv yerinde düzenlenemez, yeniden oluşturun.");
            if (reader.IsMultiVolume || System.Text.RegularExpressions.Regex.IsMatch(archivePath, @"\.\d{3}$"))
                throw new NotSupportedException("Ciltlere bölünmüş arşivler düzenlenemez; önce tek dosya olarak yeniden oluşturun.");
            if (library.FindFormat(handler) is not { CanUpdate: true })
                throw new NotSupportedException($"{handler} arşivleri yalnızca okunabilir; düzenlenemez.");
            // Hatalı arşivi yeniden yazmak, okunamayan kısmı kalıcı olarak kaybettirir.
            if (reader.Warnings.Count > 0)
                throw new InvalidDataException($"Arşivde sorun var ({reader.Warnings[0]}); düzenlemeden önce test edin veya onarın.");

            var items = ArchiveUpdatePlanner.Plan(reader.Entries, update);
            var preset = update.Preset ?? DefaultPresetFor(handler, reader.IsSolid);
            var password = VerifyPassword(reader, ct) ?? update.Password;

            var target = new OutputTarget(archivePath);
            try
            {
                Write(library, handler, items, reader.InArchive, preset, password, reader.HeadersEncrypted, threads: null,
                    target.Stream, progress, ct);
                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        finally
        {
            // Okuyucu kapanınca arşivin dosya tanıtıcıları serbest kalır; Commit eskisinin üzerine yazabilir.
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Şifreli girdisi olan arşivde parola düzenlemeden önce doğrulanır: en küçük şifreli girdi çözülmeye çalışılır
    /// (gerekirse parola sorulur, yanlışsa ArchivePasswordException). Böylece eski blokları yeniden paketlemek
    /// mümkün olur, yeni dosyalar aynı parolayla şifrelenir ve yanlış yazılmış parola arşive karışmaz.
    /// </summary>
    private static string? VerifyPassword(SevenZipReader reader, CancellationToken ct)
    {
        var probe = reader.Entries.Where(e => e is { IsEncrypted: true, IsDirectory: false }).MinBy(e => e.Size);
        if (probe is null)
            return reader.KnownPassword;

        using (reader.OpenEntryAsync(probe.Index, ct).GetAwaiter().GetResult())
        {
        }
        return reader.KnownPassword;
    }

    private static CompressionPreset DefaultPresetFor(string handler, bool solid) => handler.ToLowerInvariant() switch
    {
        "7z" => CompressionPreset.Balanced with { Solid = solid },
        "zip" => CompressionPreset.Share,
        _ => new CompressionPreset(handler, handler, ArchiveFormat.Unknown, "Copy", 0, false, ""),
    };

    /// <summary>tar.gz, tar.xz, tar.zst: önce tar geçici dosyaya, sonra tek akış olarak sıkıştırılır.</summary>
    private static void CreateTarWrapped(SevenZipLibrary library, CreateRequest request, IReadOnlyList<SourceItem> sources,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var final = Path.GetFullPath(request.ArchivePath);
        var tarName = InnerTarName(Path.GetFileName(final));
        var tarPath = $"{final}.{Guid.NewGuid():N}.tar.tmp";
        var inputBytes = Math.Max(1, sources.Sum(s => s.Size));

        try
        {
            var tarPreset = new CompressionPreset("tar", "TAR", ArchiveFormat.Tar, "Copy", 0, false, "");
            using (var tar = new FileStream(tarPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                Write(library, "tar", ArchiveUpdatePlanner.Plan([], new ArchiveUpdate(), sources), null, tarPreset,
                    null, false, null, tar, Phase(progress, 0, inputBytes, 2 * inputBytes), ct);
            }

            var tarSize = new FileInfo(tarPath).Length;
            var item = new UpdateItem.Add(new SourceItem(tarPath, tarName, false, tarSize), tarName);
            using var target = new OutputTarget(final, request.VolumeSize);
            Write(library, SevenZipProperties.HandlerFor(request.Preset.Format), [item], null, request.Preset, null, false,
                request.Threads, target.Stream, Phase(progress, inputBytes, inputBytes, 2 * inputBytes), ct);
            target.Commit();
        }
        finally
        {
            try
            {
                File.Delete(tarPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>"yedek.tar.gz" → "yedek.tar", "yedek.tgz" → "yedek.tar".</summary>
    internal static string InnerTarName(string archiveName)
    {
        var lower = archiveName.ToLowerInvariant();
        foreach (var compound in new[] { ".tar.gz", ".tar.xz", ".tar.zst", ".tar.bz2", ".tar.lz4" })
        {
            if (lower.EndsWith(compound, StringComparison.Ordinal))
                return archiveName[..^(compound.Length - ".tar".Length)];
        }
        var stem = Path.GetFileNameWithoutExtension(archiveName);
        return stem + ".tar";
    }

    private static void Write(SevenZipLibrary library, string handler, IReadOnlyList<UpdateItem> items, IInArchive? source,
        CompressionPreset preset, string? password, bool encryptHeaders, int? threads, Stream output,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var format = library.FindFormat(handler) ?? throw new NotSupportedException($"Bu 7z.dll '{handler}' biçimini tanımıyor.");
        if (!format.CanUpdate)
            throw new NotSupportedException($"{format.Name} biçiminde arşiv oluşturulamaz (yalnızca okunur).");
        if (password is not null && handler is not ("7z" or "zip"))
            throw new NotSupportedException($"{format.Name} biçimi şifrelemeyi desteklemiyor; 7z veya zip seçin.");

        // Güncellemede aynı nesne kullanılır: 7-Zip eski girdileri sıkıştırılmış haliyle kopyalar.
        var created = source is null;
        var archive = source is not null
            ? (IOutArchive)(object)source
            : library.CreateOutArchive(format.ClassId);

        try
        {
            // Yöntemin varlığı SetProperties'te değil UpdateItems'ta denetlenir; bu yüzden önceden bakılır.
            if (library.EncoderMethods.Count > 0 && handler is "7z" or "zip" &&
                !library.EncoderMethods.Contains(preset.Method) && !preset.Method.Equals("Copy", StringComparison.OrdinalIgnoreCase) &&
                SevenZipProperties.Fallback(handler, preset) is { } fallback)
            {
                Debug.WriteLine($"[7-Zip] {preset.Method} bu 7z.dll'de yok; {fallback.Method} kullanılıyor.");
                preset = fallback;
            }

            ApplyProperties(archive, handler, preset, password, encryptHeaders, threads);

            var callback = new UpdateCallback(items, source, password, progress, ct);
            int hr;
            try
            {
                hr = archive.UpdateItems(new OutStreamSeekAdapter(output, callback.SetFault), (uint)items.Count, callback);
            }
            finally
            {
                callback.CloseOpenStreams();
            }

            if (callback.Fault is { } fault)
                ExceptionDispatchInfo.Throw(fault);
            ct.ThrowIfCancellationRequested();
            if (hr == unchecked((int)0x80070057))
                throw new NotSupportedException($"7z.dll bu ayarlarla yazamıyor ({handler}, {preset.Method} {preset.Level}; 0x{hr:X8}).");
            if (hr != HResult.S_OK)
                throw new IOException($"7-Zip arşivi yazamadı (0x{hr:X8}).");
            output.Flush();
        }
        finally
        {
            if (created && (object)archive is ComObject comObject)
                comObject.FinalRelease();
        }
    }

    private static void ApplyProperties(IOutArchive archive, string handler, CompressionPreset preset, string? password,
        bool encryptHeaders, int? threads)
    {
        var properties = SevenZipProperties.For(handler, preset, password is not null, encryptHeaders, threads);
        if (properties.Count == 0 || (object)archive is not ISetProperties setter)
            return;

        var hr = Set(setter, properties);
        if (hr != HResult.S_OK)
        {
            var text = string.Join(" ", properties.Select(p => $"-m{p.Name}={p.Value}"));
            throw new NotSupportedException($"7z.dll bu sıkıştırma ayarlarını kabul etmedi ({text}; 0x{hr:X8}).");
        }
    }

    private static unsafe int Set(ISetProperties setter, IReadOnlyList<(string Name, string Value)> properties)
    {
        var names = new nint[properties.Count];
        var values = new PropVariant[properties.Count];
        try
        {
            for (var i = 0; i < properties.Count; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUni(properties[i].Name);
                if (SevenZipProperties.TryParseNumber(properties[i].Value, out var number))
                {
                    values[i].VarType = PropVariant.VT_UI4;
                    values[i].UInt32 = number;
                }
                else
                {
                    values[i].VarType = PropVariant.VT_BSTR;
                    values[i].Pointer = Marshal.StringToBSTR(properties[i].Value);
                }
            }

            fixed (nint* namePointer = names)
            fixed (PropVariant* valuePointer = values)
                return setter.SetProperties(namePointer, valuePointer, (uint)properties.Count);
        }
        finally
        {
            for (var i = 0; i < properties.Count; i++)
            {
                if (names[i] != 0)
                    Marshal.FreeCoTaskMem(names[i]);
                fixed (PropVariant* value = &values[i])
                    NativeMethods.PropVariantClear(value);
            }
        }
    }

    /// <summary>İki aşamalı işlemde (tar + sıkıştırma) ilerlemeyi tek çubukta birleştirir.</summary>
    /// <param name="offset">Bu aşamadan önce tamamlanan pay.</param>
    /// <param name="phaseBytes">Bu aşamanın toplamdaki payı; 7-Zip'in kendi toplamı buna ölçeklenir.</param>
    private static IProgress<ArchiveProgress>? Phase(IProgress<ArchiveProgress>? inner, long offset, long phaseBytes, long total) =>
        inner is null ? null : new PhaseProgress(inner, offset, phaseBytes, total);

    private sealed class PhaseProgress(IProgress<ArchiveProgress> inner, long offset, long phaseBytes, long total)
        : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value)
        {
            var scale = value.TotalBytes > 0 ? (double)phaseBytes / value.TotalBytes : 1;
            inner.Report(new ArchiveProgress(offset + (long)(value.CompletedBytes * scale), total, value.CurrentItem));
        }
    }
}
