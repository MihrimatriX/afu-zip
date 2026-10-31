using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AfuZip.Core;

namespace AfuZip.App.Services;

/// <summary>
/// Görev çubuğu düğmesinde ilerleme: etkin görevlerin ortalaması; duraklatılınca sarı, hata olunca kırmızı.
/// Tüm açık pencerelerin (ana pencere, ilerleme penceresi) düğmesine uygulanır.
/// </summary>
internal sealed partial class TaskbarProgress
{
    private const int NoProgress = 0;
    private const int Indeterminate = 0x1;
    private const int Normal = 0x2;
    private const int Error = 0x4;
    private const int Paused = 0x8;

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly ITaskbarList3? _taskbar;
    private readonly Func<IEnumerable<nint>> _windows;
    private readonly List<ArchiveTask> _active = [];
    private bool _lastFailed;

    public TaskbarProgress(TaskQueue queue, Func<IEnumerable<nint>> windows)
    {
        _windows = windows;
        _taskbar = TryCreate();
        queue.TaskAdded += (_, task) =>
        {
            _active.Add(task);
            _lastFailed = false;
            task.PropertyChanged += (_, _) => Update();
            Update();
        };
    }

    public void Update()
    {
        if (_taskbar is null)
            return;

        foreach (var finished in _active.Where(t => !t.IsActive).ToList())
        {
            _lastFailed |= finished.State == ArchiveTaskState.Failed;
            _active.Remove(finished);
        }

        int state;
        ulong value = 0;
        if (_active.Count == 0)
        {
            // Hata kırmızı kalır, kullanıcı pencereye dönünce (yeni görev başlayınca) temizlenir.
            state = _lastFailed ? Error : NoProgress;
            value = _lastFailed ? 1000UL : 0;
        }
        else
        {
            value = (ulong)(_active.Average(t => t.Fraction ?? 0) * 1000);
            state = _active.Any(t => t.State == ArchiveTaskState.Paused) ? Paused
                : _active.All(t => t.Fraction is null) ? Indeterminate
                : Normal;
        }

        foreach (var hwnd in _windows())
        {
            try
            {
                _taskbar.SetProgressState(hwnd, state);
                if (state is Normal or Paused or Error)
                    _taskbar.SetProgressValue(hwnd, value, 1000);
            }
            catch (COMException)
            {
                // Pencere kapanmak üzere: sonraki güncellemede listede olmayacak.
            }
        }
    }

    private static ITaskbarList3? TryCreate()
    {
        try
        {
            var iid = typeof(ITaskbarList3).GUID;
            if (CoCreateInstance(ClsidTaskbarList, 0, 1 /* CLSCTX_INPROC_SERVER */, iid, out var pointer) < 0)
                return null;
            try
            {
                var taskbar = (ITaskbarList3)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
                taskbar.HrInit();
                return taskbar;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}

/// <summary>ITaskbarList → ITaskbarList2 → ITaskbarList3 vtable sırası; yalnızca ilerleme yöntemlerine kadar.</summary>
[GeneratedComInterface]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
internal partial interface ITaskbarList3
{
    void HrInit();
    void AddTab(nint hwnd);
    void DeleteTab(nint hwnd);
    void ActivateTab(nint hwnd);
    void SetActiveAlt(nint hwnd);
    void MarkFullscreenWindow(nint hwnd, int fullscreen);
    void SetProgressValue(nint hwnd, ulong completed, ulong total);
    void SetProgressState(nint hwnd, int flags);
}
