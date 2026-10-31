using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;

namespace AfuZip.Engine.SevenZip.Interop;

/// <summary>
/// Arşiv açılırken çağrılır. Başlığı şifreli 7z'ler burada parola ister; RAR ve 7z ciltleri
/// <see cref="IArchiveOpenVolumeCallback"/> üzerinden sonraki parçaları açtırır.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class OpenCallback(string archivePath, IPasswordProvider? passwords, CancellationToken ct)
    : IArchiveOpenCallback, ICryptoGetTextPassword, IArchiveOpenVolumeCallback
{
    private readonly List<FileStream> _volumes = [];
    private readonly List<string> _missing = [];

    public bool PasswordRequested { get; private set; }
    public string? PasswordUsed { get; private set; }
    public Exception? Fault { get; private set; }

    /// <summary>Açılan ek ciltler; arşiv kapanınca okuyucu tarafından kapatılır.</summary>
    public IReadOnlyList<FileStream> Volumes => _volumes;

    /// <summary>
    /// İşleyicinin istediği ama bulunamayan cilt adları. Split gibi işleyiciler son cildi bulmak için
    /// her zaman bir sonrakini de ister; bu yüzden tek başına hata değildir, "beklenmedik son" ile birlikte anlamlıdır.
    /// </summary>
    public IReadOnlyList<string> MissingVolumes => _missing;

    public void DisposeVolumes()
    {
        foreach (var volume in _volumes)
            volume.Dispose();
        _volumes.Clear();
    }

    public int GetProperty(uint propId, PropVariant* value)
    {
        // İşleyici ilk cildin adından sonraki cilt adlarını türetir (film.part1.rar → film.part2.rar).
        if (propId == ItemPropId.Name && value != null)
        {
            value->VarType = PropVariant.VT_BSTR;
            value->Pointer = System.Runtime.InteropServices.Marshal.StringToBSTR(Path.GetFileName(archivePath));
        }
        return HResult.S_OK;
    }

    public int GetStream(string name, out IInStream? inStream)
    {
        inStream = null;
        try
        {
            // Yalnızca ilk cildin klasöründeki dosyalar: arşiv "..\..\x" gibi bir ad isteyemez.
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, Path.GetFileName(name));
            if (!File.Exists(path))
            {
                _missing.Add(Path.GetFileName(path));
                return HResult.S_FALSE;
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            _volumes.Add(stream);
            inStream = new InStreamAdapter(stream);
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            Fault ??= ex;
            return HResult.E_FAIL;
        }
    }

    public int SetTotal(ulong* files, ulong* bytes) => HResult.S_OK;

    public int SetCompleted(ulong* files, ulong* bytes) => ct.IsCancellationRequested ? HResult.E_ABORT : HResult.S_OK;

    public int CryptoGetTextPassword(out string? password)
    {
        PasswordRequested = true;
        try
        {
            password = PasswordUsed ?? PasswordPrompt.Ask(passwords, archivePath, ct);
        }
        catch (Exception ex)
        {
            // Sağlayıcının istisnası (iptal, diyalog hatası) native sınırı geçmez; Open bittikten sonra atılır.
            Fault ??= ex;
            password = null;
        }
        PasswordUsed = password;
        return password is null ? HResult.E_ABORT : HResult.S_OK;
    }
}

/// <summary>7-Zip'in çıkardığı her girdi için hedef stream'i sağlayan taraf.</summary>
internal interface IExtractSink
{
    /// <summary>Yalnızca çıkarma modundaki girdiler için çağrılır.</summary>
    /// <returns>null: bu girdi için çıktı yok (klasör, atlandı, engellendi).</returns>
    Stream? Begin(int index);

    /// <summary>
    /// İstenen her girdi için çağrılır. İşleyici bir girdiyi kendiliğinden test veya atla moduna
    /// düşürebilir (ör. solid blokta bozulmadan sonra); bu durumda <see cref="Begin"/> çağrılmamış olur.
    /// </summary>
    void End(int index, OperationResult result);

    /// <summary>İşlem yarıda kesildiğinde açık kalan girdiyi temizler.</summary>
    void Abort(int index);
}

