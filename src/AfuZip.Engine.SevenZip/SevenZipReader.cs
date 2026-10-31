using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;
using AfuZip.Engine.SevenZip.Interop;

namespace AfuZip.Engine.SevenZip;

/// <summary>
/// 7z.dll ile açılmış bir arşiv. IInArchive thread-safe olmadığı için tüm erişimler tek bir kapıdan geçer.
/// </summary>
internal sealed class SevenZipReader : IArchiveReader
{
    private readonly IInArchive _archive;
    private readonly FileStream _file;
    private readonly IReadOnlyList<FileStream> _volumes;
    private readonly IPasswordProvider? _passwords;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _password;

    private SevenZipReader(string path, SevenZipFormat format, IInArchive archive, FileStream file,
        IReadOnlyList<FileStream> volumes, IReadOnlyList<string> missingVolumes, IPasswordProvider? passwords, string? password,
        bool headersEncrypted)
    {
        HeadersEncrypted = headersEncrypted;
        _volumes = volumes;
        ArchivePath = path;
        FormatName = format.Name;
        Format = FormatFromHandler(format.Name);
        _archive = archive;
        _file = file;
        _passwords = passwords;
        _password = password;
        Entries = ReadEntries();
        IsSolid = ReadArchiveProperty(ItemPropId.Solid, static v => v.AsBool()) ?? false;
        Warnings = ReadWarnings(missingVolumes);
    }

