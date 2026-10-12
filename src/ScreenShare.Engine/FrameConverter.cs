using System.Runtime.InteropServices;
using SocScreenShare.Settings;
using SocScreenShare.Video;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SocScreenShare.Engine;

/// <summary>
/// Del lienzo BGRA a una imagen NV12 del tamaño del vídeo, escalada sin deformar y centrada. Lo
/// hace el procesador de vídeo de la tarjeta gráfica (escala y convierte de color en un paso); si el
/// equipo no lo tiene, se hace por procesador con <see cref="Nv12Converter"/>.
/// </summary>
internal sealed class FrameConverter : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly ScreenCanvas _canvas;
    private readonly int _width, _height;
    private readonly ID3D11VideoDevice? _videoDevice;
    private readonly ID3D11VideoContext? _videoContext;
    private readonly ID3D11VideoProcessorEnumerator? _enumerator;
    private readonly ID3D11VideoProcessor? _processor;
    private readonly ID3D11VideoProcessorInputView? _inputView;
    private readonly ID3D11VideoProcessorOutputView? _outputView;
    private readonly ID3D11Texture2D? _nv12;
    private readonly ID3D11Texture2D _staging;
    private RectI _lastContent;

    public FrameConverter(GraphicsDevice gd, ScreenCanvas canvas, int width, int height, int fps)
    {
        _gd = gd;
        _canvas = canvas;
        _width = width;
        _height = height;
        if (gd.HasVideoSupport)
        {
            try
            {
                _videoDevice = gd.Device.QueryInterface<ID3D11VideoDevice>();
                _videoContext = gd.Context.QueryInterface<ID3D11VideoContext>();
                var desc = new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputWidth = (uint)canvas.Width,
                    InputHeight = (uint)canvas.Height,
                    OutputWidth = (uint)width,
                    OutputHeight = (uint)height,
                    InputFrameRate = new Rational((uint)fps, 1),
                    OutputFrameRate = new Rational((uint)fps, 1),
                    Usage = VideoUsage.OptimalQuality,
                };
                _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
                _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
                _inputView = _videoDevice.CreateVideoProcessorInputView(canvas.Texture, _enumerator, new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });
                _nv12 = gd.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default));
                _outputView = _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });
                _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
                _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
                // Entrada RGB de 0 a 255; salida YCbCr BT.709 de 16 a 235 (lo que se declara en el MP4).
                _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0 });
                _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { Usage = 0, YCbCr_Matrix = 1, Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235 });
                _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
                _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, width, height));
                _staging = gd.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                UsesGpu = true;
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write($"sin procesador de vídeo por hardware, se convierte por procesador: {ex.Message}");
                DisposeGpu();
            }
        }
        _staging = gd.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)canvas.Width, (uint)canvas.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    public bool UsesGpu { get; }

    public int BufferSize => Nv12Converter.BufferSize(_width, _height);

    /// <summary>Convierte el lienzo tal como está ahora y deja la imagen NV12 en <paramref name="nv12"/>.</summary>
    public void Convert(Span<byte> nv12)
    {
        lock (_gd.ContextLock)
        {
            var content = _canvas.Content;
            if (UsesGpu)
            {
                if (content != _lastContent)
                {
                    var dest = Letterbox.Fit(content.Width, content.Height, _width, _height);
                    _videoContext!.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(content.X, content.Y, content.Right, content.Bottom));
                    _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RawRect(dest.X, dest.Y, dest.Right, dest.Bottom));
                    _lastContent = content;
                }
                var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
                _videoContext!.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [stream]).CheckError();
                _gd.Context.CopyResource(_staging, _nv12!);
                var map = _gd.Context.Map(_staging, 0, MapMode.Read);
                try
                {
                    CopyPlane(map.DataPointer, (int)map.RowPitch, nv12, _width, _height);
                    CopyPlane(map.DataPointer + (nint)(map.RowPitch * _height), (int)map.RowPitch, nv12[(_width * _height)..], _width, _height / 2);
                }
                finally
                {
                    _gd.Context.Unmap(_staging, 0);
                }
            }
            else
            {
                _gd.Context.CopyResource(_staging, _canvas.Texture);
                var map = _gd.Context.Map(_staging, 0, MapMode.Read);
                try
                {
                    unsafe
                    {
                        var pitch = (int)map.RowPitch;
                        var src = new ReadOnlySpan<byte>((void*)map.DataPointer, pitch * _canvas.Height);
                        Nv12Converter.Convert(src, content.Width, content.Height, pitch, nv12, _width, _height);
                    }
                }
                finally
                {
                    _gd.Context.Unmap(_staging, 0);
                }
            }
        }
    }

    private static unsafe void CopyPlane(nint source, int pitch, Span<byte> dest, int width, int rows)
    {
        for (var y = 0; y < rows; y++)
            new ReadOnlySpan<byte>((void*)(source + (nint)y * pitch), width).CopyTo(dest.Slice(y * width, width));
    }

    private void DisposeGpu()
    {
        _outputView?.Dispose();
        _inputView?.Dispose();
        _nv12?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
    }

    public void Dispose()
    {
        DisposeGpu();
        _staging.Dispose();
    }
}
