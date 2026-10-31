using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;

namespace AfuZip.Engine.SevenZip.Interop;

/// <summary>7-Zip'in yazdığı arşivi seek edilebilir bir .NET stream'ine aktarır (7z başlığı sonradan başa yazılır).</summary>
[GeneratedComClass]
internal sealed unsafe partial class OutStreamSeekAdapter(Stream stream, Action<Exception> onFault) : IOutStream
{
    private const int NegativeSeek = unchecked((int)0x80070083);

    public int Write(byte* data, uint size, uint* processedSize)
    {
        if (processedSize != null)
            *processedSize = 0;
        try
        {
            if (size > 0)
                stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            if (processedSize != null)
                *processedSize = size;
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            onFault(ex);
            return HResult.E_ABORT;
        }
    }

    public int Seek(long offset, uint seekOrigin, ulong* newPosition)
    {
        try
        {
            var origin = (SeekOrigin)seekOrigin;
            var basePosition = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => stream.Position,
                SeekOrigin.End => stream.Length,
                _ => -1,
            };
            if (basePosition < 0)
                return HResult.E_FAIL;
            if (basePosition + offset < 0)
                return NegativeSeek;

            var position = stream.Seek(offset, origin);
            if (newPosition != null)
                *newPosition = (ulong)position;
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            onFault(ex);
            return HResult.E_FAIL;
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            stream.SetLength((long)newSize);
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            onFault(ex);
            return HResult.E_FAIL;
        }
    }
}

