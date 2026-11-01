using AfuZip.Core;
using AfuZip.Engine.SevenZip.Interop;

namespace AfuZip.Engine.SevenZip;

/// <summary>Girdileri <see cref="ExtractionSession"/> kurallarıyla diske yazar.</summary>
internal sealed class FileSystemSink(ExtractionSession session, IReadOnlyList<ArchiveEntry> entries) : IExtractSink
{
    // Bilerek çıktı vermediğimiz girdiler (atlandı/engellendi); işleyicinin bunlar için döndürdüğü
    // sonuç kullanıcıya hata olarak gösterilmez.
    private readonly HashSet<int> _declined = [];

    private ArchiveEntry? _entry;
    private string? _target;
    private Stream? _output;

    public Stream? Begin(int index)
    {
        var entry = entries[index];
        if (entry.IsDirectory)
        {
            session.CreateDirectory(entry);
            _declined.Add(index);
            return null;
        }

        var target = session.BeginFile(entry);
        if (target is null)
        {
            _declined.Add(index);
            return null;
        }

        _entry = entry;
        _target = target;
        _output = session.OpenOutput(target);
        return _output;
    }

    public void End(int index, OperationResult result)
    {
        if (_entry is null)
        {
            // Begin çağrılmadı: işleyici girdiyi kendiliğinden test/atla moduna düşürdü.
            if (!_declined.Contains(index) && !IsSuccess(result) && !entries[index].IsDirectory)
                session.FailFile(entries[index], null, SevenZipErrors.Describe(result, entries[index].IsEncrypted));
            return;
        }

        _output?.Dispose();
        if (IsSuccess(result))
            session.CompleteFile(_entry, _target!);
        else
            session.FailFile(_entry, _target, SevenZipErrors.Describe(result, _entry.IsEncrypted));
        Reset();
    }

    public void Abort(int index)
    {
        if (_entry is null)
            return;
        _output?.Dispose();
        session.FailFile(_entry, _target, "işlem yarıda kesildi");
        Reset();
    }

    private static bool IsSuccess(OperationResult result) => result is OperationResult.Ok or OperationResult.DataAfterEnd;

    private void Reset()
    {
        _entry = null;
        _target = null;
        _output = null;
    }
}

/// <summary>Tek girdiyi belleğe veya geçici dosyaya açar (iç içe arşiv, OpenEntryAsync).</summary>
internal sealed class SingleEntrySink(ArchiveEntry entry) : IExtractSink
{
    private const long InMemoryLimit = 64L * 1024 * 1024;

    private Stream? _buffer;
    private OperationResult? _result;

    public Stream? Begin(int index)
    {
        // Boyutu bilinmeyen girdiler (gz/xz/bz2 çoğu zaman boyut bildirmez) sınırsız büyüyebileceği için
        // belleğe değil geçici dosyaya açılır.
        _buffer = entry.Size is > 0 and <= InMemoryLimit
            ? new MemoryStream((int)entry.Size)
            : new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        return _buffer;
    }

    public void End(int index, OperationResult result) => _result = result;

    public void Abort(int index)
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    /// <summary>Başa sarılmış stream'i döndürür; girdi hatalıysa gerçek nedeni içeren istisna atar.</summary>
    public Stream TakeStream(string archivePath)
    {
        if (_result is { } result && result is not (OperationResult.Ok or OperationResult.DataAfterEnd))
        {
            _buffer?.Dispose();
            if (SevenZipErrors.IsPasswordFailure(result, entry.IsEncrypted))
                throw new ArchivePasswordException(archivePath, wasProvided: true);
            throw new InvalidDataException($"'{entry.Path}': {SevenZipErrors.Describe(result, entry.IsEncrypted)}");
        }

        if (_buffer is null || _result is null)
            throw new IOException($"'{entry.Path}' açılamadı.");

        _buffer.Position = 0;
        return _buffer;
    }
}

/// <summary>Test modu: 7-Zip veriyi açıp CRC'yi doğrular, hiçbir şey yazılmaz.</summary>
internal sealed class TestSink(TestResult result, IReadOnlyList<ArchiveEntry> entries) : IExtractSink
{
    public Stream? Begin(int index) => null;

    public void End(int index, OperationResult opResult)
    {
        var entry = entries[index];
        if (entry.IsDirectory)
            return;
        if (opResult is OperationResult.Ok or OperationResult.DataAfterEnd)
            result.AddTested();
        else
            result.AddFailed(entry.Path, SevenZipErrors.Describe(opResult, entry.IsEncrypted));
    }

    public void Abort(int index)
    {
    }
}

/// <summary><see cref="IEntryReceiver"/>'ı 7-Zip'in girdi akışına bağlar (bir bloğun birden çok girdisini tek geçişte okumak için).</summary>
internal sealed class ReceiverSink(IEntryReceiver receiver, IReadOnlyList<ArchiveEntry> entries) : IExtractSink
{
    private int _active = -1;

    public Stream? Begin(int index)
    {
        var entry = entries[index];
        if (entry.IsDirectory || receiver.Begin(entry) is not { } target)
            return null;
        _active = index;
        return target;
    }

    public void End(int index, OperationResult result)
    {
        // Begin çağrılmadıysa (klasör, alıcı istemedi, işleyici atla moduna düşürdü) alıcıya bildirilecek bir şey yok.
        if (_active != index)
            return;
        _active = -1;
        var entry = entries[index];
        receiver.End(entry, result is OperationResult.Ok or OperationResult.DataAfterEnd
            ? null
            : SevenZipErrors.Describe(result, entry.IsEncrypted));
    }

    public void Abort(int index)
    {
        if (_active != index)
            return;
        _active = -1;
        receiver.End(entries[index], "işlem yarıda kesildi");
    }
}

internal static class SevenZipErrors
{
    public static bool IsPasswordFailure(OperationResult result, bool encrypted) =>
        result == OperationResult.WrongPassword ||
        (encrypted && result is OperationResult.DataError or OperationResult.CrcError);

    public static string Describe(OperationResult result, bool encrypted)
    {
        if (IsPasswordFailure(result, encrypted))
            return "parola yanlış";

        return result switch
        {
            OperationResult.UnsupportedMethod => "desteklenmeyen sıkıştırma yöntemi",
            OperationResult.DataError => "veri hatası",
            OperationResult.CrcError => "CRC hatası (dosya bozuk)",
            OperationResult.Unavailable => "veri yok (eksik cilt?)",
            OperationResult.UnexpectedEnd => "beklenmeyen dosya sonu",
            OperationResult.DataAfterEnd => "verinin sonrasında fazladan bayt var",
            OperationResult.IsNotArc => "arşiv değil",
            OperationResult.HeadersError => "başlık hatası",
            _ => $"bilinmeyen hata ({(int)result})",
        };
    }
}
