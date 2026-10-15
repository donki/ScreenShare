using SocScreenShare.Settings;
using SocScreenShare.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using Box = Vortice.Mathematics.Box;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace SocScreenShare.Engine;

/// <summary>Lo que se pinta en el lienzo: un elemento de captura y dónde va.</summary>
/// <param name="Window">La ventana que se captura (0 si es una pantalla).</param>
internal sealed record CapturePart(GraphicsCaptureItem Item, int X, int Y, nint Window = 0);

/// <summary>
/// El lienzo de la grabación: una textura BGRA en la tarjeta gráfica donde cada sesión de
/// Windows.Graphics.Capture copia su última imagen en cuanto llega (una pantalla, todas una al lado
/// de otra en su sitio, o una ventana). El codificador lo lee a ritmo fijo, así que una pantalla
/// quieta sigue dando imágenes y el vídeo sale con cadencia constante, que es lo que mejor se
/// comparte.
/// </summary>
internal sealed class ScreenCanvas : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly List<Session> _sessions = [];
    private readonly ID3D11RenderTargetView _rtv;
    private readonly bool _isWindow;
    private readonly bool _cursor;
    private RectI _content;
    private long _framesBefore;   // las imágenes de las sesiones ya cerradas al cambiar de ventana

    public ScreenCanvas(GraphicsDevice gd, IReadOnlyList<CapturePart> parts, int width, int height, bool isWindow, bool cursor)
    {
        _gd = gd;
        _isWindow = isWindow;
        _cursor = cursor;
        Width = width;
        Height = height;
        Texture = gd.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default));
        _rtv = gd.Device.CreateRenderTargetView(Texture);
        lock (gd.ContextLock)
            gd.Context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
        _content = isWindow && parts.Count > 0
            ? new RectI(0, 0, Math.Min(width, parts[0].Item.Size.Width), Math.Min(height, parts[0].Item.Size.Height))
            : new RectI(0, 0, width, height);
        foreach (var p in parts)
            _sessions.Add(new Session(this, p, cursor));
    }

    public ID3D11Texture2D Texture { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>La parte del lienzo con imagen (en una ventana, su tamaño actual, que puede cambiar).</summary>
    public RectI Content { get { lock (_gd.ContextLock) return _content; } }

    public long FramesArrived { get { lock (_sessions) return _framesBefore + _sessions.Sum(s => s.Frames); } }

    /// <summary>Se ha cerrado la ventana (o desconectado la pantalla) que se grababa; con la ventana, o 0.</summary>
    public event Action<nint>? SourceClosed;

    public ManualResetEventSlim FirstFrame { get; } = new(false);

    public void Start()
    {
        foreach (var s in _sessions)
            s.Start();
    }

    /// <summary>
    /// Grabando una ventana, pasar a grabar otra en el mismo lienzo (y el mismo fichero): se cierra la
    /// sesión de la anterior, se borra lo que quedaba de ella y se empieza con la nueva. La anterior
    /// ya no avisa de que se cierra.
    /// </summary>
    public void SwitchTo(CapturePart part)
    {
        if (!_isWindow)
            throw new InvalidOperationException("Solo se cambia de ventana grabando una ventana.");
        var next = new Session(this, part, _cursor);
        Session[] old;
        lock (_sessions)
        {
            old = [.. _sessions];
            _framesBefore += old.Sum(s => s.Frames);
            _sessions.Clear();
            _sessions.Add(next);
        }
        foreach (var s in old)
            s.Dispose();
        lock (_gd.ContextLock)
        {
            _gd.Context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
            _content = new RectI(0, 0, Math.Min(Width, part.Item.Size.Width), Math.Min(Height, part.Item.Size.Height));
        }
        next.Start();
    }

    public void Dispose()
    {
        Session[] all;
        lock (_sessions)
            all = [.. _sessions];
        foreach (var s in all)
            s.Dispose();
        _rtv.Dispose();
        Texture.Dispose();
        FirstFrame.Dispose();
    }

    private void OnFrame(Session s, Direct3D11CaptureFrame frame)
    {
        using var tex = GraphicsDevice.TextureOf(frame.Surface);
        var size = frame.ContentSize;
        lock (_gd.ContextLock)
        {
            // Una imagen rezagada de la ventana de antes de cambiar no pinta encima de la nueva.
            if (s.IsDisposed)
                return;
            if (_isWindow)
            {
                var content = new RectI(0, 0, Math.Min(Width, size.Width), Math.Min(Height, size.Height));
                if (content != _content)
                {
                    // La ventana ha cambiado de tamaño: fuera lo que quedaba del tamaño anterior.
                    _gd.Context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
                    _content = content;
                }
            }
            var w = Math.Min(size.Width, Width - s.Part.X);
            var h = Math.Min(size.Height, Height - s.Part.Y);
            if (w > 0 && h > 0)
                _gd.Context.CopySubresourceRegion(Texture, 0, (uint)s.Part.X, (uint)s.Part.Y, 0, tex, 0, new Box(0, 0, 0, w, h, 1));
        }
        FirstFrame.Set();
    }

    /// <summary>Una sesión de captura de un elemento.</summary>
    private sealed class Session : IDisposable
    {
        private readonly ScreenCanvas _canvas;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly TypedEventHandler<GraphicsCaptureItem, object> _onClosed;
        private SizeInt32 _poolSize;
        private long _frames;
        private volatile bool _disposed;

        public Session(ScreenCanvas canvas, CapturePart part, bool cursor)
        {
            _canvas = canvas;
            Part = part;
            _poolSize = part.Item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(canvas._gd.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(part.Item);
            try { _session.IsCursorCaptureEnabled = cursor; } catch (Exception) { }
            // Sin el marco amarillo de Windows 11 cuando se puede (en Windows 10 no existe la opción).
            try { _session.IsBorderRequired = false; } catch (Exception) { }
            _onClosed = (_, _) =>
            {
                if (!_disposed)
                    canvas.SourceClosed?.Invoke(part.Window);
            };
            part.Item.Closed += _onClosed;
        }

        public CapturePart Part { get; }
        public long Frames => Interlocked.Read(ref _frames);
        public bool IsDisposed => _disposed;

        public void Start() => _session.StartCapture();

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                    return;
                Interlocked.Increment(ref _frames);
                var size = frame.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    _poolSize = size;
                    sender.Recreate(_canvas._gd.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                }
                _canvas.OnFrame(this, frame);
            }
            catch (Exception ex)
            {
                AppLog.Write($"imagen capturada: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            // Bajo el cerrojo del contexto: así ninguna imagen suya está a medio copiar al volver.
            lock (_canvas._gd.ContextLock)
                _disposed = true;
            try { Part.Item.Closed -= _onClosed; } catch (Exception) { }
            _pool.FrameArrived -= OnFrameArrived;
            try { _session.Dispose(); } catch (Exception) { }
            try { _pool.Dispose(); } catch (Exception) { }
        }
    }
}
