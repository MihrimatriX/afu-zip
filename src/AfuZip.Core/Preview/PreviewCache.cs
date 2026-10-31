namespace AfuZip.Core.Preview;

/// <summary>
/// Önbellekteki bir girdinin içeriği. Her <see cref="OpenRead"/> bağımsız, seek edilebilir bir stream verir.
/// Açık kaldığı sürece girdi önbellekten atılmaz (dosyası silinmez); işi bitince <see cref="Dispose"/> edilmeli.
/// </summary>
public sealed class PreviewSource : IDisposable
{
    private readonly PreviewCache _cache;
    private readonly PreviewCache.Item _item;
    private int _disposed;

    internal PreviewSource(PreviewCache cache, PreviewCache.Item item, bool fromCache)
    {
        _cache = cache;
        _item = item;
        FromCache = fromCache;
    }

    public ArchiveEntry Entry => _item.Entry;
    public long Length => _item.Length;

    /// <summary>İçerik daha önce açılmıştı (aynı solid bloktan veya önceki bir önizlemeden).</summary>
    public bool FromCache { get; }

    public Stream OpenRead() => _item.Memory is { } memory
        ? new MemoryStream(memory, 0, (int)_item.Length, writable: false, publiclyVisible: false)
        // FileShare.Delete: önbellek dosyayı atarken açık okuyucular (WebView, medya oynatıcı) bozulmaz.
        : new FileStream(_item.File!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

    /// <summary>Baştan en fazla <paramref name="count"/> bayt (tür tespiti için).</summary>
    public byte[] ReadHead(int count)
    {
        using var stream = OpenRead();
        var buffer = new byte[(int)Math.Min(count, _item.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cache.Unpin(_item);
    }
}

/// <summary>
/// Önizlenen girdilerin LRU önbelleği. Küçük girdiler bellekte, büyükler geçici klasörde tutulur ve toplam boyut
/// bütçeyi aşınca en uzun süredir kullanılmayan atılır; o anda gösterilen (açık <see cref="PreviewSource"/>) girdi atılmaz.
///
/// Solid arşivlerde bir dosyayı açmak bloğun önceki kısmını da çözmeyi gerektirir. Kullanıcı genelde sırayla
/// gezdiği için ilk önizlemede bloğun o dosyaya kadarki kısmı ve sonrasından küçük bir pay topluca açılır;
/// aynı bloktaki sonraki önizlemeler anında gelir. Blok açma, önizlemeyi isteyen seçimden bağımsız sürer:
/// kullanıcı aynı bloktaki başka bir dosyaya geçerse iş yarıda kesilip yeniden başlamaz.
/// </summary>
public sealed class PreviewCache : IDisposable
{
    private readonly long _budget;
    private readonly long _memoryEntryLimit;
    private readonly long _blockPrefetchLimit;
    private readonly long _readAheadLimit;
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Item>> _items = [];
    private readonly LinkedList<Item> _lru = new(); // baş: en son kullanılan
    private readonly Dictionary<Key, Task> _loading = [];
    private readonly Dictionary<IArchiveReader, CancellationTokenSource> _readerTokens = new(ReferenceEqualityComparer.Instance);
    private long _size;
    private bool _disposed;

    /// <param name="budget">Önbelleğin toplam boyutu (bellek + disk).</param>
    /// <param name="memoryEntryLimit">Bundan küçük girdiler bellekte tutulur.</param>
    /// <param name="blockPrefetchLimit">Solid blokta topluca açılacak en fazla bayt.</param>
    /// <param name="readAheadLimit">Hedeften sonraki girdilerden önceden açılacak en fazla bayt (okuyucuyu az meşgul etsin).</param>
    public PreviewCache(long budget = 512L << 20, long memoryEntryLimit = 4L << 20, long blockPrefetchLimit = 192L << 20,
        long readAheadLimit = 32L << 20, string? directory = null)
    {
        _budget = budget;
        _memoryEntryLimit = memoryEntryLimit;
        _blockPrefetchLimit = Math.Min(blockPrefetchLimit, budget / 2);
        _readAheadLimit = Math.Min(readAheadLimit, _blockPrefetchLimit);
        _directory = directory ?? Path.Combine(Path.GetTempPath(), "AfuZip", "preview", Guid.NewGuid().ToString("N"));
    }

    public long Size
    {
        get
        {
            lock (_gate)
                return _size;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public bool Contains(IArchiveReader reader, int index)
    {
        lock (_gate)
            return _items.ContainsKey(new Key(reader, index));
    }

    /// <summary>Girdiyi önbellekten verir; yoksa (solid arşivde bloğuyla birlikte) açar. Dönen kaynak Dispose edilmeli.</summary>
    /// <param name="progress">Solid blok açılırken ilerleme.</param>
    /// <exception cref="ArchivePasswordException">Parola gerekli, verilmedi veya yanlış.</exception>
    public async Task<PreviewSource> OpenAsync(IArchiveReader reader, ArchiveEntry entry,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.IsDirectory)
            throw new ArgumentException($"'{entry.Path}' bir klasör.", nameof(entry));

        var key = new Key(reader, entry.Index);
        if (TryGet(key, fromCache: true) is { } hit)
            return hit;

        // Aynı girdiyi içeren bir blok zaten açılıyorsa onu bekle; değilse gerekiyorsa yeni blok açmayı başlat.
        Task? load;
        lock (_gate)
            _loading.TryGetValue(key, out load);
        if (load is null && SolidGroup(reader, entry) is { Count: > 1 } group)
            load = StartBlockLoad(reader, group, progress);

        if (load is not null)
        {
            try
            {
                await load.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ArchivePasswordException)
            {
                // Kullanıcı parola diyaloğunda vazgeçti: tek girdi yolunda yeniden sorulmasın.
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Blok açılamadı; aşağıdaki tek girdi yolu gerçek hatayı (CRC, desteklenmeyen yöntem) verir.
            }
            if (TryGet(key, fromCache: false) is { } loaded)
                return loaded;
        }

        // Solid değil, sınır aşıldı veya blok açılamadı: tek girdi. Hata (parola, CRC) buradan istisna olarak çıkar.
        await using var stream = await reader.OpenEntryAsync(entry.Index, ct).ConfigureAwait(false);
        // Kopyalama (büyük girdide diske yazma) çağıranın thread'inde (UI) yapılmasın.
        return await Task.Run(() => Store(key, entry, stream, ct), ct).ConfigureAwait(false)
            ?? throw new ObjectDisposedException(nameof(PreviewCache));
    }

    /// <summary>Okuyucu kapanırken: o okuyucuya ait girdileri atar, süren blok açmayı iptal eder.</summary>
    public void Evict(IArchiveReader reader)
    {
        // Önce iptal (kilit dışında: devamlar kilit altında satır içi çalışmasın), sonra temizlik. Arada biten
        // bir blok girdisi Add'de belirteci iptal edilmiş bulur ve eklenmez.
        CancellationTokenSource? cts;
        lock (_gate)
            _readerTokens.Remove(reader, out cts);
        cts?.Cancel();
        cts?.Dispose();

        List<Item> removed;
        lock (_gate)
        {
            removed = [.. _lru.Where(i => ReferenceEquals(i.Key.Reader, reader))];
            foreach (var item in removed)
                RemoveLocked(item);
        }
        DeleteUnpinned(removed);
    }

    public void Clear()
    {
        List<Item> removed;
        lock (_gate)
        {
            removed = [.. _lru];
            foreach (var item in removed)
                RemoveLocked(item);
        }
        DeleteUnpinned(removed);
    }

    public void Dispose()
    {
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            tokens = [.. _readerTokens.Values];
            _readerTokens.Clear();
        }
        foreach (var cts in tokens)
        {
            cts.Cancel();
            cts.Dispose();
        }
        Clear();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Hâlâ açık bir dosya (oynatılan video) varsa klasör sonraki açılışta CleanupStale ile silinir.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Çöken oturumlardan kalan önizleme klasörlerini siler (bir günden eski olanlar).</summary>
    public static void CleanupStale(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "AfuZip", "preview");
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // --- Solid blok ---------------------------------------------------------------------------------

    /// <summary>
    /// Hedefle birlikte açılacak girdiler. Bloğun hedeften önceki kısmı zaten çözülmek zorunda (sınır dahilinde
    /// saklanır); sonrasından yalnızca küçük bir pay eklenir ki okuyucu başka işler için uzun süre meşgul olmasın.
    /// Blok bilgisi yoksa (solid RAR) tüm arşiv tek blok sayılır.
    /// </summary>
    internal List<ArchiveEntry> SolidGroup(IArchiveReader reader, ArchiveEntry target)
    {
        // Tek geçişte okuyamayan okuyucuda (yönetilen tar.gz) toplu açma her girdi için baştan açmak olurdu.
        if (!reader.IsSolid || !reader.SupportsBatchRead || target.Size == 0)
            return [target];

        // Boş dosyaların 7z'de bloğu yoktur; blok bilgisi olan bir arşivde bloksuz girdi tek başına açılır.
        if (target.Block is null && reader.Entries.Any(e => e.Block is not null))
            return [target];

        var group = new List<ArchiveEntry>();
        long before = 0, after = 0;
        lock (_gate)
        {
            foreach (var entry in reader.Entries)
            {
                if (entry.IsDirectory || entry.Block != target.Block)
                    continue;
                if (entry.Index != target.Index && (_items.ContainsKey(new Key(reader, entry.Index)) || _loading.ContainsKey(new Key(reader, entry.Index))))
                    continue;

                if (entry.Index < target.Index)
                {
                    if (before + entry.Size > _blockPrefetchLimit)
                        continue; // çözülecek ama saklanmayacak
                    before += entry.Size;
                    group.Add(entry);
                }
                else if (entry.Index == target.Index)
                {
                    group.Add(entry);
                }
                else if (after + entry.Size <= _readAheadLimit)
                {
                    after += entry.Size;
                    group.Add(entry);
                }
            }
        }

        group.Sort((a, b) => a.Index.CompareTo(b.Index));
        return group.Count > 0 ? group : [target];
    }

    private Task StartBlockLoad(IArchiveReader reader, List<ArchiveEntry> group, IProgress<ArchiveProgress>? progress)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task load;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_readerTokens.TryGetValue(reader, out var cts))
                _readerTokens[reader] = cts = new CancellationTokenSource();

            load = LoadBlockAsync(reader, group, progress, started.Task, cts.Token);
            foreach (var entry in group)
                _loading.TryAdd(new Key(reader, entry.Index), load);
        }

        // Bittiğinde (başarılı, hatalı veya iptal) yalnızca bu yüklemeye ait kayıtları kaldır.
        _ = load.ContinueWith(completed =>
        {
            lock (_gate)
            {
                foreach (var entry in group)
                {
                    var key = new Key(reader, entry.Index);
                    if (_loading.TryGetValue(key, out var task) && ReferenceEquals(task, completed))
                        _loading.Remove(key);
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        started.SetResult();
        return load;
    }

    private async Task LoadBlockAsync(IArchiveReader reader, List<ArchiveEntry> group, IProgress<ArchiveProgress>? progress,
        Task started, CancellationToken ct)
    {
        await started.ConfigureAwait(false);
        var receiver = new CacheReceiver(this, reader, ct);
        try
        {
            await reader.ReadEntriesAsync([.. group.Select(e => e.Index)], receiver, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            receiver.DisposePending();
        }
    }

    // --- Depolama -----------------------------------------------------------------------------------

    private PreviewSource? TryGet(Key key, bool fromCache)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var node))
                return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            node.Value.Pins++;
            return new PreviewSource(this, node.Value, fromCache);
        }
    }

    private PreviewSource? Store(Key key, ArchiveEntry entry, Stream source, CancellationToken ct)
    {
        // Motor zaten bellekte açtıysa (MemoryStream) tamponu kopyalamadan devral.
        if (source is MemoryStream memory && memory.TryGetBuffer(out var segment) && segment.Offset == 0 &&
            memory.Length <= _memoryEntryLimit && segment.Array is { } array)
            return Add(new Item(key, entry, array, null, memory.Length), pin: true, ct);

        if (source.CanSeek && source.Length - source.Position <= _memoryEntryLimit)
        {
            var buffer = new byte[source.Length - source.Position];
            source.ReadExactly(buffer);
            return Add(new Item(key, entry, buffer, null, buffer.Length), pin: true, ct);
        }

        var file = NewFilePath(entry);
        try
        {
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); // iptal edilmiş çok GB'lık önizleme kopyalanmaya devam etmesin
                    output.Write(buffer, 0, read);
                }
            }
            return Add(new Item(key, entry, null, file, new FileInfo(file).Length), pin: true, ct);
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    /// <param name="pin">Çağıran içeriği hemen kullanacak (önizleme); atılmasın.</param>
    /// <param name="ct">İptal edildiyse (okuyucu kapandı) girdi eklenmez.</param>
    private PreviewSource? Add(Item item, bool pin, CancellationToken ct)
    {
        List<Item> discarded = [];
        PreviewSource? result = null;
        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                discarded.Add(item);
            }
            else if (_items.TryGetValue(item.Key, out var existing))
            {
                // Aynı girdi iki kez açıldıysa (çakışan yüklemeler) var olan kalır: onu gösteren biri olabilir.
                discarded.Add(item);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                if (pin)
                {
                    existing.Value.Pins++;
                    result = new PreviewSource(this, existing.Value, fromCache: false);
                }
            }
            else
            {
                _items[item.Key] = _lru.AddFirst(item);
                _size += item.Length;
                if (pin)
                {
                    item.Pins++;
                    result = new PreviewSource(this, item, fromCache: false);
                }
                EvictOverBudgetLocked(discarded);
            }
        }

