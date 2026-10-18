using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace EveWatch.Core.Capture.Wgc;

/// <summary>
/// Windows Graphics Capture 所需的少量互操作。
/// <para>
/// WGC 的托管投影（GraphicsCaptureItem / FramePool / Session）可直接用，但有三处必须自己接：
/// ① 用 HWND 建 <see cref="GraphicsCaptureItem"/>；
/// ② 把 D3D11 设备包成 WinRT 的 IDirect3DDevice；
/// ③ 从 frame.Surface 取回 ID3D11Texture2D。
/// </para>
/// </summary>
internal static class WgcNative
{
    /// <summary>IGraphicsCaptureItemInterop 的 IID（用于 CreateForWindow）。</summary>
    private static readonly Guid InteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>ID3D11Texture2D 的 IID。</summary>
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>
    /// IInspectable 的 IID。CreateForWindow 会按 riid 对新对象做一次 QI，
    /// 传所有 WinRT 对象的公共基接口 IInspectable 必然成功。
    /// 注意：不能用 typeof(GraphicsCaptureItem).GUID，那是类型 GUID 而不是 IID。
    /// </summary>
    private static readonly Guid InspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    // ---- HSTRING ----
    // 注意：不能用 [MarshalAs(UnmanagedType.HString)]，.NET Core 不支持该编组。

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    // ---- D3D11 ----

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint item);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint item);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out nint p);
    }

    /// <summary>用窗口句柄创建捕获项。</summary>
    internal static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";

        var hr = WindowsCreateString(classId, classId.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);

        nint factoryPtr = nint.Zero;
        try
        {
            hr = RoGetActivationFactory(hstring, in InteropIid, out factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                hr = interop.CreateForWindow(hwnd, in InspectableIid, out var itemPtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            if (factoryPtr != nint.Zero)
            {
                Marshal.Release(factoryPtr);
            }

            _ = WindowsDeleteString(hstring);
        }
    }

    /// <summary>把 D3D11 设备包成 WinRT 的 IDirect3DDevice（FramePool 需要它）。</summary>
    internal static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice ToWinRtDevice(nint dxgiDevicePtr)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var inspectablePtr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            return WinRT.MarshalInspectable<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>
                .FromAbi(inspectablePtr);
        }
        finally
        {
            Marshal.Release(inspectablePtr);
        }
    }

    /// <summary>从帧的 Surface 上取出底层 ID3D11Texture2D 指针（调用方负责 Release）。</summary>
    internal static nint GetTexturePointer(Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        var surfacePtr = WinRT.MarshalInspectable<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>
            .FromManaged(surface);

        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown((nint)surfacePtr);
            try
            {
                var hr = access.GetInterface(in Texture2DIid, out var texturePtr);
                Marshal.ThrowExceptionForHR(hr);
                return texturePtr;
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            // FromManaged 内部是 CreateMarshaler().Detach()，返回的是归调用方持有的引用，必须配对释放。
            // 漏掉这一句会让每次采集泄漏一个 surface：帧池的纹理被永久钉住，显存只涨不降。
            WinRT.MarshalInspectable<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>.DisposeAbi(surfacePtr);
        }
    }
}
