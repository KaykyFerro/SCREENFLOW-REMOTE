using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Controls;

/// <summary>A bolinha de alguém: a foto do perfil, ou a inicial do nome sobre a cor dessa pessoa. O tamanho da letra vem de FontSize.</summary>
public sealed class AvatarView : Grid
{
    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(
        nameof(Colors), typeof(AvatarColors), typeof(AvatarView), new PropertyMetadata(null, Changed));

    public static readonly DependencyProperty InitialProperty = DependencyProperty.Register(
        nameof(Initial), typeof(string), typeof(AvatarView), new PropertyMetadata(string.Empty, Changed));

    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(ImageSource), typeof(AvatarView), new PropertyMetadata(null, Changed));

    private readonly Ellipse _circle = new();
    private readonly TextBlock _initial = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.Bold,
    };
    private readonly Ellipse _photo = new() { Visibility = Visibility.Collapsed };
    private readonly ImageBrush _brush = new() { Stretch = Stretch.UniformToFill };

    public AvatarView()
    {
        _photo.Fill = _brush;
        RenderOptions.SetBitmapScalingMode(_photo, BitmapScalingMode.HighQuality);
        Children.Add(_circle);
        Children.Add(_initial);
        Children.Add(_photo);
    }

    public AvatarColors? Colors
    {
        get => (AvatarColors?)GetValue(ColorsProperty);
        set => SetValue(ColorsProperty, value);
    }

    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    public ImageSource? Photo
    {
        get => (ImageSource?)GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AvatarView)d).Update();

    private void Update()
    {
        _circle.Fill = Colors?.Background;
        _initial.Foreground = Colors?.Foreground;
        _initial.Text = Initial;
        var photo = Photo;
        _brush.ImageSource = photo;
        _photo.Visibility = photo is null ? Visibility.Collapsed : Visibility.Visible;
        _initial.Visibility = photo is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