[GeneratedComClass]
internal sealed unsafe partial class ExtractCallback(
    IExtractSink sink,
    string archivePath,
    bool[]? requested,
    IPasswordProvider? passwords,
    string? knownPassword,
    IProgress<ArchiveProgress>? progress,
    CancellationToken ct) : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private long _total;
    private int _current = -1;

    public Exception? Fault { get; private set; }
    public bool PasswordRequested { get; private set; }
    public string? PasswordUsed { get; private set; } = knownPassword;

    /// <summary>En az bir girdi parola kaynaklı hata verdi; kullanılan parola hatırlanmamalı.</summary>
    public bool PasswordFailed { get; private set; }

    public int SetTotal(ulong total)
    {
        _total = (long)total;
        return HResult.S_OK;
    }

    public int SetCompleted(ulong* completeValue)
    {
        if (ct.IsCancellationRequested)
            return HResult.E_ABORT;
        if (completeValue == null || progress is null)
            return HResult.S_OK;

        // İlerleme alıcısı (ör. duraklatılmış görev) istisna atabilir; native sınırı geçmemeli.
        try
        {
            progress.Report(new((long)*completeValue, _total));
            return HResult.S_OK;
        }
        catch (OperationCanceledException)
        {
            return HResult.E_ABORT;
        }
        catch (Exception ex)
        {
            SetFault(ex);
            return HResult.E_ABORT;
        }
    }

    public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
    {
        outStream = null;
        if (ct.IsCancellationRequested)
            return HResult.E_ABORT;

        try
        {
            // Önceki girdi SetOperationResult almadan yenisi geldiyse açık stream'i kapat.
            if (_current >= 0)
                sink.Abort(_current);

            // Solid arşivde istenen girdiye ulaşmak için bloğun önceki girdileri de "atla" moduyla
            // buradan geçer. Bunlar istenmediği için sonuçları sayılmaz ve hiçbir sink'e iletilmez.
            if (requested is not null && (index >= (uint)requested.Length || !requested[index]))
            {
                _current = -1;
                return HResult.S_OK;
            }

            // İstenen girdi için mod ne olursa olsun indeksi kaydediyoruz: işleyici girdiyi kendisi
            // test/atla moduna düşürse bile SetOperationResult'taki hata doğru girdiye işlenir.
            _current = (int)index;
            if (askExtractMode != 0)
                return HResult.S_OK;

            if (sink.Begin(_current) is { } target)
                outStream = new OutStreamAdapter(target, SetFault);
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            SetFault(ex);
            return HResult.E_ABORT;
        }
    }

    public int PrepareOperation(int askExtractMode) => HResult.S_OK;

    public int SetOperationResult(int opRes)
    {
        try
        {
            if (_current < 0)
                return HResult.S_OK; // istenmeyen (atlanan) girdi

            var result = (OperationResult)opRes;
            if (PasswordRequested && result is OperationResult.WrongPassword or OperationResult.DataError or OperationResult.CrcError)
                PasswordFailed = true;

            // End atarsa _current kalır ve Cleanup yarım dosyayı Abort ile temizler.
            sink.End(_current, result);
            _current = -1;
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            SetFault(ex);
            return HResult.E_ABORT;
        }
    }

    public int CryptoGetTextPassword(out string? password)
    {
        PasswordRequested = true;
        try
        {
            password = PasswordUsed ?? PasswordPrompt.Ask(passwords, archivePath, ct);
        }
        catch (Exception ex)
        {
            SetFault(ex);
            password = null;
        }
        PasswordUsed = password;
        return password is null ? HResult.E_ABORT : HResult.S_OK;
    }

    /// <summary>Extract döndükten sonra çağrılır: yarım kalan girdiyi temizler.</summary>
    public void Cleanup()
    {
        if (_current >= 0)
            sink.Abort(_current);
        _current = -1;
    }

    private void SetFault(Exception ex) => Fault ??= ex;
}

internal static class PasswordPrompt
{
    // 7-Zip callback'leri senkron; UI parola diyaloğunu kendi thread'inde gösterip sonucu buraya döndürür.
    public static string? Ask(IPasswordProvider? passwords, string archivePath, CancellationToken ct) =>
        passwords?.GetPasswordAsync(archivePath, ct).AsTask().GetAwaiter().GetResult();
}
