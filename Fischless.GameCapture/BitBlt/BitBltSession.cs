using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using Vanara.PInvoke;

namespace Fischless.GameCapture.BitBlt;

public class BitBltSession : IDisposable
{
    // 窗口句柄
    private readonly HWND _hWnd;

    private readonly object _lockObject = new();

    // 位图句柄
    private Gdi32.SafeHBITMAP _hBitmap;

    // 位图数据指针，这个指针会在位图释放时自动释放
    private IntPtr _bitsPtr;

    // 位图数据一行字节数
    private readonly int _stride;

    // 缓冲区 CompatibleDC
    private Gdi32.SafeHDC _hdcDest;

    // 来源DC
    private User32.SafeReleaseHDC _hdcSrc;

    // 旧位图，析构时一起释放掉
    private HGDIOBJ _oldBitmap;

    // Bitmap buffer 大小
    private readonly int _bufferSize;

    // Bitmap 内存池
    private readonly ConcurrentStack<IntPtr> _bufferPool = [];

    // 【诊断】跨会话静态计数，供 app 层心跳读取，定位缓冲池泄漏（Mat 未释放会导致每帧 AllocHGlobal 新 6MB）。
    // 排查完可删除本区块及其引用。
    public static long DiagTotalBufferAllocations;   // 累计 AllocHGlobal 次数
    public static long DiagLiveBufferCount;          // 已分配但尚未回池的缓冲数(=未释放的 Mat 数)
    public static double DiagLastGdiBlitMs;          // 上一次 GDI BitBlt+拷贝耗时

    // 窗口原宽高
    public int Width { get; }
    public int Height { get; }

    /// <summary>
    ///     不是所有的失效情况都能被检测到
    /// </summary>
    /// <returns></returns>
    public bool Invalid => _hWnd.IsNull || _hdcSrc.IsInvalid || _hdcDest.IsInvalid || _hBitmap.IsInvalid || _bitsPtr == 0;

