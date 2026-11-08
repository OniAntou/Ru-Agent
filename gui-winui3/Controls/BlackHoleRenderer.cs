using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RuApp.Controls;

/// <summary>
/// Draws <see cref="BlackHoleShader"/> into a <see cref="SwapChainPanel"/> with Direct3D 11.
/// Each frame: the scene pass writes the lensed window and, separately, everything that glows
/// (HDR); the glow is downsampled and blurred at a quarter and a sixteenth of the size; the
/// composite pass tone-maps light plus bloom over the window and adds the lens effects. Shaders
/// are compiled once, off the UI thread.
/// </summary>
internal sealed unsafe class BlackHoleRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Scene
    {
        public Vector2 Resolution;
        public float Time;
        public float CameraDistance;
        public Vector2 Center;
        public float TanHalf;
        public float Disk;
        public float Swirl;
        public float Pull;
        public float Phase;
        public float Flash;
        public float Shock;
        public float Heat;
        public float HoleOn;
        public float ShadowPx;
        public Vector2 ShakeOffset;
        public float MaxShock;
        public float Fade;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BloomPass
    {
        public Vector2 Direction;
        public Vector2 SourceTexel;
        public Vector2 TargetTexel;
        public Vector2 Pad;
    }

    public sealed record Shaders(byte[] Vertex, byte[] Scene, byte[] Down, byte[] Blur, byte[] Composite);

    /// <summary>An off-screen render target that later passes read.</summary>
    private sealed class Target(ID3D11Texture2D texture, ID3D11RenderTargetView view, ID3D11ShaderResourceView input, int width, int height) : IDisposable
    {
        public ID3D11RenderTargetView View { get; } = view;
        public ID3D11ShaderResourceView Input { get; } = input;
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Dispose()
        {
            Input.Dispose();
            View.Dispose();
            texture.Dispose();
        }
    }

    private static readonly object s_gate = new();
    private static Task<Shaders>? s_shaders;
    private static readonly ID3D11ShaderResourceView[] s_unbound = new ID3D11ShaderResourceView[6];

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11RenderTargetView _output;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _scene;
    private readonly ID3D11PixelShader _down;
    private readonly ID3D11PixelShader _blur;
    private readonly ID3D11PixelShader _composite;
    private readonly ID3D11Texture2D _snapshot;
    private readonly ID3D11ShaderResourceView _snapshotView;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _sceneConstants;
    private readonly ID3D11Buffer _passConstants;
    private readonly Target _background;
    private readonly Target _light;
    private readonly Target _nearA;
    private readonly Target _nearB;
    private readonly Target _farA;
    private readonly Target _farB;
    private readonly SwapChainPanel _panel;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Compiles the shaders on the thread pool, once; a failure is retried next time.</summary>
    public static Task<Shaders> CompileAsync()
    {
        lock (s_gate)
        {
            if (s_shaders is { IsFaulted: true }) s_shaders = null;
            return s_shaders ??= Task.Run(() =>
            {
                byte[] Compile(string entry, string profile) =>
                    Compiler.Compile(BlackHoleShader.Source, entry, "blackhole.hlsl", profile, ShaderFlags.OptimizationLevel3).ToArray();
                return new Shaders(Compile("VSMain", "vs_5_0"), Compile("PSScene", "ps_5_0"), Compile("PSDown", "ps_5_0"),
                    Compile("PSBlur", "ps_5_0"), Compile("PSComposite", "ps_5_0"));
            });
        }
    }

    /// <param name="snapshot">The window as premultiplied BGRA, <paramref name="snapshotWidth"/> × <paramref name="snapshotHeight"/>.</param>
    /// <param name="width">Render width in pixels.</param>
    /// <param name="height">Render height in pixels.</param>
    /// <param name="scaleX">Swap-chain pixels per panel DIP, horizontally.</param>
    /// <param name="scaleY">Swap-chain pixels per panel DIP, vertically.</param>
    public BlackHoleRenderer(SwapChainPanel panel, Shaders shaders, byte[] snapshot,
        int snapshotWidth, int snapshotHeight, int width, int height, float scaleX, float scaleY)
    {
        _panel = panel;
        Width = width;
        Height = height;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device!, out _context!).CheckError();

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, description);
        }

        // The swap chain is sized in pixels; the panel in DIPs.
        using (var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>())
            swapChain2.MatrixTransform = new Matrix3x2(1 / scaleX, 0, 0, 1 / scaleY, 0, 0);
        AttachToPanel(panel, _swapChain.NativePointer);

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _output = _device.CreateRenderTargetView(backBuffer);

        _vertex = _device.CreateVertexShader(shaders.Vertex);
        _scene = _device.CreatePixelShader(shaders.Scene);
        _down = _device.CreatePixelShader(shaders.Down);
        _blur = _device.CreatePixelShader(shaders.Blur);
        _composite = _device.CreatePixelShader(shaders.Composite);

        fixed (byte* pixels = snapshot)
        {
            var texture = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)snapshotWidth, (uint)snapshotHeight, 1, 1, BindFlags.ShaderResource);
            _snapshot = _device.CreateTexture2D(texture, new SubresourceData(pixels, (uint)snapshotWidth * 4));
        }

        _snapshotView = _device.CreateShaderResourceView(_snapshot);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _sceneConstants = _device.CreateBuffer(new BufferDescription((uint)sizeof(Scene), BindFlags.ConstantBuffer));
        _passConstants = _device.CreateBuffer(new BufferDescription((uint)sizeof(BloomPass), BindFlags.ConstantBuffer));

        _background = CreateTarget(width, height, Format.B8G8R8A8_UNorm);
        _light = CreateTarget(width, height, Format.R16G16B16A16_Float);
        _nearA = CreateTarget(Math.Max(1, width / 4), Math.Max(1, height / 4), Format.R16G16B16A16_Float);
        _nearB = CreateTarget(_nearA.Width, _nearA.Height, Format.R16G16B16A16_Float);
        _farA = CreateTarget(Math.Max(1, width / 16), Math.Max(1, height / 16), Format.R16G16B16A16_Float);
        _farB = CreateTarget(_farA.Width, _farA.Height, Format.R16G16B16A16_Float);
    }

    private Target CreateTarget(int width, int height, Format format)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        return new Target(texture, _device.CreateRenderTargetView(texture), _device.CreateShaderResourceView(texture), width, height);
    }

    public void Render(in Scene scene)
    {
        _context.UpdateSubresource(scene, _sceneConstants);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetConstantBuffer(0, _sceneConstants);
        _context.PSSetConstantBuffer(1, _passConstants);

        // 1. The scene: lensed window and glowing light into two targets.
        _context.PSSetShaderResources(0, s_unbound);
        _context.OMSetRenderTargets([_background.View, _light.View]);
        _context.RSSetViewport(new Viewport(Width, Height));
        _context.PSSetShader(_scene);
        _context.PSSetShaderResource(0, _snapshotView);
        _context.Draw(3, 0);

        // 2. Bloom: a tight glow at a quarter size, a wide haze at a sixteenth.
        Pass(_down, _light, _nearA, Vector2.Zero);
        Pass(_blur, _nearA, _nearB, Vector2.UnitX);
        Pass(_blur, _nearB, _nearA, Vector2.UnitY);
        Pass(_down, _nearA, _farA, Vector2.Zero);
        Pass(_blur, _farA, _farB, Vector2.UnitX);
        Pass(_blur, _farB, _farA, Vector2.UnitY);
        Pass(_blur, _farA, _farB, Vector2.UnitX * 2);
        Pass(_blur, _farB, _farA, Vector2.UnitY * 2);

        // 3. Composite to the screen.
        _context.PSSetShaderResources(0, s_unbound);
        _context.OMSetRenderTargets(_output);
        _context.RSSetViewport(new Viewport(Width, Height));
        _context.PSSetShader(_composite);
        _context.PSSetShaderResource(0, _snapshotView);
        _context.PSSetShaderResource(2, _background.Input);
        _context.PSSetShaderResource(3, _light.Input);
        _context.PSSetShaderResource(4, _nearA.Input);
        _context.PSSetShaderResource(5, _farA.Input);
        _context.Draw(3, 0);
        _swapChain.Present(1, PresentFlags.None);
    }

    private void Pass(ID3D11PixelShader shader, Target source, Target target, Vector2 direction)
    {
        var constants = new BloomPass
        {
            Direction = direction,
            SourceTexel = new Vector2(1f / source.Width, 1f / source.Height),
            TargetTexel = new Vector2(1f / target.Width, 1f / target.Height),
        };
        _context.UpdateSubresource(constants, _passConstants);
        _context.PSSetShaderResources(0, s_unbound);
        _context.OMSetRenderTargets(target.View);
        _context.RSSetViewport(new Viewport(target.Width, target.Height));
        _context.PSSetShader(shader);
        _context.PSSetShaderResource(1, source.Input);
        _context.Draw(3, 0);
    }

    /// <summary>ISwapChainPanelNative::SetSwapChain, called through the panel's COM object.</summary>
    private static void AttachToPanel(SwapChainPanel panel, IntPtr swapChain)
    {
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var table = *(IntPtr**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)table[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    public void Dispose()
    {
        try
        {
            AttachToPanel(_panel, IntPtr.Zero);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            // The panel may already be gone with the window; the swap chain is released below anyway.
        }

        _context.ClearState();
        foreach (var target in new[] { _background, _light, _nearA, _nearB, _farA, _farB }) target.Dispose();
        _passConstants.Dispose();
        _sceneConstants.Dispose();
        _sampler.Dispose();
        _snapshotView.Dispose();
        _snapshot.Dispose();
        _composite.Dispose();
        _blur.Dispose();
        _down.Dispose();
        _scene.Dispose();
        _vertex.Dispose();
        _output.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
