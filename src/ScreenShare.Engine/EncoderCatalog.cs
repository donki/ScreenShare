using SocScreenShare.Settings;
using Vortice.MediaFoundation;

namespace SocScreenShare.Engine;

/// <summary>
/// Qué codificadores de vídeo tiene este equipo (MFTEnumEx de Media Foundation): H.264 viene con
/// Windows; HEVC depende de la tarjeta gráfica o de las extensiones de Windows. Se mira una vez.
/// </summary>
public static class EncoderCatalog
{
    private const uint SyncMft = 0x1, AsyncMft = 0x2, Hardware = 0x4, SortAndFilter = 0x40;

    private static readonly Lazy<(int Hardware, int Software)> Hevc = new(() => Count(VideoFormatGuids.Hevc));
    private static readonly Lazy<(int Hardware, int Software)> H264 = new(() => Count(VideoFormatGuids.H264));

    public static bool HevcAvailable => Hevc.Value.Hardware + Hevc.Value.Software > 0;
    public static bool HevcHardware => Hevc.Value.Hardware > 0;
    public static bool H264Hardware => H264.Value.Hardware > 0;

    private static (int, int) Count(Guid subtype)
    {
        try
        {
            MediaFactory.MFStartup(false).CheckError();
            return (Enumerate(subtype, Hardware | SortAndFilter), Enumerate(subtype, SyncMft | AsyncMft | SortAndFilter));
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudieron listar los codificadores: {ex.Message}");
            return (0, 0);
        }
    }

    private static int Enumerate(Guid subtype, uint flags)
    {
        using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = subtype });
        var n = 0;
        foreach (var activate in list)
        {
            n++;
            activate.Dispose();
        }
        return n;
    }
}
