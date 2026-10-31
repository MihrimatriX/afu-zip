namespace AfuZip.Core;

/// <summary>
/// Arşivin yazılacağı yer. Önce aynı klasörde geçici adlarla yazılır, <see cref="Commit"/> ile gerçek adlara
/// taşınır; yarıda kalan (iptal, hata) yazımda geçici dosyalar silinir ve var olan arşiv bozulmaz.
/// <see cref="VolumeSize"/> verilirse çıktı 7-Zip biçiminde ciltlere bölünür: ad.7z.001, ad.7z.002...
/// </summary>
public sealed class OutputTarget : IDisposable
{
    private readonly string _tempBase;
    private readonly VolumeWriterStream? _volumes;
    private readonly FileStream? _single;
    private bool _committed;

    public OutputTarget(string finalPath, long? volumeSize = null)
    {
        FinalPath = Path.GetFullPath(finalPath);
        VolumeSize = volumeSize;
        Directory.CreateDirectory(Path.GetDirectoryName(FinalPath)!);
        _tempBase = $"{FinalPath}.{Guid.NewGuid():N}.tmp";

        if (volumeSize is { } size)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 64 * 1024, nameof(volumeSize));
            _volumes = new VolumeWriterStream(_tempBase, size);
            Stream = _volumes;
        }
        else
        {
            _single = new FileStream(_tempBase, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            Stream = _single;
        }
    }

    public string FinalPath { get; }
    public long? VolumeSize { get; }

    /// <summary>Okunabilir, yazılabilir ve seek edilebilir (7z başlığı sonradan dosyanın başına yazılır).</summary>
    public Stream Stream { get; }

    /// <summary>Commit sonrası oluşan dosyalar (tek dosya veya ciltler).</summary>
    public IReadOnlyList<string> Files { get; private set; } = [];

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_committed, this);
        // Yeniden adlandırmadan önce diske yaz: elektrik kesilirse eski arşivin yerinde yarım dosya kalmasın.
        if (_single is not null)
            _single.Flush(flushToDisk: true);
        else
            _volumes!.FlushToDisk();
        Stream.Dispose();

        if (_volumes is not null)
        {
            var count = _volumes.VolumeCount;
            // Eski bir bölünmüş arşivin fazladan ciltleri kalmasın (ad.7z.005 önceki denemeden).
            for (var i = count + 1; File.Exists(VolumeWriterStream.VolumeName(FinalPath, i)); i++)
                File.Delete(VolumeWriterStream.VolumeName(FinalPath, i));

            var files = new List<string>(count);
            for (var i = 1; i <= count; i++)
            {
                var target = VolumeWriterStream.VolumeName(FinalPath, i);
                File.Move(VolumeWriterStream.VolumeName(_tempBase, i), target, overwrite: true);
                files.Add(target);
            }
            Files = files;
        }
        else if (File.Exists(FinalPath))
        {
            // ReplaceFile eski dosyanın adlandırılmış akışlarını (Mark-of-the-Web: Zone.Identifier), ACL'sini ve
            // oluşturulma tarihini korur; düzenlenen internet arşivi "internetten geldi" işaretini kaybetmesin.
            try
            {
                File.Replace(_tempBase, FinalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                File.Move(_tempBase, FinalPath, overwrite: true);
            }
            Files = [FinalPath];
        }
        else
        {
            File.Move(_tempBase, FinalPath);
            Files = [FinalPath];
        }
        _committed = true;
    }

    public void Dispose()
    {
        if (_committed)
            return;
        Stream.Dispose();
        if (_volumes is not null)
        {
            for (var i = 1; i <= _volumes.VolumeCount; i++)
                TryDelete(VolumeWriterStream.VolumeName(_tempBase, i));
        }
        else
        {
            TryDelete(_tempBase);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Sabit boyutlu ciltlere bölünen, seek edilebilir yazma stream'i. 7z biçimi bitince başa dönüp başlığı
/// yeniden yazdığı için ciltler kapanmaz; konum hangi cilde düşüyorsa oraya yazılır.
/// </summary>
public sealed class VolumeWriterStream(string basePath, long volumeSize) : Stream
{
    private readonly List<FileStream> _volumes = [];
    private long _position;
    private long _length;

    public static string VolumeName(string basePath, int number) => $"{basePath}.{number:000}";

    public int VolumeCount => _volumes.Count;
    public long VolumeSize => volumeSize;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var index = (int)(_position / volumeSize);
            var within = _position % volumeSize;
            var volume = Volume(index);
            var take = (int)Math.Min(buffer.Length, volumeSize - within);

            volume.Position = within;
            volume.Write(buffer[..take]);
            buffer = buffer[take..];
            _position += take;
            _length = Math.Max(_length, _position);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            var index = (int)(_position / volumeSize);
            var within = _position % volumeSize;
            var volume = Volume(index);
            volume.Position = within;
            var take = (int)Math.Min(Math.Min(buffer.Length, volumeSize - within), _length - _position);
            var read = volume.Read(buffer[..take]);
            if (read == 0)
                break;
            buffer = buffer[read..];
            _position += read;
            total += read;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        return _position = target;
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var needed = value == 0 ? 0 : (int)((value - 1) / volumeSize) + 1;
        for (var i = _volumes.Count - 1; i >= needed; i--)
        {
            var path = _volumes[i].Name;
            _volumes[i].Dispose();
            File.Delete(path);
            _volumes.RemoveAt(i);
        }
        // Büyütmede aradaki ciltler tam boya, sonuncusu kalana getirilir (boşluklar sıfırla dolar).
        for (var i = 0; i < needed; i++)
        {
            var size = i < needed - 1 ? volumeSize : value - (long)(needed - 1) * volumeSize;
            var volume = Volume(i);
            if (volume.Length != size)
                volume.SetLength(size);
        }
        _length = value;
        _position = Math.Min(_position, value);
    }

    public override void Flush()
    {
        foreach (var volume in _volumes)
            volume.Flush();
    }

    public void FlushToDisk()
    {
        foreach (var volume in _volumes)
            volume.Flush(flushToDisk: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var volume in _volumes)
                volume.Dispose();
        }
        base.Dispose(disposing);
    }

    private FileStream Volume(int index)
    {
        while (_volumes.Count <= index)
        {
            var number = _volumes.Count + 1;
            _volumes.Add(new FileStream(VolumeName(basePath, number), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20));
        }
        return _volumes[index];
    }
}
