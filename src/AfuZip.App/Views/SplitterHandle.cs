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
/// </summary>
public sealed partial class SplitterHandle : Grid
{
    private readonly Rectangle _grip;
    private double? _lastX;

    public SplitterHandle()
    {
        Width = 12;
        Background = new SolidColorBrush(Colors.Transparent); // boş alan da isabet testinden geçsin
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        _grip = new Rectangle
        {
            Width = 3,
            Height = 32,
            RadiusX = 1.5,
            RadiusY = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        _grip.Fill = (Brush)Application.Current.Resources["ControlStrongFillColorDefaultBrush"];
        Children.Add(_grip);

        PointerEntered += (_, _) => _grip.Opacity = 1;
        PointerExited += (_, _) => _grip.Opacity = _lastX is null ? 0 : 1;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
    }

    /// <summary>Sürükleme miktarı (DIP); pozitif = sağa.</summary>
    public event Action<double>? Dragged;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CapturePointer(e.Pointer))
            return;
        _lastX = e.GetCurrentPoint(null).Position.X;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_lastX is not { } last)
            return;
        var x = e.GetCurrentPoint(null).Position.X;
        _lastX = x;
        if (x != last)
            Dragged?.Invoke(x - last);
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
        _lastX = null;
        _grip.Opacity = 0;
    }
}
