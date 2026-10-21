using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CreateLauncher;

public sealed class SkinHead : Control
{
    Bitmap? skin;
    public SkinHead() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    public void Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        if (bitmap.PixelSize.Width < 64 || bitmap.PixelSize.Width > 1024 || bitmap.PixelSize.Width % 64 != 0 || bitmap.PixelSize.Height < bitmap.PixelSize.Width / 2 || bitmap.PixelSize.Height > 1024) { bitmap.Dispose(); throw new InvalidDataException("Некорректный размер скина"); }
        var previous = skin; skin = bitmap; previous?.Dispose(); InvalidateVisual();
    }
    public void Reset() { skin?.Dispose(); skin = null; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        var dest = new Rect(Bounds.Size);
        if (skin == null)
        {
            context.DrawRectangle(SolidColorBrush.Parse("#87A77A"), null, dest);
            double unit = Bounds.Width / 8;
            context.DrawRectangle(SolidColorBrush.Parse("#334D39"), null, new Rect(unit, unit * 2, unit * 6, unit));
            context.DrawRectangle(SolidColorBrush.Parse("#E3E9CE"), null, new Rect(unit, unit * 3, unit * 2, unit));
            context.DrawRectangle(SolidColorBrush.Parse("#E3E9CE"), null, new Rect(unit * 5, unit * 3, unit * 2, unit));
            context.DrawRectangle(SolidColorBrush.Parse("#334D39"), null, new Rect(unit * 2, unit * 6, unit * 4, unit));
            return;
        }
        double s = skin.Size.Width / 64;
        context.DrawImage(skin, new Rect(8 * s, 8 * s, 8 * s, 8 * s), dest);
        context.DrawImage(skin, new Rect(40 * s, 8 * s, 8 * s, 8 * s), dest);
    }
}
