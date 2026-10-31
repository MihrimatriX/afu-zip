using System.Collections.ObjectModel;
using AfuZip.App.Services;
using AfuZip.Core;
using AfuZip.Core.Preview;
using AfuZip.Engine.SevenZip;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfuZip.App.ViewModels;

/// <summary>Pencere düzeyi: sekmeler, görev kuyruğu, son açılanlar.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ArchiveOpener _opener;
    private readonly TaskQueue _queue;
    private readonly UiPasswordProvider _passwords;
    private readonly RecentArchives _recent;
    private readonly PreviewCache _previewCache;
    private readonly EngineRouter _router;
    private readonly SevenZipEngine _sevenZip;

    private ArchiveTabViewModel? _selectedTab;
    private string _status = "Bir arşiv açın (Ctrl+O) veya pencereye sürükleyin.";
    private bool _isBusy;

    public MainViewModel(ArchiveOpener opener, TaskQueue queue, UiPasswordProvider passwords, RecentArchives recent,
        PreviewCache previewCache, EngineRouter router, SevenZipEngine sevenZip)
    {
        _previewCache = previewCache;
        _router = router;
        _sevenZip = sevenZip;
        _opener = opener;
        _queue = queue;
        _passwords = passwords;
        _recent = recent;

        foreach (var item in recent.Load())
            Recent.Add(item);

        queue.TaskAdded += OnTaskAdded;
    }

    private readonly List<ArchiveTask> _created = [];

    private void OnTaskAdded(object? sender, ArchiveTask task)
    {
        Tasks.Insert(0, task);
        task.PropertyChanged += (_, e) =>
        {
            // İlerleme bildirimleri saniyede ~10 gelir; başlık yalnızca durum değişince güncellenir.
            if (e.PropertyName == nameof(ArchiveTask.State))
                NotifyTasks();
        };
        NotifyTasks();
    }

    public ObservableCollection<ArchiveTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ArchiveTask> Tasks { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];

    public ArchiveTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void SetStatus(string status) => Status = status;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public bool HasTabs => Tabs.Count > 0;
    public bool IsWelcomeVisible => Tabs.Count == 0;
    public bool HasRecent => Recent.Count > 0;
    public bool HasTasks => Tasks.Count > 0;
    public string TasksHeader => _queue.ActiveCount is var active and > 0 ? $"Görevler · {active} etkin" : "Görevler";

    /// <summary>Arşiv zaten açıksa o sekmeye geçer; değilse yeni sekmede açar.</summary>
    public async Task<ArchiveTabViewModel?> OpenAsync(string path)
    {
        var existing = Tabs.FirstOrDefault(t => string.Equals(t.ArchivePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        IsBusy = true;
        Status = $"Açılıyor: {Path.GetFileName(path)}";
        var tab = new ArchiveTabViewModel(_opener, _queue, _passwords, _previewCache);
        try
        {
            await tab.LoadAsync(path);
            Tabs.Add(tab);
            SelectedTab = tab;
            Status = tab.Status;
            RememberRecent(path);
            NotifyTabs();
            return tab;
        }
        catch (ArchivePasswordException ex)
        {
            Status = ex.WasProvided ? "Parola yanlış; arşiv açılamadı." : "Parola girilmedi; arşiv açılamadı.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Açılamadı: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await tab.DisposeAsync();
        return null;
    }

    public async Task CloseTabAsync(ArchiveTabViewModel tab)
    {
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.LastOrDefault();
        NotifyTabs();
        await tab.DisposeAsync();
    }

    public void ClearFinishedTasks()
    {
        foreach (var task in _queue.RemoveFinished())
            Tasks.Remove(task);
        NotifyTasks();
    }

    // --- Oluşturma ---------------------------------------------------------------------------------

    public CreateArchiveViewModel NewCreateArchive(IEnumerable<string> sources) =>
        new(sources, _sevenZip.IsAvailable, _sevenZip.HasZstd);

    /// <summary>Arşivi görev kuyruğunda oluşturur; istenirse bitince yeni sekmede açar.</summary>
    public ArchiveTask CreateArchive(CreateRequest request, bool openWhenDone)
    {
        var ui = SynchronizationContext.Current;
        var name = Path.GetFileName(request.ArchivePath);
        var created = _queue.Enqueue($"Oluşturuluyor: {name}", async (task, ct) =>
        {
            var needed = Capability.Write | (request.Password is null ? Capability.None : Capability.Encrypt);
            var engine = _router.Resolve(request.Preset.Format, needed);
            await engine.CreateAsync(request, task, ct);

            // Ciltlere bölündüyse ilk cilt açılır (ad.7z.001).
            var first = request.VolumeSize is null ? request.ArchivePath : $"{request.ArchivePath}.001";
            var size = request.VolumeSize is null
                ? new FileInfo(first).Length
                : Directory.GetFiles(Path.GetDirectoryName(request.ArchivePath)!, Path.GetFileName(request.ArchivePath) + ".*")
                    .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f),
                        $@"^{System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(request.ArchivePath))}\.\d{{3}}$"))
                    .Sum(f => new FileInfo(f).Length);

            if (openWhenDone && ui is not null)
                ui.Post(_ => _ = OpenAsync(first), null);
            return $"{name} oluşturuldu · {ByteSize.Format(size)} · {engine.Name}";
        });
        _created.Add(created);
        return created;
    }

    public async Task CloseAllAsync()
    {
        // Yalnızca bu pencerenin işleri durur; sağ tık menüsünden başlayan işler ilerleme penceresinde sürer.
        _queue.TaskAdded -= OnTaskAdded;
        foreach (var task in _created.Where(t => t.IsActive))
            task.Cancel();
        // Pencere kapanırken çağrılır: sekme koleksiyonu temizlenmez, kapanan pencerenin
        // görsel ağacıyla uğraşmak yerine yalnızca okuyucular ve geçici dosyalar bırakılır.
        await Task.WhenAll(Tabs.ToList().Select(tab => tab.DisposeAsync().AsTask()));
        try
        {
            // İptal edilen oluşturma yarım arşivini silsin diye kısa süre beklenir.
            await Task.WhenAll(_created.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }
        _previewCache.Dispose(); // önizleme geçici klasörünü sil
    }

    private void RememberRecent(string path)
    {
        Recent.Clear();
        foreach (var item in _recent.Add(path))
            Recent.Add(item);
        OnPropertyChanged(nameof(HasRecent));
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(IsWelcomeVisible));
    }

    private void NotifyTasks()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(TasksHeader));
    }
}
