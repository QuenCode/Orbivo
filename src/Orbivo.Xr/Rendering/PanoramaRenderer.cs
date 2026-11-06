using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Media;
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
    DebugSourceUv,
}

/// <summary>
/// Draws a panorama into one view with a fullscreen triangle (docs/ARCHITECTURE.md, "Rendering").
/// Used for the desktop debug viewport and for each eye in the headset.
/// </summary>
/// <remarks>
/// <see cref="Render(ID3D11DeviceContext, ID3D11RenderTargetView, int, int, in ViewParameters, Eye, in PanoramaSource, float)"/>
/// runs on the render thread every frame: it does not block and does not allocate (CLAUDE.md rule 1).
/// </remarks>
public sealed class PanoramaRenderer : IDisposable
{
    private const string ShaderFile = "panorama.hlsl";

    private static readonly Color4 Black = new(0f, 0f, 0f, 1f);

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _imageShader;
    private readonly ID3D11PixelShader _videoShader;
    private readonly ID3D11PixelShader _debugDirectionShader;
    private readonly ID3D11PixelShader _debugUvShader;
    private readonly ID3D11PixelShader _debugSourceUvShader;
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
        _videoShader = CreatePixelShader(d3d, "PsEquirect360Video");
        _debugDirectionShader = CreatePixelShader(d3d, "PsDebugDirection");
        _debugUvShader = CreatePixelShader(d3d, "PsDebugEquirect360Uv");
        _debugSourceUvShader = CreatePixelShader(d3d, "PsDebugSourceUv");

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
    /// Renders one eye's view of <paramref name="source"/> into <paramref name="target"/>; black clears the target.
    /// Render thread only.
    /// </summary>
    /// <param name="context">The immediate context of the device.</param>
    /// <param name="target">Render target view; must be an *_SRGB view (gamma, docs/DECISIONS.md).</param>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <param name="view">Orientation and field of view.</param>
    /// <param name="eye">Which eye's half of a stereo source to show (the debug viewport shows the left eye).</param>
    /// <param name="source">Image, video frame or black.</param>
    /// <param name="brightness">1 = full image, 0 = black.</param>
    public void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in ViewParameters view,
        Eye eye,
        in PanoramaSource source,
        float brightness = 1f)
    {
        Render(context, target, width, height, view, eye, source, brightness, PanoramaOutput.Image);
    }

    /// <summary>Renders an equirect 360 mono still image, or black for null.</summary>
    public void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in ViewParameters view,
        StillImageTexture? source,
        float brightness = 1f)
    {
        Render(context, target, width, height, view, Eye.Left, PanoramaSource.FromImage(source, StereoLayout.Mono), brightness, PanoramaOutput.Image);
    }

    internal void Render(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in ViewParameters view,
        Eye eye,
        in PanoramaSource source,
        float brightness,
        PanoramaOutput output)
    {
        context.OMSetRenderTargets(target, null);

        if (output == PanoramaOutput.Image && (source.IsBlack || brightness <= 0f))
        {
            context.ClearRenderTargetView(target, Black);
            return;
        }

        EyeSourceRect rect = default;
        VideoColorConstants color = default;
        if (source.IsVideo)
        {
            VideoFrame frame = source.Video;
            rect = ProjectionMath.CreateEyeSourceRect(frame.Layout, eye, frame.Width, frame.Height);
            color = frame.Color;
            context.PSSetShaderResource(1, frame.Luma);
            context.PSSetShaderResource(2, frame.Chroma);
        }
        else if (source.Image is { } image)
        {
            rect = ProjectionMath.CreateEyeSourceRect(source.ImageLayout, eye, image.Width, image.Height);
            context.PSSetShaderResource(0, image.ShaderResourceView);
        }
        else
        {
            // Debug outputs without a source: the whole image, as for mono.
            rect = ProjectionMath.CreateEyeSourceRect(StereoLayout.Mono, eye, 2, 2);
        }

        WriteConstants(context, ViewConstants.Create(view, rect, color, brightness));

        context.RSSetViewport(0f, 0f, width, height, 0f, 1f);
        context.RSSetState(_rasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(output switch
        {
            PanoramaOutput.DebugDirection => _debugDirectionShader,
            PanoramaOutput.DebugEquirect360Uv => _debugUvShader,
            PanoramaOutput.DebugSourceUv => _debugSourceUvShader,
            _ when source.IsVideo => _videoShader,
            _ => _imageShader,
        });
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _sampler);
        context.Draw(3, 0);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _rasterizerState.Dispose();
        _sampler.Dispose();
        _debugSourceUvShader.Dispose();
        _debugUvShader.Dispose();
        _debugDirectionShader.Dispose();
        _videoShader.Dispose();
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
