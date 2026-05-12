using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native;

/// <summary>
/// One-shot, zero-allocation Zstd compressor over <see cref="Span{T}"/>.
/// </summary>
public static class ZstdCompressor
{
    private const int MinCompressionLevel = 1;
    private const int MaxCompressionLevel = 22;
    private const int DefaultCompressionLevel = 3;

    /// <summary>
    /// Returns the worst-case compressed size for an input of <paramref name="srcSize"/> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCompressBound(int srcSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(srcSize);
        var bound = ZstdNative.ZSTD_compressBound((nuint)srcSize);
        if (bound > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(srcSize), "Compressed bound exceeds Int32.MaxValue.");
        }
        return (int)bound;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/> in a single pass.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public static int Compress(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int compressionLevel = DefaultCompressionLevel)
    {
        if ((uint)(compressionLevel - MinCompressionLevel) > MaxCompressionLevel - MinCompressionLevel)
        {
            ThrowLevelOutOfRange(compressionLevel);
        }

        unsafe
        {
            fixed (byte* srcPtr = &MemoryMarshal.GetReference(source))
            fixed (byte* dstPtr = &MemoryMarshal.GetReference(destination))
            {
                var written = ZstdNative.ZSTD_compress(
                    dstPtr, (nuint)destination.Length,
                    srcPtr, (nuint)source.Length,
                    compressionLevel);

                ZstdException.ThrowIfError(written);
                return checked((int)written);
            }
        }
    }

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/> in a single pass.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        unsafe
        {
            fixed (byte* srcPtr = &MemoryMarshal.GetReference(source))
            fixed (byte* dstPtr = &MemoryMarshal.GetReference(destination))
            {
                var written = ZstdNative.ZSTD_decompress(
                    dstPtr, (nuint)destination.Length,
                    srcPtr, (nuint)source.Length);

                ZstdException.ThrowIfError(written);
                return checked((int)written);
            }
        }
    }

    /// <summary>
    /// Reads the original (uncompressed) size embedded in a Zstd frame header,
    /// when present. Returns <c>null</c> when the size is unknown.
    /// </summary>
    public static long? GetFrameContentSize(ReadOnlySpan<byte> compressed)
    {
        unsafe
        {
            fixed (byte* p = &MemoryMarshal.GetReference(compressed))
            {
                var size = ZstdNative.ZSTD_getFrameContentSize(p, (nuint)compressed.Length);
                return size switch
                {
                    ZstdNative.ZSTD_CONTENTSIZE_ERROR => throw new ZstdException("Invalid Zstd frame header.", unchecked((nuint)(-2))),
                    ZstdNative.ZSTD_CONTENTSIZE_UNKNOWN => null,
                    _ => checked((long)size)
                };
            }
        }
    }

    private static void ThrowLevelOutOfRange(int level) =>
        throw new ArgumentOutOfRangeException(
            nameof(level),
            level,
            $"Compression level must be in [{MinCompressionLevel}, {MaxCompressionLevel}].");
}
