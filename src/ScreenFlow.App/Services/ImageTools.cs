using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.App.Services;

/// <summary>Uma imagem pronta para mandar pela sala: JPEG já virado e no tamanho certo, com a prévia para a tela.</summary>
public sealed record PreparedImage(byte[] Data, int Width, int Height, BitmapSource Preview);

/// <summary>
/// Fotos do perfil e imagens do chat. Do lado de quem manda: abre o arquivo escolhido (ou a imagem colada), vira conforme
/// a câmera, diminui e grava em JPEG, o que também tira a localização e os outros dados da foto. Do lado de quem recebe:
/// só abre JPEG e PNG, e confere o tamanho antes de abrir.
/// </summary>
public static class ImageTools
{
    public const int AvatarSide = 192;
    public const int ChatMaxSide = 1920;
    /// <summary>As imagens do chat ficam em torno disto, para passar rápido mesmo quando o Radmin vai por um relay.</summary>
    public const int ChatTargetBytes = 600 * 1024;
    public const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxLocalSide = 20_000;
    /// <summary>
    /// Mais pixels que isto, vindo da rede, nem abre: um PNG de uma cor só cabe em poucos KB e viraria centenas de MB na
    /// memória. O ScreenFlow manda no máximo 1920 de lado; isto deixa folga para imagens de outras versões.
    /// </summary>
    public const long MaxDecodePixels = 24_000_000;

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(PngMagic);

    public static string Extension(ReadOnlySpan<byte> data) => IsPng(data) ? ".png" : ".jpg";

    /// <summary>Extensões que o seletor de arquivos mostra (o Windows abre todas; HEIC e WebP precisam das extensões da Microsoft Store).</summary>
    public const string FileFilter = "Imagens|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.heic;*.heif;*.tif;*.tiff|Todos os arquivos|*.*";

    public static bool LooksLikeImageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".gif" or ".webp" or ".heic" or ".heif" or ".tif" or ".tiff";

    // ───────────────────────── Quem recebe ─────────────────────────

    /// <summary>
    /// Abre uma imagem que veio da rede: só JPEG ou PNG, de até <see cref="RoomBlob.MaxImageSide"/> de lado.
    /// <paramref name="decodeWidth"/> diminui já ao abrir (miniaturas gastam menos memória). Null se não der.
    /// </summary>
    public static BitmapSource? Decode(byte[]? data, int decodeWidth = 0)
    {
        if (data is null || (!IsJpeg(data) && !IsPng(data))) return null;
        try
        {
            var (width, height, _) = Probe(data);
            if (width is <= 0 or > RoomBlob.MaxImageSide || height is <= 0 or > RoomBlob.MaxImageSide || (long)width * height > MaxDecodePixels) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(data, writable: false);
            if (decodeWidth > 0 && width > decodeWidth) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            Log.Debug($"Imagem da sala não abriu: {exception.Message}");
            return null;
        }
    }

    // ───────────────────────── Quem manda ─────────────────────────

