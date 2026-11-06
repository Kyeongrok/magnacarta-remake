using System.Numerics;
using System.Runtime.InteropServices;
using MagnaCarta.Data;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MagnaCarta.Render;

/// <summary>모든 셰이더가 같이 쓰는 상수. HLSL <c>cbuffer Constants</c> 와 짝.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Constants
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 World;
    public Vector4 Rect;          // 화면 그림: 왼쪽 위·오른쪽 아래 (NDC)
    public Vector4 Tint;          // 곱할 색 · 알파
    public Vector4 LightDir;      // xyz 빛이 가는 쪽, w 쓰지 않음
    public Vector4 LightColor;
    public Vector4 Ambient;
    public Vector4 Fog;           // x 시작, y 끝, z 1 이면 라이트맵 있음
    public Vector4 FogColor;
    public Vector4 CameraPos;
    // 필드 점 빛 넷(fld 0x428 기록) — 자리 xyz · w 바깥 반경(0 이면 없음), 색 rgb · w 안쪽 반경
    public Vector4 Point0Pos, Point1Pos, Point2Pos, Point3Pos;
    public Vector4 Point0Col, Point1Col, Point2Col, Point3Col;
}

/// <summary>D3D11 장치·스왑체인·깊이 버퍼와 그리기 상태들.</summary>
internal sealed unsafe class Gfx : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView _back = null!;
    private ID3D11Texture2D _depthTex = null!;
    private ID3D11DepthStencilView _depth = null!;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _wrap, _clamp, _point;
    private readonly ID3D11BlendState _opaque, _alpha, _additive;
    private readonly ID3D11DepthStencilState _depthWrite, _depthRead, _depthNone;
    private readonly ID3D11RasterizerState _cullNone, _cullBack, _cullFront;

    private static readonly string? MapCullMode = Environment.GetEnvironmentVariable("MC_MAPCULL");

    /// <summary>
    /// 맵은 한쪽 면만 — 원본(D3D 왼손·기본 CCW 버림)을 오른손 카메라로 옮기면 「앞」 감김을 버려야 같아진다.
    /// 방 밖에 선 감시 카메라(752 길버트 집무실 등)가 벽 바깥면을 뚫고 안을 본다. 비교용 MC_MAPCULL=none|back.
    /// </summary>
    public void MapCull()
    {
        if (MapCullMode == "none") return;
        Context.RSSetState(MapCullMode == "back" ? _cullBack : _cullFront);
    }

    public Gfx(IntPtr hwnd, int width, int height)
    {
        Width = width; Height = height;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device; Context = context;
        using (var dxgi = Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
            _swapChain = factory.CreateSwapChainForHwnd(Device, hwnd, new SwapChainDescription1
            {
                Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
            });
        _constants = Device.CreateBuffer(new BufferDescription((uint)sizeof(Constants), BindFlags.ConstantBuffer));
        _wrap = Device.CreateSamplerState(SamplerDescription.AnisotropicWrap);
        _clamp = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        _point = Device.CreateSamplerState(SamplerDescription.PointClamp);
        _opaque = Device.CreateBlendState(BlendDescription.Opaque);
        _alpha = Device.CreateBlendState(BlendDescription.NonPremultiplied);
        _additive = Device.CreateBlendState(BlendDescription.Additive);
        _depthWrite = Device.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthRead = Device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _depthNone = Device.CreateDepthStencilState(DepthStencilDescription.None);
        _cullNone = Device.CreateRasterizerState(RasterizerDescription.CullNone);
        _cullBack = Device.CreateRasterizerState(RasterizerDescription.CullBack);
        _cullFront = Device.CreateRasterizerState(RasterizerDescription.CullFront);
        CreateTargets();
    }

    private void CreateTargets()
    {
        using (var back = _swapChain.GetBuffer<ID3D11Texture2D>(0)) _back = Device.CreateRenderTargetView(back);
        _depthTex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.DepthStencil,
        });
        _depth = Device.CreateDepthStencilView(_depthTex);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height)) return;
        Width = width; Height = height;
        Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _back.Dispose(); _depth.Dispose(); _depthTex.Dispose();
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        CreateTargets();
    }

    public void Begin(Color4 clear)
    {
        Context.OMSetRenderTargets(_back, _depth);
        Context.RSSetViewport(0, 0, Width, Height);
        Context.ClearRenderTargetView(_back, clear);
        Context.ClearDepthStencilView(_depth, DepthStencilClearFlags.Depth, 1f, 0);
        Context.VSSetConstantBuffer(0, _constants);
        Context.PSSetConstantBuffer(0, _constants);
        Context.PSSetSampler(0, _wrap);
        Context.PSSetSampler(1, _clamp);
        Context.PSSetSampler(2, _point);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    public void Set(in Constants c) => Context.UpdateSubresource(in c, _constants);

    public void Opaque(bool cull = false)
    {
        Context.OMSetBlendState(_opaque);
        Context.OMSetDepthStencilState(_depthWrite);
        Context.RSSetState(cull ? _cullBack : _cullNone);
    }

    public void Translucent()
    {
        Context.OMSetBlendState(_alpha);
        Context.OMSetDepthStencilState(_depthRead);
        Context.RSSetState(_cullNone);
    }

    /// <summary>더하기 섞기(검은 바탕 효과) — 깊이는 읽기만.</summary>
    public void AdditiveBlend()
    {
        Context.OMSetBlendState(_additive);
        Context.OMSetDepthStencilState(_depthRead);
        Context.RSSetState(_cullNone);
    }

    public void Overlay()
    {
        Context.OMSetBlendState(_alpha);
        Context.OMSetDepthStencilState(_depthNone);
        Context.RSSetState(_cullNone);
    }

    public void Present() => _swapChain.Present(1, PresentFlags.None);

    /// <summary>지금 뒷버퍼를 PNG 로 적는다(확인용). Present 앞에서 부른다.</summary>
    public void Capture(string path)
    {
        Context.Flush();
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Context.CopyResource(staging, back);
        var map = Context.Map(staging, 0, MapMode.Read);
        try
        {
            using var bmp = new System.Drawing.Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Width, Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy((void*)(map.DataPointer + y * (nint)map.RowPitch), (void*)(bits.Scan0 + y * bits.Stride), bits.Stride, Width * 4);
            bmp.UnlockBits(bits);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { Context.Unmap(staging, 0); }
    }

    // ── 만들기 도우미 ────────────────────────────────────────────────────────

    public (ID3D11VertexShader, ID3D11PixelShader, ReadOnlyMemory<byte>) Compile(string source, string vs, string ps)
    {
        // Vortice 가 글자 수를 바이트 수로 넘겨 한글 주석이 있으면 끝이 잘린다 — ASCII 만 남긴다
        source = new string(source.Where(ch => ch < 128).ToArray());
        var v = Compiler.Compile(source, vs, "mc", "vs_4_0");
        var p = Compiler.Compile(source, ps, "mc", "ps_4_0");
        return (Device.CreateVertexShader(v.Span), Device.CreatePixelShader(p.Span), v);
    }

    /// <summary>BGRA 그림을 밉맵까지 지은 텍스처로 올린다.</summary>
    public ID3D11ShaderResourceView Texture(Image32 img, bool mips = true)
    {
        uint levels = mips ? (uint)Math.Max(1, (int)Math.Log2(Math.Max(img.Width, img.Height)) + 1) : 1;
        using var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)img.Width, Height = (uint)img.Height, MipLevels = levels, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource | (mips ? BindFlags.RenderTarget : 0),
            MiscFlags = mips ? ResourceOptionFlags.GenerateMips : 0,
        });
        fixed (byte* p = img.Bgra)
            Context.UpdateSubresource(tex, 0, null, (IntPtr)p, (uint)img.Width * 4, 0);
        var view = Device.CreateShaderResourceView(tex);
        if (mips) Context.GenerateMips(view);
        return view;
    }

    /// <summary>틀마다 고쳐 올리는 그림(화면 글·막대).</summary>
    public ID3D11Texture2D DynamicTexture(int width, int height, out ID3D11ShaderResourceView view)
    {
        var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        });
        view = Device.CreateShaderResourceView(tex);
        return tex;
    }

    public void UploadTexture(ID3D11Texture2D tex, Image32 img)
    {
        var map = Context.Map(tex, 0, MapMode.WriteDiscard);
        fixed (byte* p = img.Bgra)
            for (int y = 0; y < img.Height; y++)
                Buffer.MemoryCopy(p + y * img.Width * 4, (void*)(map.DataPointer + y * (nint)map.RowPitch), img.Width * 4, img.Width * 4);
        Context.Unmap(tex, 0);
    }

    public ID3D11Buffer VertexBuffer<T>(T[] data) where T : unmanaged
        => Device.CreateBuffer(data, BindFlags.VertexBuffer);

    public ID3D11Buffer DynamicVertexBuffer<T>(int count) where T : unmanaged
        => Device.CreateBuffer(new BufferDescription((uint)(count * sizeof(T)), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

    public void Upload<T>(ID3D11Buffer buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        var map = Context.Map(buffer, 0, MapMode.WriteDiscard);
        data.CopyTo(new Span<T>((void*)map.DataPointer, data.Length));
        Context.Unmap(buffer, 0);
    }

    public void Dispose()
    {
        _cullBack.Dispose(); _cullFront.Dispose(); _cullNone.Dispose(); _depthNone.Dispose(); _depthRead.Dispose(); _depthWrite.Dispose();
        _additive.Dispose(); _alpha.Dispose(); _opaque.Dispose(); _point.Dispose(); _clamp.Dispose(); _wrap.Dispose(); _constants.Dispose();
        _depth.Dispose(); _depthTex.Dispose(); _back.Dispose(); _swapChain.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}
