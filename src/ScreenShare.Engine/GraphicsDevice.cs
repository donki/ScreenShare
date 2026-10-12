using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace SocScreenShare.Engine;

/// <summary>
/// El dispositivo Direct3D 11 de la grabación y el puente con Windows.Graphics.Capture (que habla
/// WinRT): crear el elemento de captura de una pantalla o una ventana, envolver el dispositivo y
/// sacar la textura de cada imagen capturada. Todo con las interfaces de interop documentadas de
/// Windows, sin componentes de terceros.
/// </summary>
internal sealed unsafe class GraphicsDevice : IDisposable
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public bool HasVideoSupport { get; }

    /// <summary>Todo uso del contexto inmediato pasa por aquí: no es seguro entre hilos.</summary>
    public object ContextLock { get; } = new();

    public GraphicsDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        ID3D11Device? device;
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out device);
        HasVideoSupport = hr.Success;
        if (hr.Failure)
        {
            flags = DeviceCreationFlags.BgraSupport;
            hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out device);
            if (hr.Failure)
                hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, flags, levels, out device);
            hr.CheckError();
        }
        Device = device!;
        Context = Device.ImmediateContext;
        using (var mt = Device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);

        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try
        {
            WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static GraphicsCaptureItem ItemForMonitor(nint monitor) => CreateItem(monitor, forWindow: false);

    public static GraphicsCaptureItem ItemForWindow(nint hwnd) => CreateItem(hwnd, forWindow: true);

    private static GraphicsCaptureItem CreateItem(nint handle, bool forWindow)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        nint factory = 0;
        try
        {
            var iid = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out factory));
            // IGraphicsCaptureItemInterop: 3 = CreateForWindow, 4 = CreateForMonitor.
            var vtbl = *(nint**)factory;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[forWindow ? 3 : 4];
            var itemIid = IidGraphicsCaptureItem;
            nint item;
            Marshal.ThrowExceptionForHR(create(factory, handle, &itemIid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            if (factory != 0)
                Marshal.Release(factory);
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>La textura de Direct3D que hay detrás de una imagen capturada (hay que liberarla).</summary>
    public static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iid = IidDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in iid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess: 3 = GetInterface.
                var vtbl = *(nint**)access;
                var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[3];
                var texIid = IidTexture2D;
                nint texture;
                Marshal.ThrowExceptionForHR(get(access, &texIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    public void Dispose()
    {
        WinRtDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);
}
