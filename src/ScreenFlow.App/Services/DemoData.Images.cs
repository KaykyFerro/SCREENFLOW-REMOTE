using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenFlow.App.Services;

public static partial class DemoData
{
    private static BitmapSource? _aurora;

    /// <summary>O fundo do app, usado como "foto" e "imagem" nas capturas (o runner do CI não tem fotos de ninguém).</summary>
    private static BitmapSource Aurora()
    {
        if (_aurora is not null) return _aurora;
        var image = new BitmapImage(new Uri("pack://application:,,,/Assets/aurora.jpg"));
        image.Freeze();
        return _aurora = image;
    }

    /// <summary>Uma foto de perfil de exemplo: um recorte quadrado do fundo, já no formato que a sala usa.</summary>
    public static byte[] ProfilePhoto(int x, int y, int side)
    {
        var crop = new CroppedBitmap(Aurora(), new Int32Rect(x, y, side, side));
        crop.Freeze();
        return ImageTools.MakeAvatar(crop);
    }

    /// <summary>Uma imagem de chat de exemplo.</summary>
    public static PreparedImage ChatPicture() => ImageTools.MakeChatImage(Aurora());
}
