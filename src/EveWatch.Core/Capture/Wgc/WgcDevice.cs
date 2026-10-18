using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EveWatch.Core.Capture.Wgc;

/// <summary>
/// 进程级共享的 D3D11 设备。
///
/// 为什么共享：
///   · <c>ID3D11Device</c> 本身线程安全，N 个采集会话共用一个即可，省掉 N 份驱动侧开销；
///   · 但 <c>ID3D11DeviceContext</c>（即时上下文）不是线程安全的，
///     所有 GPU 操作必须用 <see cref="Gate"/> 这把短锁串行化。
///
/// 200ms 节拍下每次 GPU 操作只有 1ms 量级，且多账号启动时已错峰，锁竞争可以忽略。
/// </summary>
internal sealed class WgcDevice : IDisposable
{
    private static readonly Lazy<WgcDevice> LazyInstance = new(() => new WgcDevice(), isThreadSafe: true);

    private WgcDevice()
    {
        IDXGIAdapter? noAdapter = null;

        var hr = D3D11.D3D11CreateDevice(
            noAdapter,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,   // WGC 要求 BGRA 支持
            [FeatureLevel.Level_11_0],
            out var device,
            out _,
            out var context);

        if (hr.Failure || device is null || context is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice 失败：{hr.Description}");
        }

        Device = device;
        Context = context;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        WinRtDevice = WgcNative.ToWinRtDevice(dxgiDevice.NativePointer);
    }

    /// <summary>进程级共享实例（首次访问时创建）。</summary>
    public static WgcDevice Shared => LazyInstance.Value;

    /// <summary>GPU 串行化门（即时上下文不是线程安全的）。</summary>
    public object Gate { get; } = new();

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice WinRtDevice { get; }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}