/// <summary>
/// Yeni arşivin girdilerini 7-Zip'e anlatır: hangisi eski arşivden aynen kopyalanacak, hangisinin adı
/// değişecek, hangisi diskten okunacak. Hiçbir istisna native sınırı geçmez; ilki <see cref="Fault"/>'ta saklanır.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class UpdateCallback(
    IReadOnlyList<UpdateItem> items,
    IInArchive? source,
    string? password,
    IProgress<ArchiveProgress>? progress,
    CancellationToken ct) : IArchiveUpdateCallback, ICryptoGetTextPassword2, ICryptoGetTextPassword
{
    private readonly List<Stream> _streams = [];
    private long _total;
    private string? _current;

    public Exception? Fault { get; private set; }

    public void SetFault(Exception ex) => Fault ??= ex;

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
        try
        {
            progress.Report(new ArchiveProgress((long)*completeValue, _total, _current));
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

    public int GetUpdateItemInfo(uint index, int* newData, int* newProps, uint* indexInArchive)
    {
        if (index >= items.Count)
            return HResult.E_FAIL;

        var (data, props, old) = items[(int)index] switch
        {
            UpdateItem.Keep keep => (0, 0, (uint)keep.Index),
            UpdateItem.Rename rename => (0, 1, (uint)rename.Index),
            _ => (1, 1, uint.MaxValue),
        };
        if (newData != null) *newData = data;
        if (newProps != null) *newProps = props;
        if (indexInArchive != null) *indexInArchive = old;
        return HResult.S_OK;
    }

    public int GetProperty(uint index, uint propId, PropVariant* value)
    {
        if (value == null || index >= items.Count)
            return HResult.E_FAIL;
        try
        {
            *value = default;
            switch (items[(int)index])
            {
                case UpdateItem.Rename rename:
                    // Yol dışındaki her şey (tarih, öznitelik, klasör mü) eski arşivden aynen.
                    if (propId == ItemPropId.Path)
                        SetString(value, ToOsPath(rename.Path));
                    else if (propId == ItemPropId.IsAnti)
                        SetBool(value, false);
                    else if (source is not null)
                        return source.GetProperty((uint)rename.Index, propId, value);
                    return HResult.S_OK;

                case UpdateItem.Add add:
                    SetNewItemProperty(add, propId, value);
                    return HResult.S_OK;

                default:
                    return HResult.S_OK;
            }
        }
        catch (Exception ex)
        {
            SetFault(ex);
            return HResult.E_FAIL;
        }
    }

    public int GetStream(uint index, out ISequentialInStream? inStream)
    {
        inStream = null;
        if (ct.IsCancellationRequested)
            return HResult.E_ABORT;
        if (index >= items.Count || items[(int)index] is not UpdateItem.Add { Source.IsDirectory: false } add)
            return HResult.S_OK;

        try
        {
            _current = add.Path;
            // Zip çok iş parçacıklı modda GetStream'in hemen ardından SetOperationResult'ı çağırır ve akışı işçi
            // thread sonra okur; bu yüzden dosya ilk okumada açılır, sonuna gelince kendiliğinden kapanır.
            var stream = new LazyFileStream(add.Source.FullPath, SetFault);
            _streams.Add(stream);
            inStream = new InStreamAdapter(stream);
            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            SetFault(new IOException($"'{add.Source.FullPath}' okunamadı: {ex.Message}", ex));
            return HResult.E_ABORT;
        }
    }

    public int SetOperationResult(int operationResult)
    {
        // Akışlar burada kapatılmaz (bkz. GetStream); UpdateItems bitince hepsi birden kapatılır.
        if (operationResult != 0)
            SetFault(new IOException($"7-Zip '{_current}' girdisini yazamadı (sonuç {operationResult})."));
        return HResult.S_OK;
    }

    public int CryptoGetTextPassword2(out int passwordIsDefined, out string? password1)
    {
        passwordIsDefined = password is null ? 0 : 1;
        password1 = password ?? "";
        return HResult.S_OK;
    }

    /// <summary>Şifreli bir arşivi güncellerken eski verinin çözülmesi gerekirse (solid blok yeniden paketleme).</summary>
    public int CryptoGetTextPassword(out string? password1)
    {
        password1 = password;
        return password is null ? HResult.E_ABORT : HResult.S_OK;
    }

    public void CloseOpenStreams()
    {
        foreach (var stream in _streams)
            stream.Dispose();
        _streams.Clear();
    }

    private static void SetNewItemProperty(UpdateItem.Add add, uint propId, PropVariant* value)
    {
        var source = add.Source;
        FileSystemInfo info = source.IsDirectory ? new DirectoryInfo(source.FullPath) : new FileInfo(source.FullPath);

        switch (propId)
        {
            case ItemPropId.Path:
                SetString(value, ToOsPath(add.Path));
                break;
            case ItemPropId.IsDir:
                SetBool(value, source.IsDirectory);
                break;
            case ItemPropId.IsAnti:
                SetBool(value, false);
                break;
            case ItemPropId.Size:
                // tar her yeni girdi için (klasör dahil) VT_UI8 boyut ister; 7-Zip'in kendi istemcisi klasöre 0 verir.
                value->VarType = PropVariant.VT_UI8;
                value->Int64 = source.IsDirectory ? 0 : ((FileInfo)info).Length;
                break;
            case ItemPropId.Attrib:
                value->VarType = PropVariant.VT_UI4;
                value->UInt32 = (uint)info.Attributes;
                break;
            case ItemPropId.MTime:
                SetFileTime(value, info.LastWriteTimeUtc);
                break;
            case ItemPropId.CTime:
                SetFileTime(value, info.CreationTimeUtc);
                break;
            case ItemPropId.ATime:
                SetFileTime(value, info.LastAccessTimeUtc);
                break;
        }
    }

    // 7-Zip'in kendi istemcisi yolları işletim sistemi ayracıyla verir; işleyici arşivin biçimine çevirir.
    private static string ToOsPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static void SetString(PropVariant* value, string text)
    {
        value->VarType = PropVariant.VT_BSTR;
        value->Pointer = Marshal.StringToBSTR(text); // sahipliği 7-Zip'e geçer (PropVariantClear ile bırakır)
    }

    private static void SetBool(PropVariant* value, bool flag)
    {
        value->VarType = PropVariant.VT_BOOL;
        value->Bool = flag ? (short)-1 : (short)0;
    }

    private static void SetFileTime(PropVariant* value, DateTime utc)
    {
        if (utc.Year < 1601)
            return;
        value->VarType = PropVariant.VT_FILETIME;
        value->Int64 = utc.ToFileTimeUtc();
    }
}

/// <summary>
/// İlk okumada açılan, sonuna gelince kapanan dosya akışı. Sonradan yeniden okunur veya seek edilirse
/// (zip sıkıştırma kazanç sağlamayınca "store"a düşüp başa döner) yeniden açılır. Böylece binlerce dosyalık
/// bir arşivde aynı anda yalnızca okunmakta olanlar açık kalır.
/// </summary>
internal sealed class LazyFileStream(string path, Action<Exception> onFault) : Stream
{
    private readonly Lock _gate = new();
    private FileStream? _file;
    private long _position;
    private long? _length;
    private bool _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length ??= new FileInfo(path).Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_file is null)
            {
                _file = Open();
                _file.Position = _position;
            }

            var read = _file.Read(buffer);
            _position += read;
            if (read == 0 && buffer.Length > 0)
                CloseFile();
            return read;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
            _position = target;
            if (_file is not null)
                _file.Position = target;
            return target;
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                CloseFile();
            }
        }
        base.Dispose(disposing);
    }

    private FileStream Open()
    {
        try
        {
            // Başka bir program dosyayı yazıyor olsa bile okunabilsin (günlük dosyaları, açık belgeler).
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            onFault(new IOException($"'{path}' okunamadı: {ex.Message}", ex));
            throw;
        }
    }

    private void CloseFile()
    {
        _file?.Dispose();
        _file = null;
    }
}