        DeleteUnpinned(discarded);
        return result;
    }

    internal void Unpin(Item item)
    {
        var delete = false;
        lock (_gate)
        {
            item.Pins--;
            if (item.Pins > 0)
                return;

            if (item.Removed)
            {
                delete = true;
            }
            else if (item.Length > _budget)
            {
                // Bütçeden büyük tek girdi (zorla açılan büyük video) yalnızca gösterilirken tutulur.
                RemoveLocked(item);
                delete = true;
            }
            else
            {
                var evicted = new List<Item>();
                EvictOverBudgetLocked(evicted);
                DeleteUnpinnedLocked(evicted);
            }
        }
        if (delete)
            DeleteFile(item);
    }

    private void EvictOverBudgetLocked(List<Item> evicted)
    {
        // En eskiden başlayarak; açık (pinned) girdiler atlanır.
        for (var node = _lru.Last; node is not null && _size > _budget;)
        {
            var previous = node.Previous;
            if (node.Value.Pins == 0)
            {
                evicted.Add(node.Value);
                RemoveLocked(node.Value);
            }
            node = previous;
        }
    }

    private void RemoveLocked(Item item)
    {
        if (_items.TryGetValue(item.Key, out var node) && ReferenceEquals(node.Value, item))
        {
            _items.Remove(item.Key);
            _lru.Remove(node);
            _size -= item.Length;
        }
        item.Removed = true;
    }

    /// <summary>Atılan ama hâlâ açık olan girdinin dosyası, son kullanıcısı bırakınca (Unpin) silinir.</summary>
    private void DeleteUnpinned(List<Item> items)
    {
        List<Item> toDelete;
        lock (_gate)
            toDelete = [.. items.Where(i => i.Pins == 0)];
        foreach (var item in toDelete)
            DeleteFile(item);
    }

    private static void DeleteUnpinnedLocked(List<Item> items)
    {
        foreach (var item in items)
        {
            if (item.Pins == 0)
                DeleteFile(item);
        }
    }

    private string NewFilePath(ArchiveEntry entry)
    {
        Directory.CreateDirectory(_directory);
        // Uzantı korunur: medya oynatıcılar ve sistem işleyicileri türü uzantıdan da anlar.
        var extension = Path.GetExtension(entry.Name);
        if (extension.Length > 16 || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            extension = "";
        return Path.Combine(_directory, Guid.NewGuid().ToString("N") + extension);
    }

    private static void DeleteFile(Item item)
    {
        if (item.File is not null)
            TryDelete(item.File);
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

    internal readonly record struct Key(IArchiveReader Reader, int Index)
    {
        public bool Equals(Key other) => ReferenceEquals(Reader, other.Reader) && Index == other.Index;
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Reader), Index);
    }

    internal sealed class Item(Key key, ArchiveEntry entry, byte[]? memory, string? file, long length)
    {
        public Key Key { get; } = key;
        public ArchiveEntry Entry { get; } = entry;
        public byte[]? Memory { get; } = memory;
        public string? File { get; } = file;
        public long Length { get; } = length;

        /// <summary>Açık PreviewSource sayısı (kilit altında değişir).</summary>
        public int Pins { get; set; }

        /// <summary>Önbellekten çıkarıldı; dosyası son Unpin'de silinir.</summary>
        public bool Removed { get; set; }
    }

    /// <summary>Blok açılırken her girdiyi doğrudan önbelleğe yazar.</summary>
    private sealed class CacheReceiver(PreviewCache cache, IArchiveReader reader, CancellationToken ct) : IEntryReceiver
    {
        private readonly Dictionary<int, (Stream Stream, string? File)> _open = [];

        public Stream? Begin(ArchiveEntry entry)
        {
            if (ct.IsCancellationRequested || cache._disposed)
                return null;

            if (entry.Size <= cache._memoryEntryLimit)
            {
                var memory = new MemoryStream(entry.Size > 0 ? (int)entry.Size : 0);
                _open[entry.Index] = (memory, null);
                return memory;
            }

            var file = cache.NewFilePath(entry);
            var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            _open[entry.Index] = (stream, file);
            return stream;
        }

        public void End(ArchiveEntry entry, string? error)
        {
            if (!_open.Remove(entry.Index, out var target))
                return;

            var key = new Key(reader, entry.Index);
            if (error is not null || ct.IsCancellationRequested)
            {
                target.Stream.Dispose();
                if (target.File is not null)
                    TryDelete(target.File);
                return;
            }

            if (target.Stream is MemoryStream memory)
            {
                cache.Add(new Item(key, entry, memory.GetBuffer(), null, memory.Length), pin: false, ct);
                return;
            }

            var length = target.Stream.Length;
            target.Stream.Dispose();
            cache.Add(new Item(key, entry, null, target.File, length), pin: false, ct);
        }

        /// <summary>Yarıda kalan girdiler (iptal, motor hatası).</summary>
        public void DisposePending()
        {
            foreach (var (stream, file) in _open.Values)
            {
                stream.Dispose();
                if (file is not null)
                    TryDelete(file);
            }
            _open.Clear();
        }
    }
}
