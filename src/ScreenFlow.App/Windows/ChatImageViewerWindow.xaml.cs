using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Windows;

/// <summary>Visualizador integrado do chat: zoom com a rodinha, arraste para mover e setas para navegar pelo canal.</summary>
internal partial class ChatImageViewerWindow : Window
{
    private const double MinimumZoom = 1.0;
    private const double MaximumZoom = 8.0;
    private const double ZoomStep = 1.18;

    private readonly IReadOnlyList<ChatRow> _images;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly MatrixTransform _imageTransform = new(Matrix.Identity);
    private int _index;
    private byte[]? _data;
    private BitmapSource? _bitmap;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private bool _dragging;
    private Point _dragOrigin;
    private Point _panOrigin;

    public ChatImageViewerWindow(IReadOnlyList<ChatRow> images, int selectedIndex)
    {
        if (images.Count == 0) throw new ArgumentException("O visualizador precisa de pelo menos uma imagem.", nameof(images));
        if (selectedIndex < 0 || selectedIndex >= images.Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));

        InitializeComponent();
        _images = images.ToArray();
        _index = selectedIndex;
        ImagePreview.RenderTransform = _imageTransform;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ActionStatusText.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) => _statusTimer.Stop();
        SourceInitialized += (_, _) => ThemeManager.ApplyFrame(this);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadCurrentImage();
        Focus();
    }

    private void LoadCurrentImage()
    {
        var row = _images[_index];
        _data = row.Image is { } image ? AppServices.Blobs.Get(image.Id) : null;
        _bitmap = ImageTools.Decode(_data);
        ImagePreview.Source = _bitmap;
        UnavailablePanel.Visibility = _bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        AuthorText.Text = $"{row.AuthorText} · {row.Time.ToLocalTime():dd/MM/yyyy HH:mm}";
        CaptionText.Text = row.Text;
        CaptionText.Visibility = row.HasText ? Visibility.Visible : Visibility.Collapsed;
        PositionText.Text = _images.Count > 1 ? $"{_index + 1} de {_images.Count} imagens deste canal" : "Imagem deste canal";
        PreviousButton.Visibility = _images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _images.Count - 1;
        ActionStatusText.Visibility = Visibility.Collapsed;
        ResetZoom();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Navigate(int direction)
    {
        var next = _index + direction;
        if (next < 0 || next >= _images.Count) return;
        _index = next;
        LoadCurrentImage();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep);

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void ResetZoom()
    {
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        ApplyTransform();
    }

    private void SetZoom(double value, Point? anchor = null)
    {
        var next = Math.Clamp(value, MinimumZoom, MaximumZoom);
        if (Math.Abs(next - _zoom) < 0.001) return;

        var point = anchor ?? new Point(ImageViewport.ActualWidth / 2, ImageViewport.ActualHeight / 2);
        var imageX = (point.X - _panX) / _zoom;
        var imageY = (point.Y - _panY) / _zoom;
        _panX = point.X - imageX * next;
        _panY = point.Y - imageY * next;
        _zoom = next;
        ClampPanToImage();
        ApplyTransform();
    }

    private void ClampPanToImage()
    {
        if (_bitmap is null || ImageViewport.ActualWidth <= 0 || ImageViewport.ActualHeight <= 0) return;
        var viewportWidth = ImageViewport.ActualWidth;
        var viewportHeight = ImageViewport.ActualHeight;
        var aspect = _bitmap.PixelWidth / (double)_bitmap.PixelHeight;
        var contentWidth = Math.Min(viewportWidth, viewportHeight * aspect);
        var contentHeight = contentWidth / aspect;
        if (contentHeight > viewportHeight)
        {
            contentHeight = viewportHeight;
            contentWidth = contentHeight * aspect;
        }

        var left = (viewportWidth - contentWidth) / 2 * _zoom;
        var top = (viewportHeight - contentHeight) / 2 * _zoom;
        var scaledWidth = contentWidth * _zoom;
        var scaledHeight = contentHeight * _zoom;
        _panX = scaledWidth <= viewportWidth ? 0 : Math.Clamp(_panX, viewportWidth - left - scaledWidth, -left);
        _panY = scaledHeight <= viewportHeight ? 0 : Math.Clamp(_panY, viewportHeight - top - scaledHeight, -top);
    }

    private void ApplyTransform()
    {
        _imageTransform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _panX, _panY);
        ZoomText.Text = $"{_zoom:P0}";
        ImageViewport.Cursor = _zoom > 1.01 ? Cursors.Hand : Cursors.Arrow;
        ImagePreview.Cursor = _zoom > 1.01 ? Cursors.Hand : Cursors.Arrow;
    }

    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bitmap is null) return;
        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        SetZoom(_zoom * factor, e.GetPosition(ImageViewport));
        e.Handled = true;
    }

    private void Image_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap is null) return;
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }
        if (_zoom <= 1.01) return;
        _dragging = true;
        _dragOrigin = e.GetPosition(ImageViewport);
        _panOrigin = new Point(_panX, _panY);
        ImageViewport.Cursor = Cursors.SizeAll;
        ImagePreview.Cursor = Cursors.SizeAll;
        ImageViewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(ImageViewport);
        _panX = _panOrigin.X + point.X - _dragOrigin.X;
        _panY = _panOrigin.Y + point.Y - _dragOrigin.Y;
        ClampPanToImage();
        ApplyTransform();
        ImageViewport.Cursor = Cursors.SizeAll;
    }

    private void Viewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ImageViewport.ReleaseMouseCapture();
        ApplyTransform();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrentImage();

    private void CopyCurrentImage()
    {
        if (_bitmap is null) return;
        try
        {
            Clipboard.SetImage(_bitmap);
            ShowStatus("Imagem copiada.", danger: false);
        }
        catch (Exception exception)
        {
            ShowStatus($"Não deu para copiar: {exception.Message}", danger: true);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrentImage();

    private void SaveCurrentImage()
    {
        if (_data is null) return;
        var extension = ImageTools.Extension(_data);
        var dialog = new SaveFileDialog
        {
            Title = "Salvar a imagem",
            FileName = $"ScreenFlow {DateTime.Now:yyyy-MM-dd HH-mm-ss}{extension}",
            Filter = extension == ".png" ? "Imagem PNG|*.png" : "Imagem JPEG|*.jpg",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _data);
            ShowStatus("Imagem salva.", danger: false);
        }
        catch (Exception exception)
        {
            ShowStatus($"Não deu para salvar: {exception.Message}", danger: true);
        }
    }

    private void ShowStatus(string message, bool danger)
    {
        ActionStatusText.Text = message;
        ActionStatusText.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "AccentText");
        ActionStatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.C) { CopyCurrentImage(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveCurrentImage(); e.Handled = true; }
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                SetZoom(_zoom * ZoomStep);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetZoom(_zoom / ZoomStep);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Ajuste usado só nas capturas do CI para mostrar o zoom e a navegação entre imagens.</summary>
    internal void SetDemoView(double zoom)
    {
        if (!IsLoaded) return;
        SetZoom(zoom);
    }
}
