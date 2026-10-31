using System.Collections.ObjectModel;
using System.Diagnostics;
using AfuZip.Core;
using AfuZip.Core.Preview;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfuZip.App.ViewModels;

public enum ExtractScope
{
    All,
    Selection,
}

/// <summary>
/// Bir sekme: bir arşiv ve onun içinde açılmış iç içe arşivler (seviyeler). Her seviyenin kendi
/// klasör ağacı ve konumu vardır; breadcrumb seviyeleri ve klasörleri birlikte gösterir.
/// </summary>
public sealed class ArchiveTabViewModel(ArchiveOpener opener, TaskQueue tasks, IPasswordProvider passwords, PreviewCache previewCache)
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
    private string _status = "";
    private string _searchText = "";
    private bool _isBusy;
    private bool _isPreviewVisible = true;

    public string ArchivePath { get; private set; } = "";
    public string Title => Path.GetFileName(ArchivePath);

    public ObservableCollection<EntryItem> Items { get; } = [];
    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = [];
    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    public string ArchiveInfo { get => _archiveInfo; private set => SetProperty(ref _archiveInfo, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    /// <summary>Sekmenin önizleme paneli; seçim değişince o girdiyi gösterir.</summary>
    public PreviewViewModel Preview { get; } = new(previewCache);

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set
        {
            // Panel açılınca seçili dosya önizlenir, kapanınca açık önizleme (video, WebView) bırakılır.
            if (SetProperty(ref _isPreviewVisible, value))
                _ = UpdatePreviewAsync();
        }
    }

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
            Status = ex.WasProvided ? "Parola yanlış." : "Parola girilmedi.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"İç arşiv açılamadı: {ex.Message}";
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
            Status = $"{entry.Name} bir program veya betik; güvenlik için doğrudan açılmaz. Önce çıkarın.";
            return;
        }

        IsBusy = true;
        try
        {
            var dir = Path.Combine(TempFolders.OpenRoot, Guid.NewGuid().ToString("N"));
            var result = await Current.Reader.ExtractAsync([entry.Index], new ExtractionOptions(dir) { Passwords = passwords });
            if (result.Extracted != 1)
            {
                Status = $"{entry.Name} açılamadı: {result.Failed.FirstOrDefault()?.Reason ?? result.Blocked.FirstOrDefault()?.Reason}";
                return;
            }
            Process.Start(new ProcessStartInfo(SafePath.ResolveTarget(dir, entry.Path)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or ArchivePasswordException)
        {
            Status = $"{entry.Name} açılamadı: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Seçim ve önizleme -----------------------------------------------------------------------

    public async Task SelectAsync(IReadOnlyList<EntryItem> selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRename));
        UpdateSelectionStatus();
        await UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_levels.Count == 0 || _selection.Count != 1 || !IsPreviewVisible)
        {
            Preview.Clear();
            return;
        }

        var item = _selection[0];
        if (item.IsFolder)
            Preview.ShowFolder(item.Name, Current.Tree.StatsOf(item.Path.TrimEnd('/')));
        else if (item.Entry is { } entry)
            await Preview.ShowAsync(Current.Reader, entry);
        else
            Preview.Clear();
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
        var destination = SmartExtract.ResolveDestination(Current.Label, entries, Path.GetDirectoryName(ArchivePath)!);
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
    /// sürerken sekmede gezinme ve önizleme beklemez. İç içe arşivlerde mevcut okuyucu kullanılır.
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
            Preview.Clear();
            for (var i = _levels.Count - 1; i >= 0; i--)
            {
                Preview.Release(_levels[i].Reader);
                await _levels[i].Reader.DisposeAsync();
            }
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
            Status = $"Arşiv yeniden açılamadı: {ex.Message}";
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
        RebuildFolderTree();
        Navigate("");
    }

    private async Task PopLevelAsync()
    {
        var top = _levels[^1];
        _levels.RemoveAt(_levels.Count - 1);
        Preview.Release(top.Reader);
        await top.Reader.DisposeAsync();
        NotifyEditState();
        ArchiveInfo = DescribeArchive(Current.Reader);
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
        ClearPreview();
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

    private void ClearPreview() => Preview.Clear();

    private string Header(string label, SortColumn column) =>
        _sort == column ? $"{label} {(_descending ? "▼" : "▲")}" : label;

    /// <summary>Durum satırına 7-Zip'in açılış uyarısını ekler (eksik cilt gibi sorunlar gözden kaçmasın).</summary>
    private static string WithWarnings(IArchiveReader reader, string status) =>
        reader.Warnings.Count == 0 ? status : $"{status} · ⚠ {reader.Warnings[0]}";

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
        Preview.Clear();
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
        {
            Preview.Release(_levels[i].Reader);
            await _levels[i].Reader.DisposeAsync();
        }
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

    public static void CleanupStale(TimeSpan olderThan)
    {
        NestedArchive.CleanupStale(olderThan);
        if (!Directory.Exists(OpenRoot))
            return;
        foreach (var dir in Directory.EnumerateDirectories(OpenRoot))
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
