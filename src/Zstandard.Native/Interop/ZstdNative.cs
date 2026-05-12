using System.Runtime.InteropServices;

namespace Zstandard.Native.Interop;

/// <summary>
/// Source-generated P/Invoke bindings for libzstd. AOT-safe, no marshalling.
/// </summary>
internal static partial class ZstdNative
{
    internal const string LibraryName = "libzstd";

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_compress")]
    internal static unsafe partial nuint ZSTD_compress(
        void* dst, nuint dstCapacity,
        void* src, nuint srcSize,
        int compressionLevel);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_decompress")]
    internal static unsafe partial nuint ZSTD_decompress(
        void* dst, nuint dstCapacity,
        void* src, nuint compressedSize);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_getFrameContentSize")]
    internal static unsafe partial ulong ZSTD_getFrameContentSize(
        void* src, nuint srcSize);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_isError")]
    [return: MarshalAs(UnmanagedType.U4)]
    internal static partial uint ZSTD_isError(nuint code);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_compressBound")]
    internal static partial nuint ZSTD_compressBound(nuint srcSize);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_getErrorName")]
    internal static partial nint ZSTD_getErrorName(nuint code);

    [LibraryImport(LibraryName, EntryPoint = "ZSTD_versionNumber")]
    internal static partial uint ZSTD_versionNumber();

    // ReSharper disable InconsistentNaming
    internal const ulong ZSTD_CONTENTSIZE_UNKNOWN = unchecked((ulong)-1);
    internal const ulong ZSTD_CONTENTSIZE_ERROR = unchecked((ulong)-2);
    // ReSharper restore InconsistentNaming
}