    /// <summary>Abre um arquivo de imagem deste PC, já virado conforme a câmera. Erros explicados em português.</summary>
    public static BitmapSource LoadFile(string path, int limit, bool limitShortSide = false)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("O arquivo não existe mais.");
        if (info.Length == 0) throw new InvalidDataException("O arquivo está vazio.");
        if (info.Length > MaxFileBytes) throw new InvalidDataException("A imagem é grande demais (mais de 64 MB).");
        return Load(File.ReadAllBytes(path), limit, limitShortSide);
    }

    /// <summary>
    /// Abre uma imagem deste PC (de um arquivo ou colada), diminuindo já ao abrir para o lado maior caber em
    /// <paramref name="limit"/> (ou o menor, com <paramref name="limitShortSide"/>; 0 = sem limite).
    /// </summary>
    public static BitmapSource Load(byte[] data, int limit, bool limitShortSide = false)
    {
        int width, height;
        ushort orientation;
        try { (width, height, orientation) = Probe(data); }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("O arquivo não é uma imagem que o Windows consegue abrir.", exception);
        }
        if (width <= 0 || height <= 0 || width > MaxLocalSide || height > MaxLocalSide)
            throw new InvalidDataException("A imagem tem um tamanho que o ScreenFlow não abre.");
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(data, writable: false);
            // Diminuir ao abrir é bem mais rápido: fotos de celular têm 12 MP ou mais.
            var side = limitShortSide ? Math.Min(width, height) : Math.Max(width, height);
            if (limit > 0 && side > limit)
            {
                if ((width >= height) != limitShortSide) image.DecodePixelWidth = limit;
                else image.DecodePixelHeight = limit;
            }
            image.EndInit();
            image.Freeze();
            return Orient(image, orientation);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("A imagem está estragada ou num formato que o Windows não abre.", exception);
        }
    }

    /// <summary>A imagem que está na área de transferência (print, imagem copiada do navegador ou um arquivo copiado).</summary>
    public static BitmapSource? FromClipboard()
    {
        try
        {
            // PNG primeiro: o navegador e a Ferramenta de Captura põem PNG, que não tem o problema da transparência.
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream png) return Load(png.ToArray(), ChatMaxSide);
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files
                && files.Cast<string>().FirstOrDefault(LooksLikeImageFile) is { } file)
                return LoadFile(file, ChatMaxSide);
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                // A imagem do Windows (DIB) costuma vir com a transparência zerada: sem o canal alfa as cores ficam certas.
                var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
                opaque.Freeze();
                return opaque;
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) { Log.Debug($"Área de transferência: {exception.Message}"); }
        return null;
    }

    /// <summary>A foto do perfil: o quadrado do meio (um pouco para cima nas fotos em pé), em JPEG de até 96 KB.</summary>
    public static byte[] MakeAvatar(BitmapSource source)
    {
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var x = (source.PixelWidth - side) / 2;
        var y = (source.PixelHeight - side) / 3; // o rosto costuma ficar no terço de cima
        var crop = new CroppedBitmap(source, new Int32Rect(x, y, side, side));
        crop.Freeze();
        var square = Render(crop, AvatarSide, AvatarSide);
        foreach (var quality in new[] { 90, 82, 72, 60 })
        {
            var data = Jpeg(square, quality);
            if (data.Length <= RoomBlob.MaxAvatarBytes) return data;
        }
        return Jpeg(Render(crop, 128, 128), 60);
    }

    /// <summary>
    /// Uma imagem do chat: até <see cref="ChatMaxSide"/> de lado e em torno de <see cref="ChatTargetBytes"/> (diminui a
    /// qualidade e depois o tamanho até caber; nunca passa de <see cref="RoomBlob.MaxImageBytes"/>).
    /// </summary>
    public static PreparedImage MakeChatImage(BitmapSource source)
    {
        var scale = Math.Min(1.0, (double)ChatMaxSide / Math.Max(source.PixelWidth, source.PixelHeight));
        PreparedImage? smallest = null;
        for (var attempt = 0; attempt < 4; attempt++, scale *= 0.75)
        {
            var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var bitmap = Render(source, width, height);
            foreach (var quality in new[] { 88, 80, 70 })
            {
                var data = Jpeg(bitmap, quality);
                var prepared = new PreparedImage(data, width, height, bitmap);
                if (data.Length <= ChatTargetBytes) return prepared;
                if (smallest is null || data.Length < smallest.Data.Length) smallest = prepared;
            }
        }
        if (smallest is { Data.Length: <= RoomBlob.MaxImageBytes }) return smallest;
        throw new InvalidDataException("Não deu para deixar a imagem pequena o bastante para mandar.");
    }

    /// <summary>Grava numa pasta temporária, com a extensão certa, para abrir no visualizador de fotos do Windows.</summary>
    public static string TempFile(string id, byte[] data)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ScreenFlow");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"imagem-{id[..Math.Min(12, id.Length)]}{Extension(data)}");
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        return path;
    }

    // ───────────────────────── Ajudantes ─────────────────────────

    /// <summary>Tamanho e orientação (EXIF) sem abrir os pixels.</summary>
    private static (int Width, int Height, ushort Orientation) Probe(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("O arquivo não tem imagem.");
        var frame = decoder.Frames[0];
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value) orientation = value;
        }
        catch { } // PNG e BMP não têm esse dado
        return (frame.PixelWidth, frame.PixelHeight, orientation);
    }

    /// <summary>Vira a foto como a câmera marcou (celular em pé, por exemplo).</summary>
    private static BitmapSource Orient(BitmapSource image, ushort orientation)
    {
        if (orientation is < 2 or > 8) return image;
        var transform = new TransformGroup();
        if (orientation is 2 or 4 or 5 or 7) transform.Children.Add(new ScaleTransform(-1, 1));
        var angle = orientation switch { 3 or 4 => 180, 6 or 7 => 90, 5 or 8 => 270, _ => 0 };
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        var rotated = new TransformedBitmap(image, transform);
        rotated.Freeze();
        return rotated;
    }

    /// <summary>Desenha no tamanho pedido, com boa qualidade e fundo branco (o JPEG não tem transparência).</summary>
    private static BitmapSource Render(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawImage(source, new Rect(0, 0, width, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static byte[] Jpeg(BitmapSource bitmap, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
