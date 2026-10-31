using System.Collections;

namespace AfuZip.Core.Preview;

public sealed record HexRow(long Offset, string OffsetText, string HexText, string AsciiText);

/// <summary>
/// Sanallaştırılmış hex görünümü için satır kaynağı. Satırlar yalnızca istendiğinde, 64 KB'lık parçalar
/// halinde okunup biçimlenir; böylece birkaç GB'lık bir dosya da anında açılır. Liste kontrolüne doğrudan
/// <see cref="IList"/> olarak verilebilir: kontrol yalnızca görünen satırların indeksini ister.
/// </summary>
public sealed class HexDocument : IList, IReadOnlyList<HexRow>, IDisposable
{
    public const int BytesPerRow = 16;
    private const int ChunkSize = 64 * 1024;
    private const int MaxCachedChunks = 8;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Lock _gate = new();
    private readonly LinkedList<(long Index, byte[] Data, int Length)> _chunks = new();
    private readonly int _offsetDigits;

    /// <param name="stream">Seek edilebilir olmalı.</param>
    /// <param name="maxBytes">Gösterilecek en fazla bayt; liste kontrolleri milyonlarca satırda yavaşlar.</param>
    public HexDocument(Stream stream, bool ownsStream = true, long maxBytes = long.MaxValue)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Hex görünümü seek edilebilir bir stream ister.", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
        Length = Math.Min(stream.Length, maxBytes);
        IsTruncated = stream.Length > maxBytes;
        RowCount = (Length + BytesPerRow - 1) / BytesPerRow;
        _offsetDigits = Length > 0xFFFF_FFFFL ? 12 : 8;
    }

    public long Length { get; }

    /// <summary>Dosya <c>maxBytes</c>'tan büyüktü; yalnızca baş kısmı gösteriliyor.</summary>
    public bool IsTruncated { get; }
    public long RowCount { get; }

    /// <summary>Liste kontrolleri int sayı ister; 32 GB'tan büyük dosyalarda baştaki satırlar gösterilir.</summary>
    public int Count => (int)Math.Min(RowCount, int.MaxValue);

    public HexRow this[int index] => GetRow(index);

    public HexRow GetRow(long row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Math.Max(RowCount, 1));

        var offset = row * BytesPerRow;
        Span<byte> bytes = stackalloc byte[BytesPerRow];
        var count = Read(offset, bytes);
        return Format(offset, bytes[..count], _offsetDigits);
    }

    /// <summary>"4D 5A 90 00 03 00 00 00  04 00 …" ve "MZ.............." biçiminde tek satır.</summary>
    public static HexRow Format(long offset, ReadOnlySpan<byte> bytes, int offsetDigits = 8)
    {
        Span<char> hex = stackalloc char[BytesPerRow * 3 + 1];
        Span<char> ascii = stackalloc char[BytesPerRow];
        hex.Fill(' ');
        ascii.Fill(' ');

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var position = i * 3 + (i >= 8 ? 1 : 0); // 8 bayttan sonra fazladan bir boşluk
            hex[position] = HexDigit(b >> 4);
            hex[position + 1] = HexDigit(b & 0xF);
            ascii[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
        }

        return new HexRow(
            offset,
            offset.ToString(offsetDigits == 8 ? "X8" : "X12"),
            new string(hex).TrimEnd(),
            new string(ascii[..bytes.Length]));
    }

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);

    private int Read(long offset, Span<byte> destination)
    {
        lock (_gate)
        {
            var total = 0;
            while (total < destination.Length && offset + total < Length)
            {
                var position = offset + total;
                var chunk = GetChunk(position / ChunkSize);
                var start = (int)(position % ChunkSize);
                var available = chunk.Length - start;
                if (available <= 0)
                    break;
                var take = Math.Min(available, destination.Length - total);
                chunk.Data.AsSpan(start, take).CopyTo(destination[total..]);
                total += take;
            }
            return total;
        }
    }

    private (byte[] Data, int Length) GetChunk(long index)
    {
        for (var node = _chunks.First; node is not null; node = node.Next)
        {
            if (node.Value.Index != index)
                continue;
            // En son kullanılanı başa al (LRU).
            _chunks.Remove(node);
            _chunks.AddFirst(node);
            return (node.Value.Data, node.Value.Length);
        }

        var data = _chunks.Count >= MaxCachedChunks ? _chunks.Last!.Value.Data : new byte[ChunkSize];
        if (_chunks.Count >= MaxCachedChunks)
            _chunks.RemoveLast();

        _stream.Position = index * ChunkSize;
        var length = _stream.ReadAtLeast(data, ChunkSize, throwOnEndOfStream: false);
        _chunks.AddFirst((index, data, length));
        return (data, length);
    }

    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }

    public IEnumerator<HexRow> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return GetRow(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // --- IList: liste kontrolleri için salt okunur bağlama yüzeyi -----------------------------------

    object? IList.this[int index]
    {
        get => GetRow(index);
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is HexRow row && row.Offset / BytesPerRow < Count && GetRow(row.Offset / BytesPerRow) == row;
    int IList.IndexOf(object? value) => value is HexRow row && row.Offset % BytesPerRow == 0 && row.Offset / BytesPerRow < Count ? (int)(row.Offset / BytesPerRow) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
            array.SetValue(GetRow(i), index + i);
    }
}
