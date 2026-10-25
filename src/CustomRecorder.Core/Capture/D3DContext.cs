using System.Runtime.InteropServices;
using CustomRecorder.Core.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CustomRecorder.Core.Capture;

/// <summary>
/// Owns the D3D11 device shared by capture (Windows.Graphics.Capture) and the hardware encoder (Media Foundation).
/// Sharing one device lets captured textures go to the encoder without leaving the GPU.
/// </summary>
public sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>The immediate context is not thread-safe; every caller takes this lock.</summary>
    public object ContextLock { get; } = new();

    public D3DContext()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out var device, out var context);
        if (hr.Failure || device == null || context == null)
        {
            // Fallback without the video flag (some drivers reject it).
            hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out context);
            hr.CheckError();
        }

        Device = device!;
        Context = context!;

        // Media Foundation uses the device from its own threads.
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
        {
            mt.SetMultithreadProtected(true);
        }

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try
        {
            WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public ID3D11Texture2D CreateTexture(int width, int height, bool forEncoder)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = forEncoder ? BindFlags.RenderTarget | BindFlags.ShaderResource : BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(in desc);
    }

    public ID3D11Texture2D CreateStagingTexture(int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(in desc);
    }

    public void Dispose()
    {
        (WinRtDevice as IDisposable)?.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
