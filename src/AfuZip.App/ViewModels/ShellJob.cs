using System.ComponentModel;
using AfuZip.App.Services;
using AfuZip.Core;
using AfuZip.Engine.SevenZip;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfuZip.App.ViewModels;

/// <summary>Sağ tık menüsünden başlatılan bir iş: görev kuyruğundaki görev ve bitince gösterilecek yer.</summary>
public sealed class ShellJob : ObservableObject
{
    private ArchiveTask _task = null!;

    public ArchiveTask Task
    {
        get => _task;
        internal set
        {
            _task = value;
            value.PropertyChanged += OnTaskChanged;
        }
    }

    /// <summary>Çıkarılan klasör veya oluşturulan arşiv; görev bittiğinde "Klasörü aç" ile gösterilir.</summary>
    public string? RevealPath { get; internal set; }

    /// <summary>Bildirimde kullanılacak kısa ad (arşivin veya kaynakların adı).</summary>
    public string Subject { get; internal init; } = "";

    public string Verb { get; internal init; } = "";

    public bool CanReveal => Task.State == ArchiveTaskState.Completed && RevealPath is { } path && (Directory.Exists(path) || File.Exists(path));

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveTask.State))
            OnPropertyChanged(nameof(CanReveal));
    }
}

/// <summary>Sağ tık komutlarının işleri: pencere açmadan arşivi çıkarır, test eder ya da sıkıştırır.</summary>
public sealed class ShellActions(ArchiveOpener opener, TaskQueue queue, UiPasswordProvider passwords, EngineRouter router,
    SevenZipEngine sevenZip)
{
    public ShellJob Extract(string archivePath, bool toNewFolder)
    {
        var name = Path.GetFileName(archivePath);
        var job = new ShellJob { Subject = name, Verb = "çıkarıldı" };
        job.Task = queue.Enqueue($"Çıkarılıyor: {name}", async (task, ct) =>
        {
            await using var reader = await opener.OpenAsync(archivePath, new OpenOptions(Passwords: passwords), ct);
            var baseDirectory = Path.GetDirectoryName(archivePath)!;
            var destination = toNewFolder || SettingsStore.Current.ExtractHere == ExtractHereMode.AlwaysFolder
                ? SmartExtract.FolderFor(name, baseDirectory)
                : SmartExtract.ResolveDestination(name, reader.Entries, baseDirectory);

            var result = await reader.ExtractAsync(null, new ExtractionOptions(destination) { Passwords = passwords }, task, ct);
            // Tek kök klasör arşivin yanına çıktıysa klasörün kendisi gösterilir, üst klasör değil.
            job.RevealPath = destination == baseDirectory && SingleRoot(reader.Entries) is { } root
                ? Path.Combine(baseDirectory, SafePath.SanitizeSegment(root))
                : destination;

            if (result.Failed.Count > 0)
                throw new IOException($"{result} İlk hata: {result.Failed[0].EntryPath}: {result.Failed[0].Reason}");
            if (SettingsStore.Current.RevealAfterExtract)
                Notifications.Reveal(job.RevealPath);
            return result.Blocked.Count > 0
                ? $"{result} Engellenen: {result.Blocked[0].EntryPath} ({result.Blocked[0].Reason})"
                : $"{result} → {job.RevealPath}";
        });
        return job;
    }

    public ShellJob Test(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        var job = new ShellJob { Subject = name, Verb = "test edildi, hata yok" };
        job.Task = queue.Enqueue($"Test: {name}", async (task, ct) =>
        {
            await using var reader = await opener.OpenAsync(archivePath, new OpenOptions(Passwords: passwords), ct);
            var result = await reader.TestAsync(null, task, ct);
            if (!result.Success)
                throw new InvalidDataException($"{result} İlk hata: {result.Failed[0].EntryPath}: {result.Failed[0].Reason}");
            return result.ToString();
        });
        return job;
    }

    public ShellJob Compress(IReadOnlyList<string> sources, string? presetId)
    {
        var preset = CompressionPreset.ForEngines(presetId, sevenZip.IsAvailable, sevenZip.HasZstd);
        var target = ArchiveNaming.Unique(ArchiveNaming.DefaultTarget(sources, ArchiveNaming.ExtensionFor(preset)));
        var name = Path.GetFileName(target);
        var job = new ShellJob { Subject = name, Verb = "oluşturuldu", RevealPath = target };
        job.Task = queue.Enqueue($"Sıkıştırılıyor: {name}", async (task, ct) =>
        {
            var engine = router.Resolve(preset.Format, Capability.Write);
            await engine.CreateAsync(new CreateRequest(target, sources, preset), task, ct);
            return $"{name} oluşturuldu · {ByteSize.Format(new FileInfo(target).Length)} · {preset.DisplayName}";
        });
        return job;
    }

    private static string? SingleRoot(IEnumerable<ArchiveEntry> entries)
    {
        string? root = null;
        foreach (var entry in entries)
        {
            var top = entry.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s != ".");
            if (top is null)
                continue;
            if (root is null)
                root = top;
            else if (!string.Equals(root, top, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return root;
    }
}
