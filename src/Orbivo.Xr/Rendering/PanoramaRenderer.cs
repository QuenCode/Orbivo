using Orbivo.Core.Geometry;
using Orbivo.Xr.Graphics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Orbivo.Xr.Rendering;

/// <summary>What the pixel shader writes. Only <see cref="Image"/> is used outside of tests.</summary>
internal enum PanoramaOutput
{
    Image,
    DebugDirection,
    DebugEquirect360Uv,
}

/// <summary>
/// Draws a panorama into one view with a fullscreen triangle (docs/ARCHITECTURE.md, "Rendering").
/// Used for the desktop debug viewport and, from M2, for each eye in the headset.
/// </summary>
/// <remarks>
/// <see cref="Render"/> runs on the render thread every frame: it does not block and does not allocate (CLAUDE.md rule 1).
/// </remarks>
public sealed class PanoramaRenderer : IDisposable
{
    private const string ShaderFile = "panorama.hlsl";

    private static readonly Color4 Black = new(0f, 0f, 0f, 1f);

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _imageShader;
    private readonly ID3D11PixelShader _debugDirectionShader;
    private readonly ID3D11PixelShader _debugUvShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11Buffer _constantBuffer;

    public PanoramaRenderer(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ID3D11Device d3d = device.Device;

        _vertexShader = d3d.CreateVertexShader(
            ShaderCompiler.Compile(ShaderFile, "VsFullscreenTriangle", ShaderCompiler.VertexShaderProfile), null);
        _imageShader = CreatePixelShader(d3d, "PsEquirect360");
        _debugDirectionShader = CreatePixelShader(d3d, "PsDebugDirection");
        _debugUvShader = CreatePixelShader(d3d, "PsDebugEquirect360Uv");

        // Wrap horizontally (360° around), clamp vertically (poles). No mipmaps, see panorama.hlsl.
        _sampler = d3d.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Wrap,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));

        _rasterizerState = d3d.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));

        _constantBuffer = d3d.CreateBuffer(
            ViewConstants.SizeInBytes,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);
    }

    /// <summary>
    /// Renders the view into <paramref name="target"/>. Without a source image the target is cleared to black.
    /// Render thread only.
    /// </summary>
    /// <param name="context">The immediate context of the device.</param>
    /// <param name="target">Render target view; must be an *_SRGB view (gamma, docs/DECISIONS.md).</param>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <param name="view">Orientation and field of view.</param>
    /// <param name="source">Equirect 360 mono image, or null for black.</param>
    /// <param name="brightness">1 = full image, 0 = black.</param>
    public void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in ViewParameters view,
        StillImageTexture? source,
        float brightness = 1f)
    {
        Render(context, target, width, height, view, source?.ShaderResourceView, brightness, PanoramaOutput.Image);
    }

    internal void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in ViewParameters view,
        ID3D11ShaderResourceView? source,
        float brightness,
        PanoramaOutput output)
    {
        context.OMSetRenderTargets(target, null);

        if (output == PanoramaOutput.Image && (source is null || brightness <= 0f))
        {
            context.ClearRenderTargetView(target, Black);
            return;
        }

        WriteConstants(context, ViewConstants.Create(view, brightness));

        context.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        context.RSSetState(_rasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(output switch
        {
            PanoramaOutput.DebugDirection => _debugDirectionShader,
            PanoramaOutput.DebugEquirect360Uv => _debugUvShader,
            _ => _imageShader,
        });
        context.PSSetConstantBuffer(0, _constantBuffer);
        if (source is not null)
        {
            // The debug outputs do not sample, so they run without a source.
            context.PSSetShaderResource(0, source);
        }

        context.PSSetSampler(0, _sampler);
        context.Draw(3, 0);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _rasterizerState.Dispose();
        _sampler.Dispose();
        _debugUvShader.Dispose();
        _debugDirectionShader.Dispose();
        _imageShader.Dispose();
        _vertexShader.Dispose();
    }

    private static ID3D11PixelShader CreatePixelShader(ID3D11Device device, string entryPoint)
    {
        return device.CreatePixelShader(ShaderCompiler.Compile(ShaderFile, entryPoint, ShaderCompiler.PixelShaderProfile), null);
    }

    private unsafe void WriteConstants(ID3D11DeviceContext context, in ViewConstants constants)
    {
        MappedSubresource mapped = context.Map(_constantBuffer, MapMode.WriteDiscard, MapFlags.None);
        *(ViewConstants*)mapped.DataPointer = constants;
        context.Unmap(_constantBuffer, 0);
    }
}