    public string ArchivePath { get; }
    public ArchiveFormat Format { get; }
    public string FormatName { get; }
    public string EngineName => "7-zip";
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool IsSolid { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool SupportsBatchRead => true;

    /// <summary>Arşiv açılırken parola istendi: 7z'de dosya adları da şifreli (başlık şifreleme).</summary>
    internal bool HeadersEncrypted { get; }

    /// <summary>Güncelleme (IOutArchive) aynı nesne üzerinden yapılır; yalnızca yazıcı kullanır.</summary>
    internal IInArchive InArchive => _archive;

    internal string? KnownPassword => _password;

    /// <summary>Arşiv birden çok dosyadan (cilt) okunuyor.</summary>
    internal bool IsMultiVolume => _volumes.Count > 0;

    public static unsafe SevenZipReader Open(SevenZipLibrary library, string path, OpenOptions options, CancellationToken ct)
    {
        var header = new byte[64 * 1024];
        int headerLength;
        using (var probe = File.OpenRead(path))
            headerLength = probe.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        var candidates = library.CandidatesFor(path, options.FormatHint, header.AsSpan(0, headerLength));
        if (candidates.Count == 0)
            throw new InvalidDataException($"7-Zip '{Path.GetFileName(path)}' için uygun bir format işleyicisi bulamadı.");

        foreach (var format in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            IInArchive? archive = null;
            var callback = new OpenCallback(path, options.Passwords, ct);
            try
            {
                archive = library.CreateInArchive(format.ClassId);
                ulong maxCheckStart = 1 << 20; // SFX gibi önünde veri olan arşivler için tarama sınırı
                var hr = archive.Open(new InStreamAdapter(file), &maxCheckStart, callback);

                if (hr == HResult.S_OK)
                    return new SevenZipReader(path, format, archive, file, [.. callback.Volumes], [.. callback.MissingVolumes],
                        options.Passwords, callback.PasswordUsed, headersEncrypted: callback.PasswordRequested);

                ct.ThrowIfCancellationRequested();
                if (callback.Fault is { } fault)
                    ExceptionDispatchInfo.Throw(fault);
                if (callback.PasswordRequested)
                {
                    if (callback.PasswordUsed is not null)
                        options.Passwords?.OnPasswordRejected(path);
                    throw new ArchivePasswordException(path, wasProvided: callback.PasswordUsed is not null);
                }
            }
            catch
            {
                Release(archive);
                callback.DisposeVolumes();
                file.Dispose();
                throw;
            }

            archive.Close();
            Release(archive);
            callback.DisposeVolumes();
            file.Dispose();
        }

        throw new InvalidDataException(
            $"7-Zip '{Path.GetFileName(path)}' dosyasını açamadı (denenen: {string.Join(", ", candidates.Select(c => c.Name))}).");
    }

    public async Task<Stream> OpenEntryAsync(int index, CancellationToken ct = default)
    {
        var entry = Entries[index];
        if (entry.IsDirectory)
            throw new InvalidOperationException($"'{entry.Path}' bir klasör.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var sink = new SingleEntrySink(entry);
                RunExtract([(uint)index], testMode: 0, sink, _passwords, progress: null, ct);
                return sink.TakeStream(ArchivePath);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tek Extract çağrısı: solid blok bir kez çözülür, istenen her girdi alıcıya yazılır.</summary>
    public async Task ReadEntriesAsync(IReadOnlyCollection<int> indices, IEntryReceiver receiver,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateIndices(indices);
        var sorted = indices.Distinct().Order().Select(i => (uint)i).ToArray();
        if (sorted.Length == 0)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => RunExtract(sorted, testMode: 0, new ReceiverSink(receiver, Entries), _passwords, progress, ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionResult> ExtractAsync(IReadOnlyCollection<int>? indices, ExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ValidateIndices(indices);

                var session = new ExtractionSession(ArchivePath, options);
                var sorted = indices?.Distinct().Order().Select(i => (uint)i).ToArray();
                RunExtract(sorted, testMode: 0, new FileSystemSink(session, Entries), options.Passwords ?? _passwords, progress, ct);
                return session.Result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TestResult> TestAsync(IReadOnlyCollection<int>? indices = null, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ValidateIndices(indices);
                var result = new TestResult();
                var sorted = indices?.Distinct().Order().Select(i => (uint)i).ToArray();
                RunExtract(sorted, testMode: 1, new TestSink(result, Entries), _passwords, progress, ct);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // 7z.dll indeksleri sınır kontrolü yapmadan kullanır; geçersiz indeks native bellek okuması demektir.
    private void ValidateIndices(IReadOnlyCollection<int>? indices)
    {
        if (indices is not null && indices.Any(i => i < 0 || i >= Entries.Count))
            throw new ArgumentOutOfRangeException(nameof(indices), "Arşivde olmayan bir girdi indeksi istendi.");
    }

    /// <param name="testMode">0 = çıkar, 1 = test (veri açılır, CRC doğrulanır, yazılmaz).</param>
    private unsafe void RunExtract(uint[]? indices, int testMode, IExtractSink sink, IPasswordProvider? passwords,
        IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        bool[]? requested = null;
        if (indices is not null)
        {
            requested = new bool[Entries.Count];
            foreach (var i in indices)
                requested[i] = true;
        }

        var callback = new ExtractCallback(sink, ArchivePath, requested, passwords, _password, progress, ct);
        int hr;
        if (indices is null)
        {
            hr = _archive.Extract(null, uint.MaxValue, testMode, callback);
        }
        else
        {
            fixed (uint* p = indices)
                hr = _archive.Extract(p, (uint)indices.Length, testMode, callback);
        }
        callback.Cleanup();

        if (callback.Fault is { } fault)
            ExceptionDispatchInfo.Throw(fault);
        ct.ThrowIfCancellationRequested();
        if (callback.PasswordRequested && callback.PasswordUsed is null)
            throw new ArchivePasswordException(ArchivePath, wasProvided: false);
        if (hr != HResult.S_OK)
            throw new IOException($"7-Zip çıkarma işlemi başarısız (0x{hr:X8}).");

        // Yanlış parolada Extract yine S_OK döner; hata girdi bazında gelir. Parolayı yalnızca
        // hiçbir girdi parola hatası vermediyse hatırlıyoruz, aksi halde bir sonraki işlemde tekrar sorulur.
        if (callback.PasswordFailed)
        {
            _password = null;
            passwords?.OnPasswordRejected(ArchivePath);
        }
        else if (callback.PasswordUsed is not null)
            _password = callback.PasswordUsed;
    }

    /// <summary>
    /// Open başarılı olsa bile 7-Zip sorunları arşiv özelliklerinde bildirir (7z.exe'nin "ERRORS:" ve
    /// "WARNINGS:" satırları). Bunları okumazsak eksik ciltli veya yarım indirilmiş arşiv sessizce eksik listelenir.
    /// </summary>
    private List<string> ReadWarnings(IReadOnlyList<string> missingVolumes)
    {
        var warnings = new List<string>();
        var errorFlags = (ArchiveErrorFlags)(ReadArchiveProperty(ItemPropId.ErrorFlags, static v => v.AsUInt64()) ?? 0);
        var warningFlags = (ArchiveErrorFlags)(ReadArchiveProperty(ItemPropId.WarningFlags, static v => v.AsUInt64()) ?? 0);

        warnings.AddRange(DescribeFlags(errorFlags));
        if (ReadArchiveProperty(ItemPropId.Error, static v => v.AsString()) is { Length: > 0 } error)
            warnings.Add(error);
        warnings.AddRange(DescribeFlags(warningFlags & ~errorFlags));
        if (ReadArchiveProperty(ItemPropId.Warning, static v => v.AsString()) is { Length: > 0 } warning)
            warnings.Add(warning);

        if ((errorFlags | warningFlags).HasFlag(ArchiveErrorFlags.UnexpectedEnd) && missingVolumes.Count > 0)
            warnings.Add($"Bulunamayan cilt: {string.Join(", ", missingVolumes.Distinct())}");

        return [.. warnings.Distinct()];
    }

    private static IEnumerable<string> DescribeFlags(ArchiveErrorFlags flags)
    {
        if (flags.HasFlag(ArchiveErrorFlags.UnexpectedEnd))
            yield return "Arşiv beklenmedik şekilde bitiyor (eksik cilt veya yarım indirme); liste eksik olabilir";
        if (flags.HasFlag(ArchiveErrorFlags.HeadersError))
            yield return "Arşiv başlıkları hatalı";
        if (flags.HasFlag(ArchiveErrorFlags.EncryptedHeadersError))
            yield return "Şifreli başlıklar çözülemedi";
        if (flags.HasFlag(ArchiveErrorFlags.UnavailableStart))
            yield return "Arşivin başı eksik";
        if (flags.HasFlag(ArchiveErrorFlags.UnconfirmedStart))
            yield return "Arşivin başı doğrulanamadı";
        if (flags.HasFlag(ArchiveErrorFlags.DataAfterEnd))
            yield return "Arşivin sonunda fazladan veri var";
        if (flags.HasFlag(ArchiveErrorFlags.UnsupportedMethod))
            yield return "Desteklenmeyen sıkıştırma yöntemi";
        if (flags.HasFlag(ArchiveErrorFlags.UnsupportedFeature))
            yield return "Desteklenmeyen arşiv özelliği";
        if (flags.HasFlag(ArchiveErrorFlags.DataError))
            yield return "Veri hatası";
        if (flags.HasFlag(ArchiveErrorFlags.CrcError))
            yield return "CRC hatası";
    }

    private List<ArchiveEntry> ReadEntries()
    {
        _archive.GetNumberOfItems(out var count);
        var entries = new List<ArchiveEntry>((int)count);
        var fallbackName = DefaultItemName(ArchivePath);

        for (uint i = 0; i < count; i++)
        {
            var path = ReadProperty(i, ItemPropId.Path, static v => v.AsString())
                ?? ReadProperty(i, ItemPropId.Name, static v => v.AsString());
            if (string.IsNullOrEmpty(path))
                path = count == 1 ? fallbackName : $"[{i}]";

            entries.Add(new ArchiveEntry(
                Index: (int)i,
                Path: ArchiveEntry.NormalizePath(path),
                IsDirectory: ReadProperty(i, ItemPropId.IsDir, static v => v.AsBool()) ?? false,
                Size: (long)(ReadProperty(i, ItemPropId.Size, static v => v.AsUInt64()) ?? 0),
                PackedSize: (long?)ReadProperty(i, ItemPropId.PackSize, static v => v.AsUInt64()),
                Modified: ReadProperty(i, ItemPropId.MTime, static v => v.AsFileTime()),
                Crc: (uint?)ReadProperty(i, ItemPropId.Crc, static v => v.AsUInt64()),
                Method: ReadProperty(i, ItemPropId.Method, static v => v.AsString()),
                IsEncrypted: ReadProperty(i, ItemPropId.Encrypted, static v => v.AsBool()) ?? false,
                IsSymlink: !string.IsNullOrEmpty(ReadProperty(i, ItemPropId.SymLink, static v => v.AsString())),
                Block: (int?)ReadProperty(i, ItemPropId.Block, static v => v.AsUInt64())));
        }
        return entries;
    }

    private unsafe T? ReadProperty<T>(uint index, uint propId, Func<PropVariant, T?> map)
    {
        PropVariant value = default;
        try
        {
            return _archive.GetProperty(index, propId, &value) == HResult.S_OK ? map(value) : default;
        }
        finally
        {
            NativeMethods.PropVariantClear(&value);
        }
    }

    private unsafe T? ReadArchiveProperty<T>(uint propId, Func<PropVariant, T?> map)
    {
        PropVariant value = default;
        try
        {
            return _archive.GetArchiveProperty(propId, &value) == HResult.S_OK ? map(value) : default;
        }
        finally
        {
            NativeMethods.PropVariantClear(&value);
        }
    }

    /// <summary>gz/xz gibi tek dosyalık formatlarda içerideki dosyanın adı yoksa arşiv adından türetilir.</summary>
    private static string DefaultItemName(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".tgz" or ".tbz2" or ".txz" or ".tzst" => Path.ChangeExtension(name, ".tar"),
            ".gz" or ".bz2" or ".xz" or ".zst" or ".lz4" or ".br" or ".lzma" or ".z" => name[..^ext.Length],
            _ => name + "~",
        };
    }

    private static ArchiveFormat FormatFromHandler(string handler)
    {
        foreach (var format in Enum.GetValues<ArchiveFormat>())
        {
            if (SevenZipLibrary.HandlerName(format) is { } name && name.Equals(handler, StringComparison.OrdinalIgnoreCase))
                return format;
        }
        return ArchiveFormat.Unknown;
    }

    private static void Release(IInArchive? archive)
    {
        if ((object?)archive is ComObject comObject)
            comObject.FinalRelease();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _archive.Close();
            Release(_archive);
            await _file.DisposeAsync().ConfigureAwait(false);
            foreach (var volume in _volumes)
                await volume.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
