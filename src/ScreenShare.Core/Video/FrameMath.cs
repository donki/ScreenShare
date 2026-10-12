namespace SocScreenShare.Video;

/// <summary>Un rectángulo en píxeles.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectI Union(IEnumerable<RectI> rects)
    {
        var list = rects.Where(r => !r.IsEmpty).ToList();
        if (list.Count == 0)
            return default;
        var x = list.Min(r => r.X);
        var y = list.Min(r => r.Y);
        return new RectI(x, y, list.Max(r => r.Right) - x, list.Max(r => r.Bottom) - y);
    }

    /// <summary>La parte de este rectángulo que cae dentro de <paramref name="bounds"/>.</summary>
    public RectI Intersect(RectI bounds)
    {
        var x = Math.Max(X, bounds.X);
        var y = Math.Max(Y, bounds.Y);
        var r = Math.Min(Right, bounds.Right);
        var b = Math.Min(Bottom, bounds.Bottom);
        return r <= x || b <= y ? default : new RectI(x, y, r - x, b - y);
    }
}

/// <summary>Dónde va la imagen dentro del vídeo: escalada sin deformar y centrada, con bandas negras si hace falta.</summary>
public static class Letterbox
{
    public static RectI Fit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new RectI(0, 0, targetWidth, targetHeight);
        var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        var w = Math.Min(targetWidth, Math.Max(2, (int)Math.Round(sourceWidth * scale) & ~1));
        var h = Math.Min(targetHeight, Math.Max(2, (int)Math.Round(sourceHeight * scale) & ~1));
        var x = (targetWidth - w) / 2 & ~1;
        var y = (targetHeight - h) / 2 & ~1;
        return new RectI(x, y, w, h);
    }
}

/// <summary>
/// Conversión por procesador de BGRA (lo que da la captura de Windows) a NV12 (lo que come el
/// codificador H.264), escalando por vecino más próximo y con la matriz BT.709 en rango limitado
/// (16-235), la misma que se declara en el fichero. Es la vía de reserva: lo normal es que lo haga la
/// tarjeta gráfica; esta se usa si el equipo no tiene procesador de vídeo por hardware.
/// </summary>
public static class Nv12Converter
{
    public static int BufferSize(int width, int height) => width * height * 3 / 2;

    public static void Convert(ReadOnlySpan<byte> bgra, int sourceWidth, int sourceHeight, int sourceStride,
        Span<byte> nv12, int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("NV12 necesita ancho y alto pares.");
        if (nv12.Length < BufferSize(width, height))
            throw new ArgumentException("El destino es demasiado pequeño.", nameof(nv12));
        var dest = Letterbox.Fit(sourceWidth, sourceHeight, width, height);

        // Fondo negro: Y = 16, U = V = 128.
        nv12[..(width * height)].Fill(16);
        nv12.Slice(width * height, width * height / 2).Fill(128);

        var uvPlane = nv12[(width * height)..];
        for (var y = 0; y < dest.Height; y += 2)
        {
            for (var x = 0; x < dest.Width; x += 2)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    var sy = (int)((long)(y + dy) * sourceHeight / dest.Height);
                    var row = bgra.Slice(sy * sourceStride);
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sx = (int)((long)(x + dx) * sourceWidth / dest.Width);
                        var b = row[sx * 4];
                        var g = row[sx * 4 + 1];
                        var r = row[sx * 4 + 2];
                        nv12[(dest.Y + y + dy) * width + dest.X + x + dx] = Luma(r, g, b);
                        sumR += r;
                        sumG += g;
                        sumB += b;
                    }
                }
                var (u, v) = Chroma(sumR / 4, sumG / 4, sumB / 4);
                var uv = ((dest.Y + y) / 2) * width + dest.X + x;
                uvPlane[uv] = u;
                uvPlane[uv + 1] = v;
            }
        }
    }

    // BT.709, rango limitado: Y 16-235, Cb/Cr 16-240.
    public static byte Luma(int r, int g, int b) =>
        (byte)Math.Clamp((int)Math.Round(16 + (0.2126 * r + 0.7152 * g + 0.0722 * b) * 219 / 255), 16, 235);

    public static (byte U, byte V) Chroma(int r, int g, int b)
    {
        var u = 128 + (-0.1146 * r - 0.3854 * g + 0.5 * b) * 224 / 255;
        var v = 128 + (0.5 * r - 0.4542 * g - 0.0458 * b) * 224 / 255;
        return ((byte)Math.Clamp((int)Math.Round(u), 16, 240), (byte)Math.Clamp((int)Math.Round(v), 16, 240));
    }
}
