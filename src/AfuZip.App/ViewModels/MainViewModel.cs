using System.Collections.ObjectModel;
using AfuZip.App.Services;
using AfuZip.Core;
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
    private readonly EngineRouter _router;
    private readonly SevenZipEngine _sevenZip;

    private ArchiveTabViewModel? _selectedTab;
    private string _status = "Bir arşiv açın (Ctrl+O) veya pencereye sürükleyin.";
    private bool _isBusy;

    public MainViewModel(ArchiveOpener opener, TaskQueue queue, UiPasswordProvider passwords, RecentArchives recent,
        EngineRouter router, SevenZipEngine sevenZip)
    {
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
            {
                NotifyTasks();
                // Durum çubuğu olmadığı için biten iş kısa bir bildirimle duyurulur.
                if (task.State == ArchiveTaskState.Completed)
                    Notice?.Invoke(task.StatusText, NoticeKind.Success);
                else if (task.State == ArchiveTaskState.Failed)
                    Notice?.Invoke($"{task.Title}: {task.StatusText}", NoticeKind.Error);
            }
            else if (e.PropertyName == nameof(ArchiveTask.Percent))
            {
                OnPropertyChanged(nameof(TasksPercent));
            }
        };
        NotifyTasks();
    }

    public ObservableCollection<ArchiveTabViewModel> Tabs { get; } = [];
    public ObservableCollection<ArchiveTask> Tasks { get; } = [];
    public ObservableCollection<RecentItem> Recent { get; } = [];

    public SevenZipEngine Engine => _sevenZip;

    public ArchiveTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Pencerenin altında kısa süre görünen bildirim (hata, biten iş, uyarı).</summary>
    public event Action<string, NoticeKind>? Notice;

    public void Report(string message, NoticeKind kind = NoticeKind.Info)
    {
        Status = message;
        Notice?.Invoke(message, kind);
    }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public bool HasTabs => Tabs.Count > 0;
    public bool IsWelcomeVisible => Tabs.Count == 0;
    public bool HasRecent => Recent.Count > 0;
    public bool HasNoRecent => Recent.Count == 0;
    public bool HasTasks => Tasks.Count > 0;
    public bool HasActiveTasks => Tasks.Any(t => t.IsActive);
    public bool HasNoActiveTasks => !HasActiveTasks;

    /// <summary>Etkin görevlerin ortalama ilerlemesi (durum çubuğundaki küçük çubuk).</summary>
    public double TasksPercent => Tasks.Where(t => t.IsActive).Select(t => t.Percent).DefaultIfEmpty(0).Average();
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
        var tab = new ArchiveTabViewModel(_opener, _queue, _passwords);
        tab.Notice += OnTabNotice;
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
            Report(ex.WasProvided ? "Parola yanlış; arşiv açılamadı." : "Parola girilmedi; arşiv açılamadı.", NoticeKind.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"Arşiv açılamadı: {path}", ex);
            Report($"{Path.GetFileName(path)} açılamadı: {AppLog.Describe(ex)}", NoticeKind.Error);
        }
        finally
        {
            IsBusy = false;
        }

        // Sekme arayüzü oluşturulurken hata olduysa koleksiyonda yarım bir sekme kalmasın.
        if (Tabs.Remove(tab))
            NotifyTabs();
        tab.Notice -= OnTabNotice;
        await tab.DisposeAsync();
        return null;
    }

    private void OnTabNotice(string message, NoticeKind kind) => Notice?.Invoke(message, kind);

    public async Task CloseTabAsync(ArchiveTabViewModel tab)
    {
        tab.Notice -= OnTabNotice;
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

    /// <summary>Oluşturma diyaloğu ayarlardaki varsayılan hazır ayarla ve "bitince aç" tercihiyle açılır.</summary>
    public CreateArchiveViewModel NewCreateArchive(IEnumerable<string> sources)
    {
        var viewModel = new CreateArchiveViewModel(sources, _sevenZip.IsAvailable, _sevenZip.HasZstd);
        var settings = SettingsStore.Current;
        var preferred = viewModel.Presets.ToList()
            .FindIndex(p => p.IsAvailable && string.Equals(p.Preset.Id, settings.DefaultPreset, StringComparison.OrdinalIgnoreCase));
        if (preferred >= 0 && preferred != viewModel.SelectedPresetIndex)
            viewModel.SelectedPresetIndex = preferred;
        viewModel.OpenWhenDone = settings.OpenArchiveWhenCreated;
        return viewModel;
    }

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
    }

    private void RememberRecent(string path)
    {
        Recent.Clear();
        foreach (var item in _recent.Add(path))
            Recent.Add(item);
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasNoRecent));
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(IsWelcomeVisible));
    }

    private void NotifyTasks()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(HasNoActiveTasks));
        OnPropertyChanged(nameof(TasksPercent));
        OnPropertyChanged(nameof(TasksHeader));
    }
}
