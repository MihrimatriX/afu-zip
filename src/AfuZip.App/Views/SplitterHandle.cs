using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AfuZip.App.Views;

/// <summary>
/// Paneller arasındaki sürüklenebilir ayraç. WinUI 3'te hazır GridSplitter yok (Community Toolkit'te var);
/// tek kullanım için bağımlılık eklemek yerine işaretçi yakalamayla birkaç satırda yazıldı.
/// <see cref="Vertical"/> false: dikey çizgi, yatay sürüklenir (yan panel); true: yatay çizgi, dikey sürüklenir (alt panel).
/// </summary>
public sealed partial class SplitterHandle : Grid
{
    private readonly Rectangle _line;
    private double? _last;
    private bool _vertical;

    public SplitterHandle()
    {
        Background = new SolidColorBrush(Colors.Transparent); // boş alan da isabet testinden geçsin
        _line = new Rectangle { Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        Children.Add(_line);
        ApplyOrientation();

        PointerEntered += (_, _) => Highlight(true);
        PointerExited += (_, _) => Highlight(_last is not null);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    public bool Vertical
    {
        get => _vertical;
        set
        {
            _vertical = value;
            ApplyOrientation();
        }
    }

    /// <summary>Sürükleme miktarı (DIP); pozitif = sağa veya aşağı.</summary>
    public event Action<double>? Dragged;

    /// <summary>Sürükleme bitti (yeni boyutu kaydetmek için).</summary>
    public event Action? DragCompleted;

    private void ApplyOrientation()
    {
        ProtectedCursor = InputSystemCursor.Create(_vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        if (_vertical)
        {
            Height = 6;
            Width = double.NaN;
            _line.Height = 1;
            _line.Width = double.NaN;
            _line.VerticalAlignment = VerticalAlignment.Center;
            _line.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Width = 6;
            Height = double.NaN;
            _line.Width = 1;
            _line.Height = double.NaN;
            _line.HorizontalAlignment = HorizontalAlignment.Center;
            _line.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private void Highlight(bool on)
    {
        _line.Fill = (Brush)Application.Current.Resources[on ? "AccentFillColorDefaultBrush" : "DividerStrokeColorDefaultBrush"];
        if (_vertical)
            _line.Height = on ? 3 : 1;
        else
            _line.Width = on ? 3 : 1;
    }

    private double Position(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null).Position;
        return _vertical ? point.Y : point.X;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CapturePointer(e.Pointer))
            return;
        _last = Position(e);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_last is not { } last)
            return;
        var current = Position(e);
        _last = current;
        if (current != last)
            Dragged?.Invoke(current - last);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_last is null)
            return;
        _last = null;
        Highlight(false);
        DragCompleted?.Invoke();
    }
}
