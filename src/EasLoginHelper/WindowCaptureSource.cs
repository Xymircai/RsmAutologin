// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EasLoginHelper;
public sealed class CaptureFailureException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface ICaptchaFrameSource
{
    Task<Bitmap> CaptureAsync(IntPtr window, Rectangle screenRegion, CancellationToken cancellation);
}

public static class CaptureGeometry
{
    public static Rectangle Map(Rectangle screenRegion, Rectangle windowBounds, Size frameSize)
    {
        if (windowBounds.IsEmpty || !windowBounds.Contains(screenRegion) || frameSize.Width < 1 || frameSize.Height < 1)
            throw new CaptureFailureException("Capture.RegionOutsideWindow", "验证码区域超出目标窗口。");
        var sx = frameSize.Width / (double)windowBounds.Width; var sy = frameSize.Height / (double)windowBounds.Height;
        return Rectangle.FromLTRB((int)Math.Round((screenRegion.Left - windowBounds.Left) * sx), (int)Math.Round((screenRegion.Top - windowBounds.Top) * sy),
            (int)Math.Round((screenRegion.Right - windowBounds.Left) * sx), (int)Math.Round((screenRegion.Bottom - windowBounds.Top) * sy));
    }
}

// Captures the allowed HWND's DWM surface, never pixels from another foreground window.
public sealed class WindowCaptureSource : ICaptchaFrameSource, IDisposable
{
    public string Backend { get; private set; } = "NotInitialized";
    public static WindowCaptureSource Shared { get; } = new();
    private IDirect3DDevice? _device;
    private readonly object _sync = new();
    private bool _disposed;

    public async Task<Bitmap> CaptureAsync(IntPtr window, Rectangle screenRegion, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported()) throw new CaptureFailureException("Capture.Unsupported", "此 Windows 环境不支持后台窗口捕获，请运行环境自检。");
        if (!Native.IsWindowVisible(window) || Native.IsIconic(window))
            throw new CaptureFailureException("Capture.WindowUnavailable", "目标窗口不可见或已最小化，请恢复窗口。");
        var client = Native.ClientBounds(window); var bounds = Bounds(window); var dpi = Native.GetDpiForWindow(window);
        if (!client.Contains(screenRegion)) throw new CaptureFailureException("Capture.RegionOutsideClient", "验证码区域尚未就绪。");
        IDirect3DDevice device;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            device = _device ??= CreateDevice();
        }
        var item = CreateItem(window);
        if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new CaptureFailureException("Capture.NoContent", "窗口没有可捕获的内容。");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = 0;
        pool.FrameArrived += async (sender, _) =>
        {
            if (completion.Task.IsCompleted || Interlocked.CompareExchange(ref processing, 1, 0) != 0) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) { Interlocked.Exchange(ref processing, 0); return; }
                using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
                if (Native.IsIconic(window) || Bounds(window).Size != bounds.Size || Native.ClientBounds(window).Size != client.Size || Native.GetDpiForWindow(window) != dpi)
                    throw new CaptureFailureException("Capture.LayoutChanged", "窗口布局发生变化，请等待下一次识别。");
                var size = new Size(frame.ContentSize.Width, frame.ContentSize.Height);
                if (size.Width != software.PixelWidth || size.Height != software.PixelHeight)
                    throw new CaptureFailureException("Capture.FrameSizeChanged", "捕获帧尺寸已变化。");
                var region = CaptureGeometry.Map(screenRegion, bounds, size);
                var bitmap = CopyRegion(software, region);
                if (!completion.TrySetResult(bitmap)) bitmap.Dispose();
            }
            catch (Exception e) { completion.TrySetException(e); }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        session.StartCapture();
        try { return await completion.Task; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new CaptureFailureException("Capture.Timeout", "未收到有效的后台验证码帧，请恢复窗口后重试。"); }
    }

    private static Bitmap CopyRegion(SoftwareBitmap source, Rectangle region)
    {
        if (region.Width < 20 || region.Height < 10 || !new Rectangle(0, 0, source.PixelWidth, source.PixelHeight).Contains(region))
            throw new CaptureFailureException("Capture.InvalidCrop", "验证码裁剪区域无效。");
        var bytes = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        var buffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length);
        source.CopyToBuffer(buffer);
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (var y = 0; y < region.Height; y++)
                    Marshal.Copy(bytes, ((region.Top + y) * source.PixelWidth + region.Left) * 4, pixels.Scan0 + y * pixels.Stride, region.Width * 4);
            }
            finally { bitmap.UnlockBits(pixels); }
            var samples = new List<int>();
            for (var y = 0; y < bitmap.Height; y += 3) for (var x = 0; x < bitmap.Width; x += 3)
            { var p = bitmap.GetPixel(x, y); samples.Add(p.R + p.G + p.B); }
            if (samples.Max() - samples.Min() < 65) throw new CaptureFailureException("Capture.BlankFrame", "后台验证码帧为空白，已停止提交。");
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static Rectangle Bounds(IntPtr window)
    {
        var result = DwmGetWindowAttribute(window, 9, out Native.Rect rectangle, Marshal.SizeOf<Native.Rect>());
        return result == 0 && !rectangle.ToRectangle().IsEmpty ? rectangle.ToRectangle() : Native.WindowBounds(window);
    }
    private static GraphicsCaptureItem CreateItem(IntPtr window)
    {
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;
        Marshal.ThrowExceptionForHR(WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", 44, out var name));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            try
            {
                var itemIid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, ref itemIid, out item));
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally { Marshal.ReleaseComObject(interop); }
        }
        finally { if (item != IntPtr.Zero) Marshal.Release(item); if (factory != IntPtr.Zero) Marshal.Release(factory); WindowsDeleteString(name); }
    }
    private IDirect3DDevice CreateDevice()
    {
        Backend = "Hardware";
        var result = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var native, out _, out var context);
        if (result < 0) { Backend = "WARP"; result = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out native, out _, out context); }
        Marshal.ThrowExceptionForHR(result);
        IntPtr dxgi = IntPtr.Zero, projected = IntPtr.Zero;
        try
        {
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != IntPtr.Zero) Marshal.Release(projected);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (native != IntPtr.Zero) Marshal.Release(native);
        }
    }
    public void Dispose() { lock (_sync) { _disposed = true; _device?.Dispose(); _device = null; } }

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Native.Rect value, int size);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk,
        out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr projected);
}
