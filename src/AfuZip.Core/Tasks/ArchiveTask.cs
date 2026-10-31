using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AfuZip.Core;

public enum ArchiveTaskState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Görev kuyruğundaki tek bir iş (çıkarma, test, oluşturma). Motorun ilerleme bildirimlerini alır;
/// duraklatıldığında motor thread'ini bir sonraki bildirimde bekletir. PropertyChanged, görevi oluşturan
/// thread'in SynchronizationContext'ine (UI) gönderilir ve ilerleme bildirimleri saniyede ~10 ile sınırlanır.
/// </summary>
public sealed class ArchiveTask : INotifyPropertyChanged, IProgress<ArchiveProgress>
{
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<ArchiveTask, CancellationToken, Task<string?>> _work;
    private readonly SynchronizationContext? _context;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _resume = new(initialState: true);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private readonly Lock _gate = new();

    private ArchiveTaskState _state;
    private double? _fraction;
    private string? _currentItem;
    private string? _message;
    private double _bytesPerSecond;
    private TimeSpan? _remaining;
    private long _lastNotify;

    internal ArchiveTask(string title, Func<ArchiveTask, CancellationToken, Task<string?>> work, SynchronizationContext? context)
    {
        Title = title;
        _work = work;
        _context = context;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public ArchiveTaskState State => _state;
    public double? Fraction => _fraction;
    public string? CurrentItem => _currentItem;
    public string? Message => _message;
    public double BytesPerSecond => _bytesPerSecond;
    public TimeSpan? Remaining => _remaining;
    public TimeSpan Elapsed => _clock.Elapsed;

    public bool IsActive => _state is ArchiveTaskState.Queued or ArchiveTaskState.Running or ArchiveTaskState.Paused;
    public bool CanPause => _state == ArchiveTaskState.Running;
    public bool CanResume => _state == ArchiveTaskState.Paused;
    public double Percent => (_fraction ?? 0) * 100;
    public bool IsIndeterminate => _state == ArchiveTaskState.Running && _fraction is null;

    /// <summary>"%42 · 85 MB/sn · 12 sn kaldı" gibi tek satırlık özet.</summary>
    public string StatusText => _state switch
    {
        ArchiveTaskState.Queued => "Sırada",
        ArchiveTaskState.Paused => $"Duraklatıldı · %{Percent:0}",
        ArchiveTaskState.Running when _fraction is null => _currentItem ?? "Çalışıyor",
        ArchiveTaskState.Running => string.Join(" · ", new[]
        {
            $"%{Percent:0}",
            _bytesPerSecond > 0 ? $"{ByteSize.Format((long)_bytesPerSecond)}/sn" : null,
            _remaining is { } r ? $"{FormatDuration(r)} kaldı" : null,
        }.Where(s => s is not null)),
        _ => _message ?? _state.ToString(),
    };

    public Task Completion => _completion.Task;

    internal CancellationToken Token => _cts.Token;

    public void Cancel()
    {
        _cts.Cancel();
        _resume.Set(); // duraklatılmış motor thread'i uyansın ve iptali görsün
        if (TryTransition(ArchiveTaskState.Canceled, from => from == ArchiveTaskState.Queued, "İptal edildi"))
            _completion.TrySetResult();
    }

    public void Pause()
    {
        // Durum geçişi kilit altında ve koşullu: araya Finish girerse "Duraklatıldı" sonucu ezmez.
        if (TryTransition(ArchiveTaskState.Paused, from => from == ArchiveTaskState.Running))
        {
            _resume.Reset();
            _clock.Stop();
        }
    }

    public void Resume()
    {
        if (TryTransition(ArchiveTaskState.Running, from => from == ArchiveTaskState.Paused))
        {
            _clock.Start();
            _resume.Set();
        }
    }

    /// <summary>Motor thread'inden çağrılır. Duraklatılmışsa devam edilene veya iptale kadar bekler.</summary>
    /// <exception cref="OperationCanceledException">Görev iptal edildiyse.</exception>
    public void Report(ArchiveProgress value)
    {
        _resume.Wait(_cts.Token);

        lock (_gate)
        {
            _fraction = value.Fraction;
            if (value.CurrentItem is not null)
                _currentItem = value.CurrentItem;

            var seconds = _clock.Elapsed.TotalSeconds;
            if (seconds > 0.5 && value.CompletedBytes > 0)
            {
                _bytesPerSecond = value.CompletedBytes / seconds;
                _remaining = value.TotalBytes > value.CompletedBytes
                    ? TimeSpan.FromSeconds((value.TotalBytes - value.CompletedBytes) / _bytesPerSecond)
                    : TimeSpan.Zero;
            }

            var now = Environment.TickCount64;
            if (now - _lastNotify < NotifyInterval.TotalMilliseconds)
                return;
            _lastNotify = now;
        }

        NotifyProgress();
    }

    internal async Task RunAsync()
    {
        // Sırada beklerken iptal edildiyse iş hiç başlamaz.
        if (!TryTransition(ArchiveTaskState.Running, from => from == ArchiveTaskState.Queued))
            return;

        _clock.Start();
        try
        {
            var message = await _work(this, _cts.Token).ConfigureAwait(false);
            _fraction = 1;
            Finish(ArchiveTaskState.Completed, message ?? "Tamamlandı");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Finish(ArchiveTaskState.Canceled, "İptal edildi");
        }
        catch (Exception ex)
        {
            Finish(ArchiveTaskState.Failed, ex.Message);
        }
    }

    internal void MarkCanceled() => Finish(ArchiveTaskState.Canceled, "İptal edildi");

    private void Finish(ArchiveTaskState state, string message)
    {
        if (TryTransition(state, static from => from is ArchiveTaskState.Queued or ArchiveTaskState.Running or ArchiveTaskState.Paused, message))
        {
            _clock.Stop();
            _resume.Set();
            _completion.TrySetResult();
        }
    }

    /// <summary>Durumu yalnızca mevcut durum <paramref name="allowed"/> ise değiştirir; bildirimler kilit dışında gönderilir.</summary>
    private bool TryTransition(ArchiveTaskState to, Func<ArchiveTaskState, bool> allowed, string? message = null)
    {
        lock (_gate)
        {
            if (!allowed(_state))
                return false;
            _state = to;
            if (message is not null)
            {
                _message = message;
                _remaining = null;
            }
        }

        Notify(nameof(State), nameof(IsActive), nameof(CanPause), nameof(CanResume), nameof(Message),
            nameof(StatusText), nameof(IsIndeterminate), nameof(Percent), nameof(Fraction));
        return true;
    }

    private void NotifyProgress() =>
        Notify(nameof(Fraction), nameof(Percent), nameof(CurrentItem), nameof(BytesPerSecond), nameof(Remaining),
            nameof(StatusText), nameof(IsIndeterminate));

    private void Notify(params string[] names)
    {
        void Raise()
        {
            foreach (var name in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        if (_context is null || SynchronizationContext.Current == _context)
            Raise();
        else
            _context.Post(_ => Raise(), null);
    }

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} sa {t.Minutes} dk"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} dk {t.Seconds} sn" : $"{Math.Max(1, (int)t.TotalSeconds)} sn";
}
