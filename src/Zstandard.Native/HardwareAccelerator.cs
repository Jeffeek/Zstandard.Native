using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#if NET10_0_OR_GREATER
using System.Runtime.Intrinsics.Arm;
#endif

namespace Zstandard.Native;

/// <summary>
/// CPU feature detection and vectorized buffer preprocessing primitives used by the
/// streaming codec to scrub pooled buffers and zero scratch regions in the hot path.
/// </summary>
/// <remarks>
/// <para>
/// On x86, <see cref="Vector512{T}"/> is lowered to AVX-512F on Skylake-X+ and to
/// AVX10.2 256/512 on supporting CPUs by the .NET 10 JIT — we don't have to choose at
/// the IL level. On ARM64, the SVE path uses variable-length predicated stores when
/// the runtime exposes the <c>Sve</c> intrinsics surface.
/// </para>
/// <para>
/// <b>Thread safety:</b> all members are static, idempotent, and free of shared
/// mutable state — safe to call concurrently from any number of threads.
/// </para>
/// </remarks>
public static class HardwareAccelerator
{
    /// <summary>
    /// <c>true</c> when the current process can dispatch to AVX-512 / AVX10 (x86)
    /// or SVE (ARM64) accelerated paths inside this library.
    /// </summary>
    public static bool IsHardwareAccelerated { get; } = DetectAcceleration();

    /// <summary>
    /// Reports the accelerated tier in use. Useful for diagnostics / benchmarking.
    /// </summary>
    public static AcceleratorKind ActiveAccelerator { get; } = DetectAcceleratorKind();

    /// <summary>
    /// Zero-fills <paramref name="buffer"/> using 64-byte (or wider, on SVE) vector
    /// stores when supported. Falls back to <see cref="Span{T}.Clear"/> for short
    /// buffers and unsupported targets.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearBuffer(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;

        if (Vector512.IsHardwareAccelerated && buffer.Length >= Vector512<byte>.Count)
        {
            ClearVector512(buffer);
            return;
        }

        if (Vector256.IsHardwareAccelerated && buffer.Length >= Vector256<byte>.Count)
        {
            ClearVector256(buffer);
            return;
        }

        buffer.Clear();
    }

    private static void ClearVector512(Span<byte> buffer)
    {
        ref var dst = ref MemoryMarshal.GetReference(buffer);
        var len = (nuint)buffer.Length;
        const int vlen = 64;
        nuint i = 0;
        var zero = Vector512<byte>.Zero;
        for (; i + vlen <= len; i += vlen)
            zero.StoreUnsafe(ref dst, i);
        if (i < len)
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref dst, i), (int)(len - i)).Clear();
    }

    private static void ClearVector256(Span<byte> buffer)
    {
        ref var dst = ref MemoryMarshal.GetReference(buffer);
        var len = (nuint)buffer.Length;
        const int vlen = 32;
        nuint i = 0;
        var zero = Vector256<byte>.Zero;
        for (; i + vlen <= len; i += vlen)
            zero.StoreUnsafe(ref dst, i);
        if (i < len)
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref dst, i), (int)(len - i)).Clear();
    }

    private static bool DetectAcceleration() => DetectAcceleratorKind() != AcceleratorKind.None;

    private static AcceleratorKind DetectAcceleratorKind()
    {
#if NET10_0_OR_GREATER
#pragma warning disable SYSLIB5003 // Sve is marked experimental in some SDKs.
#pragma warning disable IDE0046 // Convert if-then to return statement (we want to preserve the short-circuiting behavior here).
        // Resharper disable: ConvertToReturnStatement
        if (Sve.IsSupported)
            return AcceleratorKind.Sve;
#pragma warning restore IDE0046
#pragma warning restore SYSLIB5003
#endif
        return Vector512.IsHardwareAccelerated
            ?
            // On .NET 10 the JIT folds AVX-512F and AVX10 into the same Vector512 codegen.
            AcceleratorKind.Vector512
            : Vector256.IsHardwareAccelerated
                ? AcceleratorKind.Vector256
                : AcceleratorKind.None;
    }
}

public enum AcceleratorKind
{
    None = 0,
    Vector256 = 1,
    Vector512 = 2,
    Sve = 3
}