    public BitBltSession(HWND hWnd, int w, int h)
    {
        if (hWnd.IsNull) throw new Exception("hWnd is invalid");

        _hWnd = hWnd;

        if (w <= 0 || h <= 0) throw new Exception("Invalid width or height");

        // 这俩仅做标记
        Width = w;
        Height = h;

        lock (_lockObject)
        {
            try
            {
                _hdcSrc = User32.GetDC(_hWnd);
                if (_hdcSrc.IsInvalid) throw new Exception($"Failed to get DC for {_hWnd}");

                var hdcRasterCaps = Gdi32.GetDeviceCaps(_hdcSrc, Gdi32.DeviceCap.RASTERCAPS);
                if ((hdcRasterCaps | 1) == 0) // RC_BITBLT
                    // 设备不支持 BitBlt
                    throw new Exception("BitBlt not supported");

                var hdcSrcPixel = Gdi32.GetDeviceCaps(_hdcSrc, Gdi32.DeviceCap.BITSPIXEL);
                // 颜色位数
                if (hdcSrcPixel != 32 && hdcSrcPixel != 24)
                    // 目前只考虑支持24/32位像素格式
                    throw new Exception("BitBlt only support 24 or 32 bit pixel color");

                var hdcSrcPlanes = Gdi32.GetDeviceCaps(_hdcSrc, Gdi32.DeviceCap.PLANES);
                // 颜色平面数
                if (hdcSrcPlanes > 1)
                    // 意味着色深不是标准色深
                    throw new Exception("BitBlt only support 1 plane");

                var hdcClip = Gdi32.GetDeviceCaps(_hdcSrc, Gdi32.DeviceCap.CLIPCAPS);
                if (hdcClip == 0)
                    // 设备不支持剪切出单一窗口
                    throw new Exception("Device does not support clipping");

                _hdcDest = Gdi32.CreateCompatibleDC(_hdcSrc);
                if (_hdcDest.IsInvalid)
                {
                    Debug.Fail("Failed to create CompatibleDC");
                    throw new Exception($"Failed to create CompatibleDC for {_hWnd}");
                }

                var bmi = new Gdi32.BITMAPINFO
                {
                    bmiHeader = new Gdi32.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(),
                        biWidth = Width,
                        biHeight = -Height, // Top-down image
                        biPlanes = 1,
                        biBitCount = 24,
                        biCompression = Gdi32.BitmapCompressionMode.BI_RGB, // 内存里是BGR
                        biSizeImage = 0
                    }
                };
                _hBitmap = Gdi32.CreateDIBSection(_hdcDest, bmi, Gdi32.DIBColorMode.DIB_RGB_COLORS, out _bitsPtr,
                    IntPtr.Zero);

                if (_hBitmap.IsInvalid || _bitsPtr == 0)
                {
                    if (!_hBitmap.IsInvalid) Gdi32.DeleteObject(_hBitmap);

                    throw new Exception($"Failed to create dIB section for {_hdcDest}");
                }

                var bitmap = Gdi32.GetObject<Gdi32.BITMAP>(_hBitmap);
                if (bitmap.bmPlanes != 1 || bitmap.bmBitsPixel != 24)
                    throw new Exception("Unsupported bitmap format");

                _stride = bitmap.bmWidthBytes;
                _bufferSize = bitmap.bmWidth * bitmap.bmHeight * 3;

                _oldBitmap = Gdi32.SelectObject(_hdcDest, _hBitmap);
                if (_oldBitmap.IsNull) throw new Exception("Failed to select object");
            }
            catch (Exception)
            {
                ReleaseResources();
                throw;
            }
            finally
            {
                Gdi32.GdiFlush();
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            ReleaseResources();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     调用GDI复制到缓冲区并返回新Mat
    /// </summary>
    public unsafe Mat? GetImage()
    {
        lock (_lockObject)
        {
            var _diagBlitSw = Stopwatch.StartNew(); // 【诊断】
            // 截图
            var success = Gdi32.BitBlt(_hdcDest, 0, 0, Width, Height,
                _hdcSrc, 0, 0, Gdi32.RasterOperationMode.SRCCOPY);
            if (!success || !Gdi32.GdiFlush())
            {
                _diagBlitSw.Stop();
                DiagLastGdiBlitMs = _diagBlitSw.Elapsed.TotalMilliseconds; // 【诊断】即使失败也记录耗时
                return null;
            }

            // 新Mat
            var buffer = AcquireBuffer();
            var step = Width * 3;
            if (_stride == step)
            {
                Buffer.MemoryCopy(_bitsPtr.ToPointer(), buffer.ToPointer(), _bufferSize, _bufferSize);
            }
            else
            {
                for (var i = 0; i < Height; i++)
                {
                    Buffer.MemoryCopy((void*)(_bitsPtr + _stride * i), (void*)(buffer + step * i), step, step);
                }
            }
            _diagBlitSw.Stop();
            DiagLastGdiBlitMs = _diagBlitSw.Elapsed.TotalMilliseconds; // 【诊断】GDI 截图+拷贝耗时
            return BitBltMat.FromPixelData(this, Height, Width, MatType.CV_8UC3, buffer, step);
        }
    }

    private IntPtr AcquireBuffer()
    {
        Interlocked.Increment(ref DiagLiveBufferCount); // 【诊断】取出一个缓冲(对应一个 Mat 诞生)
        if (!_bufferPool.TryPop(out var buffer))
        {
            Interlocked.Increment(ref DiagTotalBufferAllocations); // 【诊断】池空→真的分配新 6MB
            buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        return buffer;
    }

    public void ReleaseBuffer(IntPtr buffer)
    {
        Interlocked.Decrement(ref DiagLiveBufferCount); // 【诊断】Mat 释放→缓冲回池
        _bufferPool.Push(buffer);
    }

    /// <summary>
    ///     需加锁环境下访问，释放资源
    /// </summary>
    private void ReleaseResources()
    {
        Gdi32.GdiFlush();

        if (!_oldBitmap.IsNull)
        {
            // 先选回资源再释放_hBitmap
            Gdi32.SelectObject(_hdcDest, _oldBitmap);
            _oldBitmap = Gdi32.SafeHBITMAP.Null;
        }


        if (!_hBitmap.IsNull)
        {
            Gdi32.DeleteObject(_hBitmap);
            _hBitmap = Gdi32.SafeHBITMAP.Null;
        }


        if (!_hdcDest.IsNull)
        {
            Gdi32.DeleteDC(_hdcDest);
            _hdcDest = Gdi32.SafeHDC.Null;
        }

        if (_hdcSrc != IntPtr.Zero)
        {
            User32.ReleaseDC(_hWnd, _hdcSrc);
            _hdcSrc = User32.SafeReleaseHDC.Null;
        }

        _bitsPtr = IntPtr.Zero;

        foreach (var buffer in _bufferPool)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
