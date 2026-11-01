using System.Collections.ObjectModel;
using System.Diagnostics;
using AfuZip.App.Services;
using AfuZip.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfuZip.App.ViewModels;

public enum ExtractScope
{
    All,
    Selection,
}

/// <summary>Durum çubuğu yok: hatalar ve biten işler pencerenin altında kısa bir bildirimle gösterilir.</summary>
public enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Bir sekme: bir arşiv ve onun içinde açılmış iç içe arşivler (seviyeler). Her seviyenin kendi
/// klasör ağacı ve konumu vardır; breadcrumb seviyeleri ve klasörleri birlikte gösterir.
/// </summary>
public sealed class ArchiveTabViewModel(ArchiveOpener opener, TaskQueue tasks, IPasswordProvider passwords)
    : ObservableObject, IAsyncDisposable
{
    // Çift tıklamayla doğrudan çalıştırılmayacak türler: önce çıkarılmaları gerekir.
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".lnk", ".hta", ".cpl", ".reg",
    };

    private readonly List<Level> _levels = [];
    private readonly List<ArchiveTask> _myTasks = [];
    private bool _disposed;
    private CancellationTokenSource? _searchCts;
    private SortColumn _sort = SortColumn.Name;
    private bool _descending;
    private IReadOnlyList<EntryItem> _selection = [];

    private string _archiveInfo = "";
    private string _summary = "";
    private string _warning = "";
    private string _status = "";
    private string _searchText = "";
    private bool _isBusy;

    public string ArchivePath { get; private set; } = "";
    public string Title => Path.GetFileName(ArchivePath);

    public ObservableCollection<EntryItem> Items { get; } = [];
    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];
    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    public string ArchiveInfo { get => _archiveInfo; private set => SetProperty(ref _archiveInfo, value); }

    /// <summary>Tek satır özet: biçim, yöntem, solid, dosya sayısı, boyut ve oran (arşiv bilgisinde).</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>7-Zip'in açılış uyarısı (eksik cilt, yarım indirme): sekmenin üstünde kalıcı uyarı olarak gösterilir.</summary>
    public string Warning
    {
        get => _warning;
        private set
        {
            if (SetProperty(ref _warning, value))
                OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => _warning.Length > 0;

    /// <summary>Kullanıcının görmesi gereken bir sonuç (hata, uyarı).</summary>
    public event Action<string, NoticeKind>? Notice;

    private void Report(string message, NoticeKind kind = NoticeKind.Error)
    {
        Status = message;
        Notice?.Invoke(message, kind);
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                _ = SearchAsync(value);
        }
    }

    public bool IsSearching => _searchText.Trim().Length > 0;

    /// <summary>Liste boşken ortada gösterilen açıklama (boş klasör ya da sonuçsuz arama).</summary>
    public bool IsListEmpty => HasArchive && Items.Count == 0;
    public string EmptyText => IsSearching ? $"\"{_searchText.Trim()}\" bulunamadı" : "Bu klasör boş";
    public bool CanGoUp => _levels.Count > 1 || (_levels.Count == 1 && Current.Folder.Length > 0);
    public bool HasSelection => _selection.Count > 0;

    public string NameHeader => Header("Ad", SortColumn.Name);
    public string SizeHeader => Header("Boyut", SortColumn.Size);
    public string PackedHeader => Header("Sıkıştırılmış", SortColumn.PackedSize);
    public string RatioHeader => Header("Oran", SortColumn.Ratio);
    public string MethodHeader => Header("Yöntem", SortColumn.Method);
    public string ModifiedHeader => Header("Değiştirilme", SortColumn.Modified);

    private Level Current => _levels[^1];

    private OpenOptions OpenOptions => new(Passwords: passwords);

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        ArchivePath = path;
        OnPropertyChanged(nameof(Title));
        var sw = Stopwatch.StartNew();
        var reader = await opener.OpenAsync(path, OpenOptions, ct);
        Push(reader, reader.DisplayName);
        Status = WithWarnings(reader, $"{reader.Entries.Count:N0} girdi · {sw.ElapsedMilliseconds:N0} ms · {reader.EngineName} motoru");
    }

    // --- Gezinme ---------------------------------------------------------------------------------

    public void Navigate(string folder)
    {
        if (_levels.Count == 0)
            return;
        if (!Current.Tree.FolderExists(folder))
            folder = "";

        Current.Folder = folder;
        _searchCts?.Cancel();
        if (_searchText.Length > 0)
        {
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
        }
        RefreshItems();
        RebuildBreadcrumb();
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(IsSearching));
    }

    public async Task NavigateToSegmentAsync(BreadcrumbSegment segment)
    {
        while (_levels.Count - 1 > segment.Level)
            await PopLevelAsync();
        Navigate(segment.Folder);
    }

    public async Task GoUpAsync()
    {
        if (_levels.Count == 0)
            return;
        if (Current.Folder.Length > 0)
        {
            Navigate(ArchiveTree.Parent(Current.Folder));
        }
        else if (_levels.Count > 1)
        {
            await PopLevelAsync();
            Navigate(Current.Folder);
        }
    }

    public void SortBy(SortColumn column)
    {
        if (_sort == column)
        {
            _descending = !_descending;
        }
        else
        {
            _sort = column;
            _descending = column is SortColumn.Size or SortColumn.PackedSize or SortColumn.Modified;
        }

        if (IsSearching && HasArchive)
            _ = SearchAsync(_searchText);
        else
            RefreshItems();

        foreach (var name in new[] { nameof(NameHeader), nameof(SizeHeader), nameof(PackedHeader), nameof(RatioHeader), nameof(MethodHeader), nameof(ModifiedHeader) })
            OnPropertyChanged(name);
    }

    /// <summary>Çift tıklama / Enter: klasöre gir, iç arşivi aç ya da dosyayı varsayılan uygulamayla aç.</summary>
    public async Task ActivateAsync(EntryItem item)
    {
        if (item.IsFolder)
        {
            Navigate(item.Path.TrimEnd('/'));
            return;
        }
        if (item.Entry is not { } entry)
            return;

        if (item.IsArchive)
            await OpenNestedAsync(entry);
        else
            await OpenWithDefaultAppAsync(entry);
    }

    private async Task OpenNestedAsync(ArchiveEntry entry)
    {
        IsBusy = true;
        Status = $"Açılıyor: {entry.Name}";
        try
        {
            var inner = await opener.OpenNestedAsync(Current.Reader, entry.Index, OpenOptions);
            Push(inner, entry.Name);
            Status = WithWarnings(inner, $"{entry.Name}: {inner.Entries.Count:N0} girdi · {inner.FormatName}");
        }
        catch (ArchivePasswordException ex)
        {
            Report(ex.WasProvided ? "Parola yanlış." : "Parola girilmedi.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"İç arşiv açılamadı: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenWithDefaultAppAsync(ArchiveEntry entry)
    {
        if (ExecutableExtensions.Contains(Path.GetExtension(entry.Name)))
        {
            Report($"{entry.Name} bir program veya betik; güvenlik için doğrudan açılmaz. Önce çıkarın.", NoticeKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var dir = Path.Combine(TempFolders.OpenRoot, Guid.NewGuid().ToString("N"));
            var result = await Current.Reader.ExtractAsync([entry.Index], new ExtractionOptions(dir) { Passwords = passwords });
            if (result.Extracted != 1)
            {
                Report($"{entry.Name} açılamadı: {result.Failed.FirstOrDefault()?.Reason ?? result.Blocked.FirstOrDefault()?.Reason}");
                return;
            }
            var file = SafePath.ResolveTarget(dir, entry.Path);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            if (CanEdit)
                WatchExternalEdit(entry.Path, file);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or ArchivePasswordException)
        {
            Report($"{entry.Name} açılamadı: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Seçim ------------------------------------------------------------------------------------

    public void Select(IReadOnlyList<EntryItem> selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        UpdateSelectionStatus();
    }

    // --- Görevler --------------------------------------------------------------------------------

    /// <summary>Sekmede açık bir arşiv var (düzenleme sırasında kısa süre, yeniden açma başarısızsa kalıcı olarak yok).</summary>
    public bool HasArchive => _levels.Count > 0;

    /// <summary>Arşivin yanına akıllı çıkarır: tek kök klasör varsa doğrudan, yoksa arşiv adıyla klasöre.</summary>
    public ArchiveTask? ExtractHere(ExtractScope scope)
    {
        if (!HasArchive)
            return null;
        var (indices, entries) = ResolveScope(scope);
        var baseDirectory = Path.GetDirectoryName(ArchivePath)!;
        var destination = SettingsStore.Current.ExtractHere == ExtractHereMode.AlwaysFolder
            ? SmartExtract.FolderFor(Current.Label, baseDirectory)
            : SmartExtract.ResolveDestination(Current.Label, entries, baseDirectory);
        return Extract(indices, destination);
    }

    public ArchiveTask? ExtractTo(ExtractScope scope, string destination) =>
        HasArchive ? Extract(ResolveScope(scope).Indices, destination) : null;

    public ArchiveTask? Test()
    {
        if (!HasArchive)
            return null;
        var level = Current;
        return Track(tasks.Enqueue($"Test: {level.Label}", async (progress, ct) =>
        {
            var result = await WithTaskReaderAsync(level, reader => reader.TestAsync(null, progress, ct), ct);
            return result.Success
                ? result.ToString()
                : $"{result} İlk hata: {result.Failed[0].EntryPath}: {result.Failed[0].Reason}";
        }));
    }

    private ArchiveTask Extract(IReadOnlyList<int>? indices, string destination)
    {
        var level = Current;
        var title = indices is null ? $"Çıkar: {level.Label}" : $"Çıkar: {indices.Count:N0} öğe ({level.Label})";
        // İndeksler değil yollar saklanır: görev sırada beklerken arşiv düzenlenirse (silme indeksleri kaydırır)
        // görev çalıştığı andaki dosyada doğru girdileri bulur.
        var paths = indices?.Select(i => level.Reader.Entries[i].Path).ToHashSet(StringComparer.Ordinal);
        return Track(tasks.Enqueue(title, async (progress, ct) =>
        {
            var options = new ExtractionOptions(destination) { Passwords = passwords };
            var result = await WithTaskReaderAsync(level, reader =>
            {
                IReadOnlyCollection<int>? current = paths is null
                    ? null
                    : [.. reader.Entries.Where(e => paths.Contains(e.Path)).Select(e => e.Index)];
                return reader.ExtractAsync(current, options, progress, ct);
            }, ct);

            if (result.Failed.Count == 0 && SettingsStore.Current.RevealAfterExtract && Directory.Exists(destination))
                Notifications.Reveal(destination);

            var summary = $"{result} → {destination}";
            if (result.Blocked.Count > 0)
                summary += $" Engellenen: {result.Blocked[0].EntryPath} ({result.Blocked[0].Reason})";
            if (result.Failed.Count > 0)
                summary += $" İlk hata: {result.Failed[0].EntryPath}: {result.Failed[0].Reason}";
            return summary;
        }));
    }

    /// <summary>
    /// En üst seviyedeki gerçek dosya için görevlere ayrı bir okuyucu açılır; böylece uzun bir çıkarma
    /// sürerken sekmede gezinme beklemez. İç içe arşivlerde mevcut okuyucu kullanılır.
    /// </summary>
    private async Task<T> WithTaskReaderAsync<T>(Level level, Func<IArchiveReader, Task<T>> work, CancellationToken ct)
    {
        if (!string.Equals(level.Reader.ArchivePath, ArchivePath, StringComparison.OrdinalIgnoreCase))
            return await work(level.Reader);

        await using var fresh = await opener.OpenAsync(ArchivePath, OpenOptions, ct);
        return await work(fresh);
    }

    private (IReadOnlyList<int>? Indices, IEnumerable<ArchiveEntry> Entries) ResolveScope(ExtractScope scope)
    {
        if (scope == ExtractScope.All || _selection.Count == 0)
            return (null, Current.Reader.Entries);

        var indices = Current.Tree.CollectIndices(_selection.Select(s => s.Item));
        return (indices, indices.Select(i => Current.Reader.Entries[i]));
    }

    private ArchiveTask Track(ArchiveTask task)
    {
        _myTasks.Add(task);
        return task;
    }

    // --- Düzenleme (Faz 3) -----------------------------------------------------------------------

    /// <summary>
    /// Arşiv yerinde düzenlenebilir mi: 7-Zip motoru, yazılabilir biçim (7z, zip, tar, wim), en üst seviye
    /// (iç içe veya tar.xz gibi sarmalanmış değil), ciltsiz ve hatasız.
    /// </summary>
    public bool CanEdit =>
        _levels.Count == 1 &&
        Current.Reader is var reader &&
        string.Equals(reader.ArchivePath, ArchivePath, StringComparison.OrdinalIgnoreCase) &&
        reader.Format is ArchiveFormat.SevenZip or ArchiveFormat.Zip or ArchiveFormat.Tar or ArchiveFormat.Wim &&
        reader.Warnings.Count == 0 &&
        !System.Text.RegularExpressions.Regex.IsMatch(ArchivePath, @"\.\d{3}$") &&
        opener.Router.TryResolve(reader.Format, Capability.Update) is not null;

    public bool CanDelete => CanEdit && _selection.Count > 0;

    /// <summary>Eklenecek dosyalardan arşivin o anki klasöründe zaten bulunanlar (kullanıcıya sormak için).</summary>
    public async Task<IReadOnlyList<string>> FindAddConflictsAsync(IReadOnlyList<string> paths)
    {
        if (!HasArchive)
            return [];
        var entries = Current.Reader.Entries;
        var folder = Current.Folder;
        var sources = await Task.Run(() => SourceCollector.Collect(paths));
        return ArchiveUpdatePlanner.FindConflicts(entries, sources, folder);
    }
    public bool CanRename => CanEdit && _selection.Count == 1;

    public ArchiveTask DeleteSelection()
    {
        var paths = _selection.Select(s => s.Path).ToList();
        return Edit($"Siliniyor: {paths.Count:N0} öğe", new ArchiveUpdate { Delete = paths },
            $"{paths.Count:N0} öğe silindi");
    }

    public ArchiveTask Rename(EntryItem item, string newName) =>
        Edit($"Yeniden adlandırılıyor: {item.Item.Name}",
            new ArchiveUpdate { Rename = new Dictionary<string, string> { [item.Path] = newName } },
            $"{item.Item.Name} → {newName}");

    /// <summary>Diskten dosya veya klasörleri o anki klasöre ekler; aynı adlı dosyalarda ne yapılacağı <paramref name="onConflict"/>.</summary>
    public ArchiveTask Add(IReadOnlyList<string> paths, ConflictPolicy onConflict = ConflictPolicy.Replace)
    {
        var folder = Current.Folder;
        var where = folder.Length == 0 ? "köke" : $"{folder}/ klasörüne";
        return Edit($"Ekleniyor: {paths.Count:N0} öğe",
            new ArchiveUpdate { Add = paths, AddToFolder = folder, OnConflict = onConflict },
            $"{paths.Count:N0} öğe {where} eklendi");
    }

    /// <summary>
    /// Değişiklik geçici dosyaya yazılır; bittiğinde sekmenin okuyucuları kapatılır, yeni arşiv eskisinin yerine
    /// konur ve aynı klasörde yeniden açılır. Yazma yarıda kalırsa eski arşiv hiç değişmez.
    /// </summary>
    private ArchiveTask Edit(string title, ArchiveUpdate update, string done)
    {
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Düzenleme UI thread'inden başlatılmalı.");
        var engine = opener.Router.Resolve(Current.Reader.Format, Capability.Update);
        var path = ArchivePath;

        return Track(tasks.Enqueue(title, async (progress, ct) =>
        {
            using var pending = await engine.PrepareUpdateAsync(path, update, OpenOptions, progress, ct);
            var folder = "";
            await OnUiAsync(ui, async () =>
            {
                folder = _levels.Count > 0 ? Current.Folder : "";
                await CloseLevelsAsync();
            }, ct);
            try
            {
                pending.Commit();
            }
            finally
            {
                // Commit başarısız olsa da (dosya başka programda açık) sekme eski arşivle yeniden açılır.
                // Kapanış sırasında UI artık yanıt vermiyorsa beklenmez.
                await OnUiAsync(ui, () => ReloadAsync(folder), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            }
            return done;
        }));
    }

    private async Task CloseLevelsAsync()
    {
        try
        {
            for (var i = _levels.Count - 1; i >= 0; i--)
                await _levels[i].Reader.DisposeAsync();
        }
        finally
        {
            // Biri kapanırken hata verse de sekme "arşiv yok" durumuna tutarlı geçer.
            _levels.Clear();
            Items.Clear();
            Breadcrumb.Clear();
            FolderRoots.Clear();
            _selection = [];
            NotifyEditState();
        }
    }

    private async Task ReloadAsync(string folder)
    {
        if (_disposed)
            return; // düzenleme sürerken sekme kapatıldı
        try
        {
            await LoadAsync(ArchivePath);
            Navigate(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report($"Arşiv yeniden açılamadı: {ex.Message}");
        }
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasArchive));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Görev thread'inden UI thread'inde iş çalıştırıp bitmesini bekler.</summary>
    private static Task OnUiAsync(SynchronizationContext ui, Func<Task> action, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try
            {
                await action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        // Uygulama kapanırken dağıtıcı gönderiyi düşürebilir; görev sonsuza dek beklemesin.
        return done.Task.WaitAsync(ct);
    }

    // --- Dışarı sürükleme -------------------------------------------------------------------------

    /// <summary>
    /// Explorer'a sürüklenen öğeler bırakıldığı anda geçici klasöre çıkarılır; Explorer dosyaları oradan kopyalar.
    /// Klasör yapısı korunur, sürüklenen en üst öğelerin yolları döner.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExtractForDragAsync(IReadOnlyList<EntryItem> items, CancellationToken ct = default)
    {
        if (!HasArchive || items.Count == 0)
            return [];
        var level = Current;
        var dir = Path.Combine(TempFolders.DragRoot, Guid.NewGuid().ToString("N"));
        var indices = level.Tree.CollectIndices(items.Select(i => i.Item));
        var result = await level.Reader.ExtractAsync(indices, new ExtractionOptions(dir) { Passwords = passwords }, null, ct);
        if (result.Failed.Count > 0)
            Status = $"Sürüklenen {result.Failed.Count:N0} öğe çıkarılamadı: {result.Failed[0].Reason}";
        return [.. items.Select(i => SafePath.ResolveTarget(dir, i.Path.TrimEnd('/'))).Where(p => File.Exists(p) || Directory.Exists(p))];
    }

    // --- Dışarıda düzenleme ----------------------------------------------------------------------

    private readonly List<ExternalEdit> _edits = [];

    /// <summary>Varsayılan uygulamada açılıp kaydedilen, arşive henüz yazılmamış dosyalar.</summary>
    public bool HasPendingEdits => _edits.Any(e => e.IsPending);

    public string PendingEditsMessage => _edits.Where(e => e.IsPending).ToList() switch
    {
        [] => "",
        [var only] => $"\"{Path.GetFileName(only.EntryPath)}\" başka bir programda değiştirildi. Arşivdeki dosya güncellensin mi?",
        var many => $"{many.Count:N0} dosya başka programlarda değiştirildi. Arşivdekiler güncellensin mi?",
    };

    private void WatchExternalEdit(string entryPath, string file)
    {
        if (_edits.Any(e => string.Equals(e.FilePath, file, StringComparison.OrdinalIgnoreCase)))
            return;
        var ui = SynchronizationContext.Current;
        var edit = new ExternalEdit(entryPath, file);
        // Düzenleyiciler genelde geçici dosyaya yazıp yeniden adlandırır: Created/Renamed de izlenir.
        void OnChanged(object? sender, FileSystemEventArgs e) => ui?.Post(_ => _ = CheckExternalEditAsync(edit), null);
        edit.Watcher.Changed += OnChanged;
        edit.Watcher.Created += OnChanged;
        edit.Watcher.Renamed += (sender, e) => OnChanged(sender, e);
        _edits.Add(edit);
    }

    private async Task CheckExternalEditAsync(ExternalEdit edit)
    {
        if (edit.IsChecking || _disposed)
            return;
        edit.IsChecking = true;
        try
        {
            // Kaydetme birkaç olay üretir ve dosya bir süre kilitli kalır: durulmasını bekle.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(700);
                if (edit.IsUnlockedAndChanged())
                {
                    edit.IsPending = true;
                    NotifyEdits();
                    return;
                }
            }
        }
        finally
        {
            edit.IsChecking = false;
        }
    }

    /// <summary>Değişen dosyalar arşivdeki yerlerine (aynı klasör, aynı ad) yazılır.</summary>
    public void ApplyExternalEdits()
    {
        if (!CanEdit)
        {
            Report("Arşiv şu an düzenlenemiyor (başka bir değişiklik sürüyor olabilir); biraz sonra tekrar deneyin.", NoticeKind.Warning);
            return;
        }
        var pending = _edits.Where(e => e.IsPending).ToList();
        foreach (var group in pending.GroupBy(e => ArchiveTree.Parent(e.EntryPath)))
        {
            var files = group.Select(e => e.FilePath).ToList();
            var names = string.Join(", ", group.Select(e => Path.GetFileName(e.EntryPath)));
            Edit($"Güncelleniyor: {names}",
                new ArchiveUpdate { Add = files, AddToFolder = group.Key, OnConflict = ConflictPolicy.Replace },
                $"{names} arşivde güncellendi");
        }
        foreach (var edit in pending)
            edit.Accept();
        NotifyEdits();
    }

    public void DismissExternalEdits()
    {
        foreach (var edit in _edits.Where(e => e.IsPending))
            edit.Accept();
        NotifyEdits();
    }

    private void NotifyEdits()
    {
        OnPropertyChanged(nameof(HasPendingEdits));
        OnPropertyChanged(nameof(PendingEditsMessage));
    }

    private sealed class ExternalEdit : IDisposable
    {
        private DateTime _stamp;
        private long _length;

        public ExternalEdit(string entryPath, string filePath)
        {
            EntryPath = entryPath;
            FilePath = filePath;
            Accept();
            Watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
        }

        public string EntryPath { get; }
        public string FilePath { get; }
        public FileSystemWatcher Watcher { get; }
        public bool IsPending { get; set; }
        public bool IsChecking { get; set; }

        /// <summary>Şimdiki hali "bilinen" kabul edilir (yazıldı ya da yok sayıldı).</summary>
        public void Accept()
        {
            IsPending = false;
            var info = new FileInfo(FilePath);
            (_stamp, _length) = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1);
        }

        public bool IsUnlockedAndChanged()
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || (info.LastWriteTimeUtc == _stamp && info.Length == _length))
                return false;
            try
            {
                using var _ = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException)
            {
                return false; // hâlâ yazılıyor
            }
        }

        public void Dispose() => Watcher.Dispose();
    }

    // --- Arama -----------------------------------------------------------------------------------

    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsSearching));
        if (!HasArchive)
            return;

        if (query.Trim().Length == 0)
        {
            RefreshItems();
            RebuildBreadcrumb();
            return;
        }

        try
        {
            await Task.Delay(200, cts.Token); // yazarken her tuşta aramayalım
            var tree = Current.Tree;
            var results = await Task.Run(() => tree.Search(query), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            Items.Clear();
            foreach (var item in results)
                Items.Add(new EntryItem(item, showFullPath: true));
            Status = results.Count == 0 ? $"\"{query}\" bulunamadı." : $"\"{query}\": {results.Count:N0} sonuç";
            NotifyListState();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // --- Yardımcılar -----------------------------------------------------------------------------

    private void Push(IArchiveReader reader, string label)
    {
        _levels.Add(new Level(reader, label));
        NotifyEditState();
        ArchiveInfo = DescribeArchive(reader);
        Summary = Summarize(reader);
        Warning = reader.Warnings.FirstOrDefault() ?? "";
        RebuildFolderTree();
        Navigate("");
    }

    private async Task PopLevelAsync()
    {
        var top = _levels[^1];
        _levels.RemoveAt(_levels.Count - 1);
        await top.Reader.DisposeAsync();
        NotifyEditState();
        ArchiveInfo = DescribeArchive(Current.Reader);
        Summary = Summarize(Current.Reader);
        Warning = Current.Reader.Warnings.FirstOrDefault() ?? "";
        RebuildFolderTree();
    }

    private void RefreshItems()
    {
        Items.Clear();
        if (!HasArchive)
            return; // düzenleme sırasında arşiv kısa süre kapalı
        foreach (var item in Current.Tree.Children(Current.Folder, _sort, _descending))
            Items.Add(new EntryItem(item));
        _selection = [];
        OnPropertyChanged(nameof(HasSelection));
        NotifyListState();
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        for (var level = 0; level < _levels.Count; level++)
        {
            Breadcrumb.Add(new BreadcrumbSegment(_levels[level].Label, level, ""));
            var folder = _levels[level].Folder;
            if (folder.Length == 0)
                continue;

            var path = "";
            foreach (var segment in folder.Split('/'))
            {
                path = path.Length == 0 ? segment : $"{path}/{segment}";
                Breadcrumb.Add(new BreadcrumbSegment(segment, level, path));
            }
        }
    }

    private void RebuildFolderTree()
    {
        FolderRoots.Clear();
        FolderRoots.Add(new FolderNodeViewModel(Current.Tree.Root, Current.Label, expanded: true));
    }

    private void UpdateSelectionStatus()
    {
        if (_selection.Count == 0)
            return;
        var size = _selection.Sum(s => s.Item.Size);
        Status = $"{_selection.Count:N0} öğe seçili · {ByteSize.Format(size)}";
    }

    private string Header(string label, SortColumn column) =>
        _sort == column ? $"{label} {(_descending ? "▼" : "▲")}" : label;

    /// <summary>Durum satırına 7-Zip'in açılış uyarısını ekler (eksik cilt gibi sorunlar gözden kaçmasın).</summary>
    private static string WithWarnings(IArchiveReader reader, string status) =>
        reader.Warnings.Count == 0 ? status : $"{status} · ⚠ {reader.Warnings[0]}";

    private static string Summarize(IArchiveReader reader)
    {
        var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
        var parts = new List<string> { reader.FormatName };
        // En çok kullanılan yöntem (7z'de "LZMA2:24" gibi sözlük boyutu da gelir; özet için kısaltılmaz).
        if (files.Where(f => !string.IsNullOrEmpty(f.Method)).GroupBy(f => f.Method).MaxBy(g => g.Count())?.Key is { } method)
            parts.Add(method);
        if (reader.IsSolid)
            parts.Add("solid");
        if (files.Any(f => f.IsEncrypted))
            parts.Add("şifreli");
        parts.Add($"{files.Count:N0} dosya");

        var total = files.Sum(f => f.Size);
        long? archiveSize = null;
        try
        {
            archiveSize = new FileInfo(reader.ArchivePath).Length;
        }
        catch (IOException)
        {
        }
        parts.Add(archiveSize is { } size && total > 0
            ? $"{ByteSize.Format(total)} → {ByteSize.Format(size)} (%{100.0 * size / total:0})"
            : ByteSize.Format(total));
        return string.Join("  ·  ", parts);
    }

    private static string DescribeArchive(IArchiveReader reader)
    {
        var files = reader.Entries.Count(e => !e.IsDirectory);
        var total = reader.Entries.Sum(e => e.Size);
        var encrypted = reader.Entries.Any(e => e.IsEncrypted);
        var lines = new List<string>
        {
            $"Format: {reader.FormatName}",
            $"Motor: {reader.EngineName}",
            $"Dosya: {files:N0}",
            $"Toplam: {ByteSize.Format(total)}",
            $"Solid: {(reader.IsSolid ? "evet" : "hayır")}",
            $"Şifreli girdi: {(encrypted ? "var" : "yok")}",
        };
        foreach (var warning in reader.Warnings)
            lines.Add($"⚠ {warning}");
        try
        {
            lines.Insert(4, $"Arşiv: {ByteSize.Format(new FileInfo(reader.ArchivePath).Length)}");
        }
        catch (IOException)
        {
        }
        return string.Join('\n', lines);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var edit in _edits)
            edit.Dispose();
        _edits.Clear();
        _searchCts?.Cancel();

        // Bu sekmenin okuyucusunu kullanan görevler bitmeden okuyucu kapatılmamalı.
        foreach (var task in _myTasks.Where(t => t.IsActive))
            task.Cancel();
        try
        {
            await Task.WhenAll(_myTasks.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }

        for (var i = _levels.Count - 1; i >= 0; i--)
            await _levels[i].Reader.DisposeAsync();
        _levels.Clear();
    }

    private sealed class Level(IArchiveReader reader, string label)
    {
        public IArchiveReader Reader { get; } = reader;
        public ArchiveTree Tree { get; } = new(reader.Entries);
        public string Label { get; } = label;
        public string Folder { get; set; } = "";
    }
}

internal static class TempFolders
{
    public static string OpenRoot => Path.Combine(Path.GetTempPath(), "AfuZip", "open");

    public static string DragRoot => Path.Combine(Path.GetTempPath(), "AfuZip", "drag");

    // 0.5 ve öncesinin önizleme önbelleği; artık kullanılmıyor, kalıntısı silinir.
    private static string LegacyPreviewRoot => Path.Combine(Path.GetTempPath(), "AfuZip", "preview");

    public static void CleanupStale(TimeSpan olderThan)
    {
        NestedArchive.CleanupStale(olderThan);
        try
        {
            if (Directory.Exists(LegacyPreviewRoot))
                Directory.Delete(LegacyPreviewRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var dir in new[] { OpenRoot, DragRoot }.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > olderThan)
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
