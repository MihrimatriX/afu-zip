namespace AfuZip.Core;

/// <summary>
/// 7-Zip ilerlemeyi saniyede yüzlerce kez bildirir; her biri UI thread'ine bir gönderi demektir. Bu sarmalayıcı
/// en fazla <paramref name="interval"/>'da bir iletir (son değer kaybolabilir; ilerleme çubuğu için önemsiz).
/// </summary>
public sealed class ThrottledProgress<T>(IProgress<T> inner, TimeSpan interval) : IProgress<T>
{
    private readonly long _intervalMs = (long)interval.TotalMilliseconds;
    private long _last = long.MinValue;

    public void Report(T value)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _last);
        if (now - last < _intervalMs || Interlocked.CompareExchange(ref _last, now, last) != last)
            return;
        inner.Report(value);
    }
}
