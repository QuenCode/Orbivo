using System.Runtime.CompilerServices;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.Rendering;

namespace Orbivo.Xr.Tests;

public sealed class ShaderCompilerTests
{
    [Theory]
    [InlineData("VsFullscreenTriangle", ShaderCompiler.VertexShaderProfile)]
    [InlineData("PsEquirect360", ShaderCompiler.PixelShaderProfile)]
    [InlineData("PsDebugDirection", ShaderCompiler.PixelShaderProfile)]
    [InlineData("PsDebugEquirect360Uv", ShaderCompiler.PixelShaderProfile)]
    public void PanoramaShader_CompilesWithoutWarnings(string entryPoint, string profile)
    {
        byte[] bytecode = ShaderCompiler.Compile("panorama.hlsl", entryPoint, profile);

        Assert.NotEmpty(bytecode);
    }

    [Fact]
    public void Compile_UnknownEntryPoint_ReportsCompilerOutput()
    {
        var exception = Assert.Throws<ShaderCompilationException>(
            () => ShaderCompiler.Compile("panorama.hlsl", "DoesNotExist", ShaderCompiler.PixelShaderProfile));

        Assert.Contains("DoesNotExist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewConstants_MatchesTheConstantBufferSize()
    {
        Assert.Equal(ViewConstants.SizeInBytes, Unsafe.SizeOf<ViewConstants>());
        Assert.Equal(0, ViewConstants.SizeInBytes % 16);
    }
}
