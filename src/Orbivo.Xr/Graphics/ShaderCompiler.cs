using System.Reflection;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Orbivo.Xr.Graphics;

/// <summary>
/// Compiles the HLSL files that are embedded in this assembly (Shaders/*.hlsl) with d3dcompiler_47.dll,
/// which is part of Windows. Runs once at startup, never on the render thread's frame path.
/// </summary>
internal static class ShaderCompiler
{
    public const string VertexShaderProfile = "vs_5_0";
    public const string PixelShaderProfile = "ps_5_0";

    private const string ResourcePrefix = "Orbivo.Xr.Shaders.";

    private const ShaderFlags CompileFlags =
        ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness | ShaderFlags.WarningsAreErrors;

    /// <summary>Returns the bytecode of one entry point. Throws <see cref="ShaderCompilationException"/> with the compiler output.</summary>
    public static byte[] Compile(string fileName, string entryPoint, string profile)
    {
        string source = LoadSource(fileName);
        using var include = new EmbeddedShaderInclude();

        Blob? bytecode = null;
        Blob? errors = null;
        try
        {
            var result = Compiler.Compile(
                source,
                null!,
                include,
                entryPoint,
                fileName,
                profile,
                CompileFlags,
                out bytecode,
                out errors);

            if (result.Failure || bytecode is null)
            {
                string message = errors?.AsString()?.Trim() ?? $"HRESULT 0x{result.Code:X8}";
                throw new ShaderCompilationException($"Shader {fileName}:{entryPoint} ({profile}) failed to compile: {message}");
            }

            return bytecode.AsBytes();
        }
        finally
        {
            bytecode?.Dispose();
            errors?.Dispose();
        }
    }

    public static string LoadSource(string fileName)
    {
        using Stream stream = OpenResource(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream OpenResource(string fileName)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new ShaderCompilationException($"Embedded shader source '{fileName}' not found.");
    }

    /// <summary>
    /// Resolves <c>#include "x.hlsl"</c> against the embedded shader files.
    /// Derives from <see cref="CallbackBase"/> so that SharpGen creates the native callback for the compiler.
    /// </summary>
    private sealed class EmbeddedShaderInclude : CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream? parentStream) => OpenResource(fileName);

        public void Close(Stream stream) => stream.Dispose();
    }
}

public sealed class ShaderCompilationException(string message) : Exception(message);
