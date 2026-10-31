namespace AfuZip.Core;

/// <summary>
/// Uzun işlemler (çıkarma, test, oluşturma) için kuyruk. Varsayılan eşzamanlılık 1: aynı diske paralel
/// yazmak genelde hızlandırmaz, yavaşlatır. Görevler UI'dan eklenir; çalışma thread pool'da olur.
/// </summary>
public sealed class TaskQueue(int maxConcurrency = 1)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);

    // Kuyruk UI thread'inde oluşturulur; görevler başka bir thread'den eklense bile bildirimler UI'a gider.
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<ArchiveTask> _tasks = [];
    private readonly Lock _gate = new();

    public event EventHandler<ArchiveTask>? TaskAdded;

    public IReadOnlyList<ArchiveTask> Tasks
    {
        get
        {
            lock (_gate)
                return [.. _tasks];
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _tasks.Count(t => t.IsActive);
        }
    }

    /// <param name="work">Görevin kendisi: ilerlemeyi <see cref="ArchiveTask"/>'a (IProgress) bildirir, sonuç mesajı döndürür.</param>
    public ArchiveTask Enqueue(string title, Func<ArchiveTask, CancellationToken, Task<string?>> work)
    {
        var task = new ArchiveTask(title, work, _context);
        lock (_gate)
            _tasks.Add(task);

        if (_context is null || SynchronizationContext.Current == _context)
            TaskAdded?.Invoke(this, task);
        else
            _context.Post(_ => TaskAdded?.Invoke(this, task), null);

        _ = RunWhenSlotFreeAsync(task);
        return task;
    }

    /// <summary>Biten (tamamlanan, başarısız, iptal edilen) görevleri listeden kaldırır.</summary>
    public IReadOnlyList<ArchiveTask> RemoveFinished()
    {
        lock (_gate)
        {
            var finished = _tasks.Where(t => !t.IsActive).ToList();
            _tasks.RemoveAll(t => !t.IsActive);
            return finished;
        }
    }

    public void CancelAll()
    {
        foreach (var task in Tasks)
            task.Cancel();
    }

    private async Task RunWhenSlotFreeAsync(ArchiveTask task)
    {
        try
        {
            await _slots.WaitAsync(task.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            task.MarkCanceled();
            return;
        }

        try
        {
            await Task.Run(task.RunAsync).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }
}
