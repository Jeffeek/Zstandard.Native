using System.Runtime.InteropServices;

namespace Zstandard.Native.Interop;

/// <summary>
/// Source-generated P/Invoke bindings for libzstd. AOT-safe, no marshalling.
/// </summary>
internal static partial class ZstdNative
{
    internal const string LibraryName = "libzstd";

    // --- one-shot ---

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_compress")]
    internal static unsafe partial nuint ZSTD_compress(
        void* dst,
        nuint dstCapacity,
        void* src,
        nuint srcSize,
        int compressionLevel
    );

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_decompress")]
    internal static unsafe partial nuint ZSTD_decompress(
        void* dst,
        nuint dstCapacity,
        void* src,
        nuint compressedSize
    );

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_getFrameContentSize")]
    internal static unsafe partial ulong ZSTD_getFrameContentSize(void* src, nuint srcSize);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_isError")]
    [return: MarshalAs(UnmanagedType.U4)]
    internal static partial uint ZSTD_isError(nuint code);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_compressBound")]
    internal static partial nuint ZSTD_compressBound(nuint srcSize);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_getErrorName")]
    internal static partial nint ZSTD_getErrorName(nuint code);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_versionNumber")]
    // ReSharper disable once UnusedMember.Global
    internal static partial uint ZSTD_versionNumber();

    // --- context lifecycle ---

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_createCCtx")]
    internal static partial nint ZSTD_createCCtx();

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_freeCCtx")]
    internal static partial nuint ZSTD_freeCCtx(nint cctx);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_createDCtx")]
    internal static partial nint ZSTD_createDCtx();

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_freeDCtx")]
    internal static partial nuint ZSTD_freeDCtx(nint dctx);

    // --- parameter / reset ---

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_CCtx_setParameter")]
    internal static partial nuint ZSTD_CCtx_setParameter(nint cctx, int param, int value);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_DCtx_setParameter")]
    internal static partial nuint ZSTD_DCtx_setParameter(nint dctx, int param, int value);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_CCtx_reset")]
    internal static partial nuint ZSTD_CCtx_reset(nint cctx, ZstdResetDirective reset);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_DCtx_reset")]
    internal static partial nuint ZSTD_DCtx_reset(nint dctx, ZstdResetDirective reset);

    // --- streaming ---

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_compressStream2")]
    internal static unsafe partial nuint ZSTD_compressStream2(
        // ReSharper disable once IdentifierTypo
        nint cctx,
        ZSTD_outBuffer* output,
        ZSTD_inBuffer* input,
        int endOp
    );

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_decompressStream")]
    internal static unsafe partial nuint ZSTD_decompressStream(
        // ReSharper disable once IdentifierTypo
        nint dctx,
        ZSTD_outBuffer* output,
        ZSTD_inBuffer* input
    );

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_CStreamInSize")]
    internal static partial nuint ZSTD_CStreamInSize();

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_CStreamOutSize")]
    internal static partial nuint ZSTD_CStreamOutSize();

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_DStreamInSize")]
    internal static partial nuint ZSTD_DStreamInSize();

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_DStreamOutSize")]
    internal static partial nuint ZSTD_DStreamOutSize();

    // --- dictionary training (ZDICT_*) ---

    [LibraryImport(LibraryName, EntryPoint = "ZDICT_trainFromBuffer")]
    internal static unsafe partial nuint ZDICT_trainFromBuffer(
        void* dictBuffer,
        nuint dictBufferCapacity,
        void* samplesBuffer,
        nuint* samplesSizes,
        uint nbSamples
    );

    [LibraryImport(LibraryName, EntryPoint = "ZDICT_isError")]
    internal static partial uint ZDICT_isError(nuint code);

    [LibraryImport(LibraryName, EntryPoint = "ZDICT_getErrorName")]
    internal static partial nint ZDICT_getErrorName(nuint code);

    // ReSharper disable InconsistentNaming
    internal const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked((ulong)-1);
    internal const ulong ZSTD_CONTENTSIZE_ERROR = unchecked((ulong)-2);

    // ZSTD_cParameter / ZSTD_dParameter ids (libzstd public API constants).
    internal const int ZSTD_c_compressionLevel = 100;
    internal const int ZSTD_c_checksumFlag = 201;
    internal const int ZSTD_c_nbWorkers = 400;

    internal const int ZSTD_d_windowLogMax = 100;

    // ReSharper restore InconsistentNaming
}

// ReSharper disable once InconsistentNaming
internal enum ZstdResetDirective
{
    SessionOnly = 1,
    // ReSharper disable once UnusedMember.Global
    Parameters = 2,
    SessionAndParameters = 3
}

[StructLayout(LayoutKind.Sequential)]
// ReSharper disable once InconsistentNaming
internal unsafe struct ZSTD_inBuffer
{
    public void* src;
    public nuint size;
    public nuint pos;
}

[StructLayout(LayoutKind.Sequential)]
// ReSharper disable once InconsistentNaming
internal unsafe struct ZSTD_outBuffer
{
    public void* dst;
    public nuint size;
    public nuint pos;
}
