using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GoPing.Services;

internal sealed class WgcScreenCapturer : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private bool _disposed;

    private WgcScreenCapturer(GraphicsCaptureItem item, IDirect3DDevice device)
    {
        _device = device;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _session = _pool.CreateCaptureSession(item);
        TrySetBorder(_session, false);
        TrySetCursor(_session, true);
        _session.StartCapture();
    }

    public static WgcScreenCapturer? TryCreate(ScreenShareSource src)
    {
        try
        {
            var item = CreateItem(src);
            if (item is null) { CallLogger.Error($"WGC CreateItem returned null for {src.Kind} handle={src.Handle}"); return null; }
            var device = CreateDirect3DDevice();
            if (device is null) { CallLogger.Error("WGC CreateDirect3DDevice returned null"); return null; }
            return new WgcScreenCapturer(item, device);
        }
        catch (Exception ex)
        {
            CallLogger.Error("WGC create failed", ex);
            return null;
        }
    }

    public string? TryCaptureJpegBase64(int maxW = 960, long quality = 45L)
    {
        try
        {
            Direct3D11CaptureFrame? last = null;
            while (true)
            {
                var frame = _pool.TryGetNextFrame();
                if (frame is null) break;
                last?.Dispose();
                last = frame;
            }
            if (last is null) return null;
            using (last)
            {
                int w = Math.Max(1, last.ContentSize.Width);
                int h = Math.Max(1, last.ContentSize.Height);
                using var sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(last.Surface).GetAwaiter().GetResult();
                using var converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                using var bmp = SoftwareBitmapToBitmap(converted, w, h);
                if (bmp is null) return null;
                return EncodeBitmap(bmp, maxW, quality);
            }
        }
        catch (Exception ex)
        {
            CallLogger.Error("WGC capture failed", ex);
            return null;
        }
    }

    private static Bitmap? SoftwareBitmapToBitmap(SoftwareBitmap sb, int width, int height)
    {
        try
        {
            using var buffer = sb.LockBuffer(BitmapBufferAccessMode.Read);
            var plane = buffer.GetPlaneDescription(0);
            int srcStride = plane.Stride;
            int srcSize = srcStride * height;
            byte[] bytes = new byte[srcSize];
            sb.CopyToBuffer(bytes.AsBuffer());

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int dstStride = data.Stride;
                int rowBytes = width * 4;
                int srcOffset = plane.StartIndex;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(bytes, srcOffset + y * srcStride, data.Scan0 + y * dstStride, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            CallLogger.Error("WGC SoftwareBitmap->Bitmap failed", ex);
            return null;
        }
    }

    private static string? EncodeBitmap(Bitmap bmp, int maxW, long quality)
    {
        int targetMaxW = Math.Max(320, maxW);
        Bitmap toEncode = bmp;
        bool scaled = false;
        if (bmp.Width > targetMaxW)
        {
            int nh = Math.Max(1, (int)(bmp.Height * (targetMaxW / (double)bmp.Width)));
            var small = new Bitmap(targetMaxW, nh, PixelFormat.Format24bppRgb);
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                sg.DrawImage(bmp, 0, 0, targetMaxW, nh);
            }
            toEncode = small;
            scaled = true;
        }

        try
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (encoder is not null)
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 20L, 95L));
                toEncode.Save(ms, encoder, ep);
            }
            else
            {
                toEncode.Save(ms, ImageFormat.Jpeg);
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        finally
        {
            if (scaled) toEncode.Dispose();
        }
    }

    private static GraphicsCaptureItem? CreateItem(ScreenShareSource src)
    {
        var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
        IntPtr hstring = MarshalString.FromManaged("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            int hr = RoGetActivationFactory(hstring, ref interopGuid, out var interopPtr);
            if (hr != 0 || interopPtr == IntPtr.Zero) { CallLogger.Error($"WGC RoGetActivationFactory hr=0x{hr:X8}"); return null; }
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            try
            {
                var guid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                if (src.Kind == ScreenShareKind.Window && src.Handle != IntPtr.Zero)
                {
                    hr = interop.CreateForWindow(src.Handle, ref guid, out var itemPtr);
                    if (hr != 0 || itemPtr == IntPtr.Zero) { CallLogger.Error($"WGC CreateForWindow hr=0x{hr:X8} hwnd={src.Handle}"); return null; }
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                }

                nint hmon = src.Handle;
                if (hmon == IntPtr.Zero)
                    hmon = MonitorFromPoint(new POINT { X = src.Left + 1, Y = src.Top + 1 }, 2);
                if (hmon == IntPtr.Zero) { CallLogger.Error("WGC no monitor handle"); return null; }
                hr = interop.CreateForMonitor(hmon, ref guid, out var monPtr);
                if (hr != 0 || monPtr == IntPtr.Zero) { CallLogger.Error($"WGC CreateForMonitor hr=0x{hr:X8} hmon={hmon}"); return null; }
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(monPtr);
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            MarshalString.DisposeAbi(hstring);
        }
    }

    private static IDirect3DDevice? CreateDirect3DDevice()
    {
        uint[] flagSets = { 0x2u /*BGRA_SUPPORT*/, 0u };
        foreach (int driverType in new[] { 1 /*HARDWARE*/, 5 /*WARP*/ })
        foreach (uint flags in flagSets)
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, flags,
                null, 0, 7, out var d3dDevice, out _, out var context);
            if (hr != 0 || d3dDevice == IntPtr.Zero)
            {
                CallLogger.Info($"WGC D3D11CreateDevice driver={driverType} flags=0x{flags:X} hr=0x{hr:X8}");
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
                continue;
            }
            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(d3dDevice, out var inspectable);
                if (hr != 0 || inspectable == IntPtr.Zero)
                {
                    CallLogger.Info($"WGC CreateDirect3D11DeviceFromDXGIDevice driver={driverType} flags=0x{flags:X} hr=0x{hr:X8}");
                    continue;
                }
                CallLogger.Info($"WGC D3D device created driver={driverType} flags=0x{flags:X}");
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
            }
        }
        CallLogger.Error("WGC failed to create any D3D11 device");
        return null;
    }

    private static void TrySetBorder(GraphicsCaptureSession session, bool value)
    {
        try { session.GetType().GetProperty("IsBorderRequired")?.SetValue(session, value); } catch { }
    }

    private static void TrySetCursor(GraphicsCaptureSession session, bool value)
    {
        try { session.GetType().GetProperty("IsCursorCaptureEnabled")?.SetValue(session, value); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr window, [In] ref Guid iid, out IntPtr result);
        int CreateForMonitor(IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int GetAdapter(out IntPtr pAdapter);
        int CreateSurface(IntPtr pDesc, uint NumSurfaces, uint Usage, IntPtr pSharedResource, out IntPtr ppSurface);
        int QueryResourceResidency(IntPtr ppResources, IntPtr pResidencyStatus, uint NumResources);
        int SetGPUThreadPriority(int Priority);
        int GetGPUThreadPriority(out int pPriority);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        uint[]? pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device { }
}