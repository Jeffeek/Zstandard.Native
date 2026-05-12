using System.Runtime.InteropServices;
using Zstandard.Native.Interop;

namespace Zstandard.Native;

/// <summary>
/// Trains a Zstandard dictionary from a corpus of representative samples.
/// </summary>
/// <remarks>
/// <para>
/// A trained dictionary, fed into <c>ZSTD_CCtx_loadDictionary</c> on the compressor
/// side and <c>ZSTD_DCtx_loadDictionary</c> on the decompressor side, dramatically
/// improves compression ratio for small homogeneous payloads (JSON rows, protobuf
/// messages, log lines) by precomputing literals and offsets that would otherwise
/// have to be rediscovered inside every frame.
/// </para>
/// <para>
/// <b>Thread safety:</b> the API is a pure function — concurrent invocations from
/// many threads are safe.
/// </para>
/// <para>
/// <b>Sizing:</b> the Zstandard maintainers recommend a destination buffer of at
/// least 100 KiB, and a corpus totaling roughly 100× the desired dictionary size.
/// </para>
/// </remarks>
public static class ZstdDictionaryTrainer
{
    /// <summary>
    /// Recommended destination buffer size for <see cref="Train"/>. Matches the
    /// libzstd manual's "typical dictionary size" guidance (~110 KiB).
    /// </summary>
    public const int RecommendedDictionarySize = 112_640;

    /// <summary>
    /// The Zstandard dictionary magic, in little-endian byte order
    /// (<c>0xEC30A437</c>). Always the first four bytes of a valid trained dictionary.
    /// </summary>
    public static ReadOnlySpan<byte> DictionaryMagic => [0x37, 0xA4, 0x30, 0xEC];

    /// <summary>
    /// Train a Zstandard dictionary from a flat sample buffer.
    /// </summary>
    /// <param name="samples">
    /// All samples concatenated end-to-end. Sample boundaries are described by
    /// <paramref name="sampleSizes"/>.
    /// </param>
    /// <param name="sampleSizes">
    /// One <see cref="UIntPtr"/> per sample, in order, giving its length in bytes.
    /// The sum must equal <paramref name="samples"/>.Length.
    /// </param>
    /// <param name="dictionary">
    /// Destination buffer for the trained dictionary. Recommended capacity is
    /// <see cref="RecommendedDictionarySize"/>.
    /// </param>
    /// <returns>Number of bytes written into <paramref name="dictionary"/>.</returns>
    /// <exception cref="ZstdException">
    /// Thrown when libzstd reports a training failure (e.g. samples too small,
    /// destination capacity too small, corpus too uniform).
    /// </exception>
    public static int Train(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<nuint> sampleSizes,
        Span<byte> dictionary)
    {
        if (sampleSizes.IsEmpty)
        {
            throw new ArgumentException("At least one sample is required.", nameof(sampleSizes));
        }

        unsafe
        {
            fixed (byte* samplesPtr = &MemoryMarshal.GetReference(samples))
            fixed (nuint* sizesPtr = &MemoryMarshal.GetReference(sampleSizes))
            fixed (byte* dictPtr = &MemoryMarshal.GetReference(dictionary))
            {
                var written = ZstdNative.ZDICT_trainFromBuffer(
                    dictPtr, (nuint)dictionary.Length,
                    samplesPtr, sizesPtr, (uint)sampleSizes.Length);

                if (ZstdNative.ZDICT_isError(written) != 0)
                {
                    ThrowZdictError(written);
                }

                return checked((int)written);
            }
        }
    }

    private static void ThrowZdictError(nuint code)
    {
        var ptr = ZstdNative.ZDICT_getErrorName(code);
        var name = ptr == nint.Zero ? "unknown ZDICT error" : Marshal.PtrToStringUTF8(ptr) ?? "unknown ZDICT error";
        throw new ZstdException($"ZDICT error: {name}", code);
    }
}
