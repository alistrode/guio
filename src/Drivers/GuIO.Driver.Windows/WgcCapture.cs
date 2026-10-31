using System.Runtime.InteropServices;
using GuIO.Contracts;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GuIO.Driver.Windows;

/// <summary>
/// 基于 Windows.Graphics.Capture 的截屏，可截取 DirectX/Vulkan 游戏画面，
/// 克服 PrintWindow 对游戏窗口的黑屏问题。任何环节失败都返回 null，由上层降级。
/// </summary>
internal static class WgcCapture
{
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, int len, out IntPtr hstr);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstr);

    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly object Gate = new();
    private static ID3D11Device? _device;

    private static ID3D11Device? GetDevice()
    {
        lock (Gate)
        {
            if (_device is not null) return _device;
            try
            {
                _device = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 });
                return _device;
            }
            catch { return null; }
        }
    }

    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    public static Frame? CaptureWindow(IntPtr hwnd, PixelRect rect)
    {
        try
        {
            var item = CreateItemForWindow(hwnd);
            if (item is null) return null;
            return CaptureItem(item);
        }
        catch { return null; }
    }

    public static Frame? CaptureRegion(PixelRect rect)
    {
        try
        {
            var item = CreateItemForMonitor(MonitorFromRect(rect));
            if (item is null) return null;
            return CaptureItem(item);
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static IntPtr MonitorFromRect(PixelRect r)
    {
        var rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        return MonitorFromRect(ref rect, 2 /*NEAREST*/);
    }

    private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GetItemInterop();
        if (interop is null) return null;
        var iid = IID_GraphicsCaptureItem;
        IntPtr ptr = interop.CreateForWindow(hwnd, ref iid);
        if (ptr == IntPtr.Zero) return null;
        try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static GraphicsCaptureItem? CreateItemForMonitor(IntPtr monitor)
    {
        var interop = GetItemInterop();
        if (interop is null) return null;
        var iid = IID_GraphicsCaptureItem;
        IntPtr ptr = interop.CreateForMonitor(monitor, ref iid);
        if (ptr == IntPtr.Zero) return null;
        try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static IGraphicsCaptureItemInterop? GetItemInterop()
    {
        int hr = WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", 44, out IntPtr hstr);
        if (hr != 0) return null;
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hstr, ref iid, out IntPtr factory);
            if (hr != 0 || factory == IntPtr.Zero) return null;
            try { return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory); }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstr); }
    }

    private static Frame? CaptureItem(GraphicsCaptureItem item)
    {
        var device = GetDevice();
        if (device is null) return null;

        IDirect3DDevice? winrtDevice = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
            if (hr != 0 || inspectable == IntPtr.Zero) return null;
            winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);

            var size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            TrySetCursorAndBorder(session);
            session.StartCapture();

            // 等待首帧（池有帧间隔，给足 1.5s）
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (frame is null && DateTime.UtcNow < deadline)
            {
                frame = pool.TryGetNextFrame();
                if (frame is null) Thread.Sleep(30);
            }
            if (frame is null) return null;

            var surface = frame.Surface;
            int w = frame.ContentSize.Width, h = frame.ContentSize.Height;
            if (w <= 0 || h <= 0) return null;
            return CopySurfaceToFrame(surface, device, w, h, item.Size.Width, item.Size.Height);
        }
        catch { return null; }
        finally
        {
            frame?.Dispose();
            session?.Dispose();
            pool?.Dispose();
            winrtDevice?.Dispose();
        }
    }

    private static void TrySetCursorAndBorder(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = false; } catch { }
    }

    private static Frame? CopySurfaceToFrame(object surface, ID3D11Device device, int w, int h, int fullW, int fullH)
    {
        // surface(IDirect3DSurface) -> ID3D11Texture2D
        IntPtr surfaceAbi = WinRT.MarshalInterface<global::Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>.FromManaged(
            (global::Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface)surface);
        IntPtr accessPtr = IntPtr.Zero;
        IntPtr texPtr = IntPtr.Zero;
        try
        {
            var iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            Marshal.QueryInterface(surfaceAbi, ref iidAccess, out accessPtr);
            if (accessPtr == IntPtr.Zero) return null;
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);

            var iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
            texPtr = access.GetInterface(ref iidTex);
            if (texPtr == IntPtr.Zero) return null;
        }
        finally
        {
            Marshal.Release(surfaceAbi);
            if (accessPtr != IntPtr.Zero) Marshal.Release(accessPtr);
        }

        using var srcTex = new ID3D11Texture2D(texPtr);
        using var context = device.ImmediateContext;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, srcTex);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = w * 4;
            var buf = new byte[stride * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, buf, y * stride, stride);
            return new Frame
            {
                Width = w, Height = h, Stride = stride, Pixels = buf,
                SourceRect = new PixelRect(0, 0, fullW, fullH),
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
        finally { context.Unmap(staging, 0); }
    }
}
