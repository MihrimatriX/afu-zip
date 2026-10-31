namespace AfuZip.Tests;

public class TaskQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tasks_RunOneAtATimeInOrder()
    {
        var queue = new TaskQueue(maxConcurrency: 1);
        var log = new List<string>();
        var running = 0;
        var maxRunning = 0;

        ArchiveTask Add(string name) => queue.Enqueue(name, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (log) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(30, ct);
            lock (log) log.Add(name);
            Interlocked.Decrement(ref running);
            return $"{name} bitti";
        });

        var tasks = new[] { Add("a"), Add("b"), Add("c") };
        await Task.WhenAll(tasks.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(["a", "b", "c"], log);
        Assert.Equal(1, maxRunning);
        Assert.All(tasks, t => Assert.Equal(ArchiveTaskState.Completed, t.State));
        Assert.Equal("b bitti", tasks[1].Message);
    }

    [Fact]
    public async Task Failure_IsCapturedAsMessage()
    {
        var queue = new TaskQueue();
        var task = queue.Enqueue("bozuk", (_, _) => throw new InvalidDataException("CRC hatası"));
        await task.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ArchiveTaskState.Failed, task.State);
        Assert.Equal("CRC hatası", task.Message);
    }

    [Fact]
    public async Task FinishedTask_IgnoresLatePauseResumeAndCancel()
    {
        var queue = new TaskQueue();
        var task = queue.Enqueue("kısa", (_, _) => Task.FromResult<string?>("bitti"));
        await task.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Arayüzdeki düğme, görev bittikten hemen sonra tıklanmış olabilir: sonuç ezilmemeli.
        task.Pause();
        task.Resume();
        task.Cancel();

        Assert.Equal(ArchiveTaskState.Completed, task.State);
        Assert.Equal("bitti", task.Message);
        Assert.False(task.CanPause);
        Assert.False(task.CanResume);
    }

    [Fact]
    public async Task QueuedTask_CanBeCanceledBeforeItStarts()
    {
        var queue = new TaskQueue(maxConcurrency: 1);
        var gate = new TaskCompletionSource();
        var first = queue.Enqueue("uzun", async (_, ct) => { await gate.Task.WaitAsync(ct); return null; });
        var second = queue.Enqueue("sıradaki", (_, _) => Task.FromResult<string?>("çalışmamalıydı"));

        second.Cancel();
        gate.SetResult();
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(ArchiveTaskState.Completed, first.State);
        Assert.Equal(ArchiveTaskState.Canceled, second.State);
    }

    [Fact]
    public async Task Pause_BlocksProgressReportsUntilResume()
    {
        var queue = new TaskQueue();
        var reported = 0;
        var firstReported = new TaskCompletionSource();
        var mayContinue = new TaskCompletionSource();
        var task = queue.Enqueue("çıkarma", (progress, ct) => Task.Run<string?>(async () =>
        {
            progress.Report(new ArchiveProgress(1, 3));
            Interlocked.Increment(ref reported);
            firstReported.SetResult();
            await mayContinue.Task;

            for (var i = 2; i <= 3; i++)
            {
                progress.Report(new ArchiveProgress(i, 3)); // duraklatılmışken burada beklemeli
                Interlocked.Increment(ref reported);
            }
            return "tamam";
        }, ct));

        await firstReported.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        task.Pause();
        mayContinue.SetResult();
        await Task.Delay(200, Ct);

        Assert.Equal(1, Volatile.Read(ref reported));
        Assert.Equal(ArchiveTaskState.Paused, task.State);

        task.Resume();
        await task.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ArchiveTaskState.Completed, task.State);
        Assert.Equal(100, task.Percent);
    }

    [Fact]
    public async Task RunningTask_CanBeCanceled()
    {
        var queue = new TaskQueue();
        var task = queue.Enqueue("sonsuz", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
        await WaitUntil(() => task.State == ArchiveTaskState.Running);

        task.Cancel();
        await task.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ArchiveTaskState.Canceled, task.State);
        Assert.Single(queue.RemoveFinished());
        Assert.Empty(queue.Tasks);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "koşul zaman aşımına uğradı");
    }
}
