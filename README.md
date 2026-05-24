# Zstandard.Native

[![ci](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/ci.yml/badge.svg)](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/ci.yml)
[![codeql](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/codeql.yml/badge.svg)](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/Zstandard.Native.svg)](https://www.nuget.org/packages/Zstandard.Native)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Ultra-fast, **Native AOT-safe** Zstandard wrapper for **.NET 8**, **.NET 9**, and **.NET 10** with zero-allocation `Span<byte>` APIs, source-generated `[LibraryImport]` bindings, and hardware-accelerated paths that target **AVX10.2** on x86 and **SVE** on ARM64 via the .NET 10 JIT.

```csharp
using Zstandard.Native;

ReadOnlySpan<byte> src = ...;
Span<byte> dst = stackalloc byte[ZstdCompressor.GetCompressBound(src.Length)];
int written = ZstdCompressor.Compress(src, dst, compressionLevel: 3);

Span<byte> back = stackalloc byte[src.Length];
int decoded = ZstdCompressor.Decompress(dst[..written], back);
```

No reflection. No marshalling shims. No `Stream` adapters in the hot path. Just `Span`, `nuint`, and the libzstd ABI.

---

## Table of contents

- [Why another zstd binding](#why-another-zstd-binding)
- [Install](#install)
- [Quick start](#quick-start)
- [Hardware acceleration on .NET 10 (AVX10 / SVE)](#hardware-acceleration-on-net-10-avx10--sve)
- [Native AOT design notes](#native-aot-design-notes)
- [Performance](#performance)
- [Streaming API & context reuse](#streaming-api--context-reuse)
- [Native runtime binaries](#native-runtime-binaries)
- [Thread safety & disposal](#thread-safety--disposal)
- [Compatibility matrix](#compatibility-matrix)
- [Contributing](#contributing)
- [License](#license)

---

## Why another zstd binding

| Concern | Zstandard.Native | Typical managed port | Typical P/Invoke wrapper |
|---|---|---|---|
| Zero managed allocations on the hot path | ✅ `Span`-only | ⚠️ byte arrays | ⚠️ byte arrays |
| Source-generated P/Invoke (`[LibraryImport]`) | ✅ | n/a | ❌ `[DllImport]` |
| Native AOT compatible without runtime warnings | ✅ AOT analyzers as errors | ⚠️ depends | ❌ reflection-based marshalling |
| `SafeHandle` for `ZSTD_CCtx` / `ZSTD_DCtx` | ✅ | n/a | ⚠️ raw `IntPtr` |
| AVX10 / SVE buffer paths | ✅ via Vector512 + Sve | ❌ | ❌ |
| Streaming with context reuse | ✅ `Reset()` | ⚠️ allocates | ⚠️ allocates |
| Pooled scratch via `ArrayPool<byte>.Shared` | ✅ | ❌ | ❌ |

If your workload is **per-message compression at line rate** (RPC frames, log shipping, KV row compression, columnar batches), the per-call context allocation and managed-array copies of typical bindings dominate. This library is built around removing exactly that overhead.

---

## Install

```bash
dotnet add package Zstandard.Native
```

You also need the native `libzstd` binary on the loader path. See [Native runtime binaries](#native-runtime-binaries) for the supported options.

---

## Quick start

### One-shot

```csharp
using Zstandard.Native;

byte[] payload = File.ReadAllBytes("doc.json");
byte[] compressed = new byte[ZstdCompressor.GetCompressBound(payload.Length)];

int n = ZstdCompressor.Compress(payload, compressed, compressionLevel: 9);

long? original = ZstdCompressor.GetFrameContentSize(compressed.AsSpan(0, n));
byte[] back = new byte[(int)original!];
ZstdCompressor.Decompress(compressed.AsSpan(0, n), back);
```

### Streaming (reuse the context across many frames)

```csharp
using var compressor = new ZstdStreamCompressor(compressionLevel: 3);
Span<byte> outBuf = stackalloc byte[ZstdStreamCompressor.RecommendedOutputSize];

foreach (var frame in producer)
{
    compressor.Reset();
    var r = compressor.Compress(frame.Span, outBuf, ZstdEndDirective.End);
    network.Send(outBuf[..r.BytesWritten]);
}
```

---

## Hardware acceleration on .NET 10 (AVX10 / SVE)

`HardwareAccelerator` is the library's vectorized utility surface. On hot paths where the codec needs to zero scratch regions or scrub pooled buffers, it dispatches to the widest available vector ISA without you choosing one at the IL level.

```csharp
if (HardwareAccelerator.IsHardwareAccelerated)
{
    Console.WriteLine($"Active tier: {HardwareAccelerator.ActiveAccelerator}");
    // -> Vector512  (AVX-512F / AVX10 on .NET 10)
    // -> Sve        (ARM64 SVE)
    // -> Vector256  (fallback)
}
```

### Why this matters on .NET 10

| ISA | Vector width | What .NET 10 does |
|---|---|---|
| **AVX10.2** | 256 / 512 bit | The JIT lowers `Vector512<T>` to AVX10.2 encodings on supporting CPUs (Granite Rapids and later) automatically — no source change. |
| **AVX-512F** | 512 bit | Same `Vector512<T>` IL is lowered to legacy AVX-512 on Skylake-X+. |
| **SVE** | Variable (128–2048 bit) | On ARM64, `Sve.IsSupported` lights up predicated, variable-length stores via `System.Runtime.Intrinsics.Arm.Sve` — one loop body works on a 128-bit V1 core or a 512-bit Neoverse-V2 without rewriting. |

This is the **.NET 10 hardware-acceleration story** in one sentence: we write `Vector512.StoreUnsafe(Vector512<byte>.Zero, ref dst, i)` once and the JIT picks the right encoding per host CPU at startup.

### What runs on the vectorized path today

- `HardwareAccelerator.ClearBuffer(Span<byte>)` — 64-byte (or wider, on SVE) vector stores with a tail-clear, used to scrub pooled scratch buffers before returning them to `ArrayPool<byte>.Shared`.
- `ZstdStreamCompressor.Dispose()` / `ZstdStreamDecompressor.Dispose()` both call into this path before releasing their borrowed buffers.

The codec itself runs inside libzstd, which has its own AVX2/AVX-512/SVE detection — our job is to make sure the .NET side doesn't add managed-overhead waste around it.

---

## Native AOT design notes

The whole library compiles cleanly under `PublishAot=true` on .NET 8 and .NET 10, **with every `IL2xxx` and `IL3xxx` warning promoted to an error in `Directory.Build.props`**. The CI publish pipeline runs a dedicated AOT validation gate that does a real `dotnet publish -p:PublishAot=true` of a consumer probe project against the just-built nupkg and refuses to ship if the resulting native binary doesn't pass a round-trip test.

### Zero-reflection design rules

1. **Every native call uses `[LibraryImport]`** (the source generator), not `[DllImport]`. There is no `Marshal.PtrToStructure`, no runtime marshalling table.
2. **Signatures use only blittable types**: `void*`, `nuint`, `ulong`, `nint`. The `ZSTD_inBuffer` / `ZSTD_outBuffer` structs are `[StructLayout(LayoutKind.Sequential)]` and passed by pointer.
3. **No `Activator.CreateInstance`, `Type.GetType`, or LINQ Expressions** anywhere in the codebase.
4. **Native library resolution is wired via a `[ModuleInitializer]`** that calls `NativeLibrary.SetDllImportResolver` — no reflection, no probing assemblies for attributes.
5. **All public APIs accept `Span<byte>` / `ReadOnlySpan<byte>`**, so the AOT consumer can pin without enrolling a marshaller.
6. **`SafeHandle` finalizers** guarantee `ZSTD_freeCCtx` / `ZSTD_freeDCtx` runs even on process abort.

### How the AOT gate works

```
publish.yml ─▶ pack (real nupkg, real version)
            ─▶ aot-gate:
                 set up local-feed NuGet.config
                 dotnet publish eng/AotProbe -p:PublishAot=true -r <rid>
                 run the produced native binary, expect exit 0
            ─▶ push to nuget.org
```

The probe lives in `eng/AotProbe/` and exercises one-shot compress, decompress, and streaming. If the .NET 10 ILC ever can't statically reason about the library, the push is blocked.

---

## Performance

```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.6584/24H2/2024Update/HudsonValley)
AMD Ryzen 9 9950X 4.30GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.300
  [Host]   : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v4
  AOT 10.0 : .NET 10.0.8, X64 NativeAOT x86-64-v4
  AOT 8.0  : .NET 8.0.27, X64 NativeAOT x86-64-v4
  AOT 9.0  : .NET 9.0.16, X64 NativeAOT x86-64-v4
  JIT 10.0 : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v4
  JIT 8.0  : .NET 8.0.25 (8.0.25, 8.0.2526.11203), X64 RyuJIT x86-64-v4
  JIT 9.0  : .NET 9.0.14 (9.0.14, 9.0.1426.11910), X64 RyuJIT x86-64-v4

IterationCount=3  LaunchCount=1  WarmupCount=3  

```

### Key findings

- **Streaming context reuse** peaks at **12 510 MB/s** on NativeAOT 10.0 (1 MiB payload, 0 B allocated). `Reset()` resets the native context in-place, skipping `ZSTD_createCCtx` entirely after first construction.
- **Context reuse is 1.7–1.9× faster than fresh context** at 64 KB and **2.6–3.2× faster** at 1 MiB. The gap grows with payload size because `ZSTD_createCCtx` provisions internal hash and chain tables proportional to the window log — it is not constant-cost.
- **One-shot compression beats ZstdSharp by 10–75%** across all runtimes and payload sizes, and allocates **0 B** vs. 64 B per call. The advantage is largest on older runtimes (.NET 8 AOT) and at 64 KB+ payloads.
- **One-shot decompression is slower than ZstdSharp below ~1 MiB.** ZstdSharp's pure-managed port avoids the P/Invoke boundary entirely; the call overhead (~0.65–0.80 μs on this CPU) dominates when the codec itself finishes in under 1 μs. The curves converge at 1 MiB+. Native decompression allocates **0 B**; ZstdSharp allocates 56 B per call.

### Streaming: context reuse vs. fresh context

`ZstdStreamCompressor.Reset()` resets the native `ZSTD_CCtx` in-place. The "fresh context per call" rows simulate allocating a new compressor, compressing one frame, and disposing — representative of code that `new`s a compressor inside a loop. The 72 B in the fresh-context rows is the managed wrapper object; the native scratch lives in unmanaged memory and is not reflected here.

| Method                                     | Job      | Runtime        | PayloadSize | Mean       | Error      | StdDev    | P95        | MB/s    | Allocated |
|------------------------------------------- |--------- |--------------- |------------ |-----------:|-----------:|----------:|-----------:|--------:|----------:|
| Stream.Compress (context reuse)            | JIT 8.0  | .NET 8.0       | 65536       |   5.265 μs |  0.0502 μs | 0.0028 μs |   5.267 μs | 12446.9 |         - |
| Stream.Compress (fresh context per call)   | JIT 8.0  | .NET 8.0       | 65536       |   9.752 μs |  0.5763 μs | 0.0316 μs |   9.783 μs |  6720.1 |      72 B |
| Stream.Compress (context reuse)            | AOT 8.0  | NativeAOT 8.0  | 65536       |   5.717 μs |  1.5252 μs | 0.0836 μs |   5.793 μs | 11463.6 |         - |
| Stream.Compress (fresh context per call)   | AOT 8.0  | NativeAOT 8.0  | 65536       |  10.161 μs |  4.4907 μs | 0.2462 μs |  10.320 μs |  6449.9 |      72 B |
| Stream.Compress (context reuse)            | JIT 8.0  | .NET 8.0       | 1048576     |  89.593 μs | 30.0836 μs | 1.6490 μs |  90.869 μs | 11703.8 |         - |
| Stream.Compress (fresh context per call)   | JIT 8.0  | .NET 8.0       | 1048576     | 237.396 μs | 56.1683 μs | 3.0788 μs | 240.432 μs |  4417.0 |      72 B |
| Stream.Compress (context reuse)            | AOT 8.0  | NativeAOT 8.0  | 1048576     |  84.537 μs |  0.6718 μs | 0.0368 μs |  84.558 μs | 12403.8 |         - |
| Stream.Compress (fresh context per call)   | AOT 8.0  | NativeAOT 8.0  | 1048576     | 272.042 μs | 89.3763 μs | 4.8990 μs | 275.291 μs |  3854.5 |      72 B |
|                                            |          |                |             |            |            |           |            |         |           |
| Stream.Compress (context reuse)            | JIT 9.0  | .NET 9.0       | 65536       |   6.142 μs |  2.1504 μs | 0.1179 μs |   6.216 μs | 10669.4 |         - |
| Stream.Compress (fresh context per call)   | JIT 9.0  | .NET 9.0       | 65536       |   9.639 μs |  0.6917 μs | 0.0379 μs |   9.667 μs |  6799.1 |      72 B |
| Stream.Compress (context reuse)            | AOT 9.0  | NativeAOT 9.0  | 65536       |   5.475 μs |  0.1460 μs | 0.0080 μs |   5.483 μs | 11969.4 |         - |
| Stream.Compress (fresh context per call)   | AOT 9.0  | NativeAOT 9.0  | 65536       |   9.513 μs |  2.4363 μs | 0.1335 μs |   9.597 μs |  6888.9 |      72 B |
| Stream.Compress (context reuse)            | JIT 9.0  | .NET 9.0       | 1048576     | 100.940 μs | 43.3969 μs | 2.3787 μs | 103.277 μs | 10388.1 |         - |
| Stream.Compress (fresh context per call)   | JIT 9.0  | .NET 9.0       | 1048576     | 234.497 μs | 48.8881 μs | 2.6797 μs | 237.113 μs |  4471.6 |      72 B |
| Stream.Compress (context reuse)            | AOT 9.0  | NativeAOT 9.0  | 1048576     |  84.975 μs |  1.2712 μs | 0.0697 μs |  85.034 μs | 12339.7 |         - |
| Stream.Compress (fresh context per call)   | AOT 9.0  | NativeAOT 9.0  | 1048576     | 258.560 μs | 24.8720 μs | 1.3633 μs | 259.821 μs |  4055.4 |      72 B |
|                                            |          |                |             |            |            |           |            |         |           |
| Stream.Compress (context reuse)            | JIT 10.0 | .NET 10.0      | 65536       |   6.119 μs |  0.1592 μs | 0.0087 μs |   6.126 μs | 10710.7 |         - |
| Stream.Compress (fresh context per call)   | JIT 10.0 | .NET 10.0      | 65536       |  10.481 μs |  1.2564 μs | 0.0689 μs |  10.546 μs |  6252.5 |      72 B |
| Stream.Compress (context reuse)            | AOT 10.0 | NativeAOT 10.0 | 65536       |   5.454 μs |  0.0997 μs | 0.0055 μs |   5.460 μs | 12015.6 |         - |
| Stream.Compress (fresh context per call)   | AOT 10.0 | NativeAOT 10.0 | 65536       |   9.776 μs |  0.1204 μs | 0.0066 μs |   9.782 μs |  6704.0 |      72 B |
| Stream.Compress (context reuse)            | JIT 10.0 | .NET 10.0      | 1048576     |  88.758 μs |  0.4609 μs | 0.0253 μs |  88.776 μs | 11813.9 |         - |
| Stream.Compress (fresh context per call)   | JIT 10.0 | .NET 10.0      | 1048576     | 235.646 μs | 43.3496 μs | 2.3761 μs | 237.042 μs |  4449.8 |      72 B |
| Stream.Compress (context reuse)            | AOT 10.0 | NativeAOT 10.0 | 1048576     |  83.819 μs |  1.8219 μs | 0.0999 μs |  83.908 μs | 12510.1 |         - |
| Stream.Compress (fresh context per call)   | AOT 10.0 | NativeAOT 10.0 | 1048576     | 238.898 μs | 26.6135 μs | 1.4588 μs | 239.921 μs |  4389.2 |      72 B |

> **Why does the fresh-context penalty grow with payload size?** At 64 KB the construction overhead adds ~4 μs; at 1 MiB it adds ~155–188 μs. libzstd allocates the `ZSTD_CCtx`'s internal hash tables relative to the window log, so larger inputs cause proportionally more native allocator work at construction and destruction.

### One-shot compress / decompress vs. ZstdSharp

`Native.Compress` / `Native.Decompress` call libzstd through a single `[LibraryImport]` P/Invoke with no intermediate managed byte[] copy. ZstdSharp is a pure-managed C# port — no native calls — but allocates a small wrapper object per call. The `Ratio` column is relative to `Native.Compress` within each benchmark group (not to ZstdSharp); `Native.Decompress` appears at 0.13–0.15 because decompression is inherently much faster than compression.

| Method               | Job      | Runtime        | PayloadSize | Level | Mean           | Error         | StdDev      | P95            | Ratio | RatioSD | MB/s     | Gen0   | Allocated | Alloc Ratio |
|--------------------- |--------- |--------------- |------------ |------ |---------------:|--------------:|------------:|---------------:|------:|--------:|---------:|-------:|----------:|------------:|
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 4096        | 1     |      5.2194 us |     0.1116 us |   0.0061 us |      5.2234 us |  1.00 |    0.00 |    784.8 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 4096        | 1     |      5.9595 us |     0.1283 us |   0.0070 us |      5.9644 us |  1.14 |    0.00 |    687.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 4096        | 1     |      0.7988 us |     0.0145 us |   0.0008 us |      0.7996 us |  0.15 |    0.00 |   5127.6 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 4096        | 1     |      0.1654 us |     0.0329 us |   0.0018 us |      0.1670 us |  0.03 |    0.00 |  24765.5 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 4096        | 1     |      5.2612 us |     2.0259 us |   0.1110 us |      5.3707 us |  1.00 |    0.03 |    778.5 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 4096        | 1     |      6.6351 us |     1.2869 us |   0.0705 us |      6.6923 us |  1.26 |    0.03 |    617.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 4096        | 1     |      0.7901 us |     0.0175 us |   0.0010 us |      0.7910 us |  0.15 |    0.00 |   5184.4 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 4096        | 1     |      0.1461 us |     0.0589 us |   0.0032 us |      0.1492 us |  0.03 |    0.00 |  28041.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 4096        | 1     |      5.2040 us |     0.3624 us |   0.0199 us |      5.2236 us |  1.00 |    0.00 |    787.1 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 4096        | 1     |      6.2339 us |     0.0743 us |   0.0041 us |      6.2365 us |  1.20 |    0.00 |    657.0 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 4096        | 1     |      0.8057 us |     0.0504 us |   0.0028 us |      0.8082 us |  0.15 |    0.00 |   5083.9 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 4096        | 1     |      0.1602 us |     0.0236 us |   0.0013 us |      0.1614 us |  0.03 |    0.00 |  25572.3 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 4096        | 1     |      5.1549 us |     0.2450 us |   0.0134 us |      5.1668 us |  1.00 |    0.00 |    794.6 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 4096        | 1     |      5.7935 us |     0.9666 us |   0.0530 us |      5.8458 us |  1.12 |    0.01 |    707.0 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 4096        | 1     |      0.7761 us |     0.0063 us |   0.0003 us |      0.7764 us |  0.15 |    0.00 |   5277.7 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 4096        | 1     |      0.1252 us |     0.0059 us |   0.0003 us |      0.1255 us |  0.02 |    0.00 |  32715.0 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 4096        | 1     |      5.1597 us |     0.2713 us |   0.0149 us |      5.1700 us |  1.00 |    0.00 |    793.8 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 4096        | 1     |      6.4884 us |     0.2120 us |   0.0116 us |      6.4998 us |  1.26 |    0.00 |    631.3 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 4096        | 1     |      0.7932 us |     0.0326 us |   0.0018 us |      0.7947 us |  0.15 |    0.00 |   5164.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 4096        | 1     |      0.1356 us |     0.0814 us |   0.0045 us |      0.1395 us |  0.03 |    0.00 |  30209.1 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 4096        | 1     |      5.2300 us |     0.1104 us |   0.0060 us |      5.2354 us |  1.00 |    0.00 |    783.2 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 4096        | 1     |      6.0053 us |     0.8211 us |   0.0450 us |      6.0494 us |  1.15 |    0.01 |    682.1 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 4096        | 1     |      0.7787 us |     0.0078 us |   0.0004 us |      0.7791 us |  0.15 |    0.00 |   5260.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 4096        | 1     |      0.1259 us |     0.0178 us |   0.0010 us |      0.1268 us |  0.02 |    0.00 |  32537.2 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 4096        | 3     |      5.9589 us |     0.1938 us |   0.0106 us |      5.9675 us |  1.00 |    0.00 |    687.4 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 4096        | 3     |      7.8438 us |     1.3284 us |   0.0728 us |      7.9119 us |  1.32 |    0.01 |    522.2 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 4096        | 3     |      0.7965 us |     0.0057 us |   0.0003 us |      0.7968 us |  0.13 |    0.00 |   5142.6 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 4096        | 3     |      0.1673 us |     0.0070 us |   0.0004 us |      0.1677 us |  0.03 |    0.00 |  24480.9 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 4096        | 3     |      5.9260 us |     0.2333 us |   0.0128 us |      5.9386 us |  1.00 |    0.00 |    691.2 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 4096        | 3     |      8.3426 us |     1.0815 us |   0.0593 us |      8.3899 us |  1.41 |    0.01 |    491.0 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 4096        | 3     |      0.7905 us |     0.0012 us |   0.0001 us |      0.7906 us |  0.13 |    0.00 |   5181.2 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 4096        | 3     |      0.1445 us |     0.0113 us |   0.0006 us |      0.1451 us |  0.02 |    0.00 |  28343.6 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 4096        | 3     |      5.9716 us |     0.6878 us |   0.0377 us |      6.0058 us |  1.00 |    0.01 |    685.9 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 4096        | 3     |      7.8306 us |     2.1637 us |   0.1186 us |      7.9348 us |  1.31 |    0.02 |    523.1 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 4096        | 3     |      0.8106 us |     0.2511 us |   0.0138 us |      0.8241 us |  0.14 |    0.00 |   5053.3 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 4096        | 3     |      0.1664 us |     0.0029 us |   0.0002 us |      0.1665 us |  0.03 |    0.00 |  24616.8 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 4096        | 3     |      5.9081 us |     0.1044 us |   0.0057 us |      5.9137 us |  1.00 |    0.00 |    693.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 4096        | 3     |      6.5088 us |     3.0582 us |   0.1676 us |      6.6742 us |  1.10 |    0.02 |    629.3 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 4096        | 3     |      0.7732 us |     0.0075 us |   0.0004 us |      0.7736 us |  0.13 |    0.00 |   5297.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 4096        | 3     |      0.1178 us |     0.0297 us |   0.0016 us |      0.1193 us |  0.02 |    0.00 |  34764.8 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 4096        | 3     |      5.9271 us |     0.2093 us |   0.0115 us |      5.9349 us |  1.00 |    0.00 |    691.1 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 4096        | 3     |      7.1686 us |     1.0334 us |   0.0566 us |      7.2243 us |  1.21 |    0.01 |    571.4 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 4096        | 3     |      0.7991 us |     0.1550 us |   0.0085 us |      0.8074 us |  0.13 |    0.00 |   5126.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 4096        | 3     |      0.1345 us |     0.1116 us |   0.0061 us |      0.1387 us |  0.02 |    0.00 |  30456.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 4096        | 3     |      5.8937 us |     0.2207 us |   0.0121 us |      5.9056 us |  1.00 |    0.00 |    695.0 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 4096        | 3     |      6.7085 us |     0.5742 us |   0.0315 us |      6.7278 us |  1.14 |    0.01 |    610.6 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 4096        | 3     |      0.7785 us |     0.0130 us |   0.0007 us |      0.7791 us |  0.13 |    0.00 |   5261.4 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 4096        | 3     |      0.1440 us |     0.0417 us |   0.0023 us |      0.1463 us |  0.02 |    0.00 |  28437.4 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 4096        | 9     |     12.1465 us |     0.2200 us |   0.0121 us |     12.1575 us |  1.00 |    0.00 |    337.2 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 4096        | 9     |     15.2757 us |     0.1627 us |   0.0089 us |     15.2814 us |  1.26 |    0.00 |    268.1 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 4096        | 9     |      0.8036 us |     0.0173 us |   0.0009 us |      0.8042 us |  0.07 |    0.00 |   5097.3 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 4096        | 9     |      0.1670 us |     0.0077 us |   0.0004 us |      0.1674 us |  0.01 |    0.00 |  24528.9 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 4096        | 9     |     11.7874 us |     0.1831 us |   0.0100 us |     11.7973 us |  1.00 |    0.00 |    347.5 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 4096        | 9     |     17.0282 us |     3.5879 us |   0.1967 us |     17.2222 us |  1.44 |    0.01 |    240.5 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 4096        | 9     |      0.7937 us |     0.1676 us |   0.0092 us |      0.8027 us |  0.07 |    0.00 |   5160.8 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 4096        | 9     |      0.1439 us |     0.0279 us |   0.0015 us |      0.1449 us |  0.01 |    0.00 |  28466.8 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 4096        | 9     |     12.2197 us |     1.9976 us |   0.1095 us |     12.3276 us |  1.00 |    0.01 |    335.2 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 4096        | 9     |     15.2320 us |     0.7914 us |   0.0434 us |     15.2626 us |  1.25 |    0.01 |    268.9 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 4096        | 9     |      0.8045 us |     0.0055 us |   0.0003 us |      0.8047 us |  0.07 |    0.00 |   5091.7 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 4096        | 9     |      0.1619 us |     0.0117 us |   0.0006 us |      0.1625 us |  0.01 |    0.00 |  25296.9 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 4096        | 9     |     11.8900 us |     3.0688 us |   0.1682 us |     12.0552 us | 1.000 |    0.02 |    344.5 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 4096        | 9     |     12.7661 us |     0.3613 us |   0.0198 us |     12.7856 us | 1.074 |    0.01 |    320.8 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 4096        | 9     |      0.7746 us |     0.0236 us |   0.0013 us |      0.7759 us | 0.065 |    0.00 |   5287.8 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 4096        | 9     |      0.1150 us |     0.0128 us |   0.0007 us |      0.1155 us | 0.010 |    0.00 |  35630.9 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 4096        | 9     |     11.9028 us |     1.9758 us |   0.1083 us |     12.0074 us |  1.00 |    0.01 |    344.1 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 4096        | 9     |     15.0713 us |     0.9846 us |   0.0540 us |     15.1153 us |  1.27 |    0.01 |    271.8 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 4096        | 9     |      0.7993 us |     0.1711 us |   0.0094 us |      0.8085 us |  0.07 |    0.00 |   5124.3 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 4096        | 9     |      0.1347 us |     0.1233 us |   0.0068 us |      0.1414 us |  0.01 |    0.00 |  30401.2 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 4096        | 9     |     11.7800 us |     0.2358 us |   0.0129 us |     11.7921 us |  1.00 |    0.00 |    347.7 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 4096        | 9     |     14.3680 us |     3.2271 us |   0.1769 us |     14.5419 us |  1.22 |    0.01 |    285.1 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 4096        | 9     |      0.7828 us |     0.0039 us |   0.0002 us |      0.7830 us |  0.07 |    0.00 |   5232.8 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 4096        | 9     |      0.1263 us |     0.0273 us |   0.0015 us |      0.1273 us |  0.01 |    0.00 |  32443.1 | 0.0007 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 65536       | 1     |     35.8979 us |     0.6889 us |   0.0378 us |     35.9297 us |  1.00 |    0.00 |   1825.6 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 65536       | 1     |     48.9681 us |    10.3084 us |   0.5650 us |     49.4604 us |  1.36 |    0.01 |   1338.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 65536       | 1     |      1.3662 us |     0.0229 us |   0.0013 us |      1.3670 us |  0.04 |    0.00 |  47968.7 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 65536       | 1     |      0.6485 us |     0.0178 us |   0.0010 us |      0.6494 us |  0.02 |    0.00 | 101062.3 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 65536       | 1     |     35.3008 us |     0.0110 us |   0.0006 us |     35.3014 us |  1.00 |    0.00 |   1856.5 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 65536       | 1     |     59.8082 us |     4.4658 us |   0.2448 us |     60.0241 us |  1.69 |    0.01 |   1095.8 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 65536       | 1     |      1.3583 us |     0.0078 us |   0.0004 us |      1.3587 us |  0.04 |    0.00 |  48248.8 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 65536       | 1     |      0.6405 us |     0.0198 us |   0.0011 us |      0.6415 us |  0.02 |    0.00 | 102323.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 65536       | 1     |     36.2828 us |     1.5304 us |   0.0839 us |     36.3655 us |  1.00 |    0.00 |   1806.3 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 65536       | 1     |     50.2401 us |     0.2461 us |   0.0135 us |     50.2516 us |  1.38 |    0.00 |   1304.5 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 65536       | 1     |      1.3686 us |     0.0270 us |   0.0015 us |      1.3696 us |  0.04 |    0.00 |  47886.6 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 65536       | 1     |      0.6417 us |     0.0350 us |   0.0019 us |      0.6435 us |  0.02 |    0.00 | 102120.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 65536       | 1     |     35.8993 us |     0.3364 us |   0.0184 us |     35.9142 us |  1.00 |    0.00 |   1825.6 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 65536       | 1     |     47.8690 us |     6.2661 us |   0.3435 us |     48.2077 us |  1.33 |    0.01 |   1369.1 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 65536       | 1     |      1.3434 us |     0.1379 us |   0.0076 us |      1.3509 us |  0.04 |    0.00 |  48783.7 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 65536       | 1     |      0.6061 us |     0.0664 us |   0.0036 us |      0.6097 us |  0.02 |    0.00 | 108122.8 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 65536       | 1     |     35.3736 us |     3.7709 us |   0.2067 us |     35.5774 us |  1.00 |    0.01 |   1852.7 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 65536       | 1     |     58.7038 us |     2.0600 us |   0.1129 us |     58.8149 us |  1.66 |    0.01 |   1116.4 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 65536       | 1     |      1.3643 us |     0.0287 us |   0.0016 us |      1.3655 us |  0.04 |    0.00 |  48037.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 65536       | 1     |      0.6266 us |     0.0824 us |   0.0045 us |      0.6301 us |  0.02 |    0.00 | 104587.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 65536       | 1     |     35.0047 us |     0.3231 us |   0.0177 us |     35.0199 us |  1.00 |    0.00 |   1872.2 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 65536       | 1     |     49.4005 us |     3.0046 us |   0.1647 us |     49.5093 us |  1.41 |    0.00 |   1326.6 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 65536       | 1     |      1.3516 us |     0.0342 us |   0.0019 us |      1.3534 us |  0.04 |    0.00 |  48488.9 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 65536       | 1     |      0.6446 us |     0.2162 us |   0.0119 us |      0.6559 us |  0.02 |    0.00 | 101663.3 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 65536       | 3     |     37.8846 us |     0.2213 us |   0.0121 us |     37.8960 us |  1.00 |    0.00 |   1729.9 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 65536       | 3     |     52.0814 us |     5.0641 us |   0.2776 us |     52.3545 us |  1.37 |    0.01 |   1258.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 65536       | 3     |      1.3658 us |     0.0198 us |   0.0011 us |      1.3668 us |  0.04 |    0.00 |  47982.7 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 65536       | 3     |      0.6545 us |     0.1217 us |   0.0067 us |      0.6610 us |  0.02 |    0.00 | 100135.6 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 65536       | 3     |     37.0770 us |     2.3902 us |   0.1310 us |     37.2004 us |  1.00 |    0.00 |   1767.6 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 65536       | 3     |     71.8637 us |     7.6070 us |   0.4170 us |     72.2062 us |  1.94 |    0.01 |    911.9 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 65536       | 3     |      1.3573 us |     0.0157 us |   0.0009 us |      1.3581 us |  0.04 |    0.00 |  48282.8 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 65536       | 3     |      0.6289 us |     0.0087 us |   0.0005 us |      0.6293 us |  0.02 |    0.00 | 104213.3 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 65536       | 3     |     37.6237 us |     1.1441 us |   0.0627 us |     37.6792 us |  1.00 |    0.00 |   1741.9 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 65536       | 3     |     54.5735 us |    14.7079 us |   0.8062 us |     55.3685 us |  1.45 |    0.02 |   1200.9 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 65536       | 3     |      1.4055 us |     0.6117 us |   0.0335 us |      1.4295 us |  0.04 |    0.00 |  46629.2 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 65536       | 3     |      0.6366 us |     0.0061 us |   0.0003 us |      0.6369 us |  0.02 |    0.00 | 102953.4 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 65536       | 3     |     37.9944 us |     0.8206 us |   0.0450 us |     38.0387 us |  1.00 |    0.00 |   1724.9 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 65536       | 3     |     50.4755 us |    12.6416 us |   0.6929 us |     51.1588 us |  1.33 |    0.02 |   1298.4 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 65536       | 3     |      1.3438 us |     0.0113 us |   0.0006 us |      1.3443 us |  0.04 |    0.00 |  48770.9 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 65536       | 3     |      0.6481 us |     0.1904 us |   0.0104 us |      0.6574 us |  0.02 |    0.00 | 101117.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 65536       | 3     |     37.6539 us |    15.2256 us |   0.8346 us |     38.4272 us |  1.00 |    0.03 |   1740.5 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 65536       | 3     |     61.8480 us |    20.6026 us |   1.1293 us |     62.8531 us |  1.64 |    0.04 |   1059.6 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 65536       | 3     |      1.3655 us |     0.1693 us |   0.0093 us |      1.3731 us |  0.04 |    0.00 |  47992.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 65536       | 3     |      0.6505 us |     0.1348 us |   0.0074 us |      0.6577 us |  0.02 |    0.00 | 100741.4 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 65536       | 3     |     36.9969 us |     0.3604 us |   0.0198 us |     37.0099 us |  1.00 |    0.00 |   1771.4 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 65536       | 3     |     53.9919 us |     5.8834 us |   0.3225 us |     54.1958 us |  1.46 |    0.01 |   1213.8 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 65536       | 3     |      1.3601 us |     0.1247 us |   0.0068 us |      1.3668 us |  0.04 |    0.00 |  48186.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 65536       | 3     |      0.6515 us |     0.1433 us |   0.0079 us |      0.6585 us |  0.02 |    0.00 | 100598.7 | 0.0010 |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 65536       | 9     |     47.0062 us |     0.8368 us |   0.0459 us |     47.0485 us |  1.00 |    0.00 |   1394.2 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 65536       | 9     |     76.8122 us |     8.0686 us |   0.4423 us |     77.2418 us |  1.63 |    0.01 |    853.2 |      - |      64 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 65536       | 9     |      1.3638 us |     0.0230 us |   0.0013 us |      1.3650 us |  0.03 |    0.00 |  48054.6 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 65536       | 9     |      0.6494 us |     0.2764 us |   0.0151 us |      0.6643 us |  0.01 |    0.00 | 100921.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 65536       | 9     |     46.2932 us |    14.1688 us |   0.7766 us |     47.0561 us |  1.00 |    0.02 |   1415.7 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 65536       | 9     |     86.5422 us |    20.9761 us |   1.1498 us |     87.6743 us |  1.87 |    0.03 |    757.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 65536       | 9     |      1.3809 us |     0.3661 us |   0.0201 us |      1.3981 us |  0.03 |    0.00 |  47457.8 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 65536       | 9     |      0.6439 us |     0.1889 us |   0.0104 us |      0.6534 us |  0.01 |    0.00 | 101778.5 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 65536       | 9     |     47.4542 us |    13.2184 us |   0.7245 us |     48.1669 us |  1.00 |    0.02 |   1381.0 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 65536       | 9     |     80.7835 us |    19.1062 us |   1.0473 us |     81.5116 us |  1.70 |    0.03 |    811.3 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 65536       | 9     |      1.3676 us |     0.0061 us |   0.0003 us |      1.3679 us |  0.03 |    0.00 |  47920.9 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 65536       | 9     |      0.6493 us |     0.0254 us |   0.0014 us |      0.6504 us |  0.01 |    0.00 | 100927.1 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 65536       | 9     |     46.5038 us |     0.7650 us |   0.0419 us |     46.5446 us |  1.00 |    0.00 |   1409.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 65536       | 9     |     62.1395 us |     1.2754 us |   0.0699 us |     62.2023 us |  1.34 |    0.00 |   1054.7 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 65536       | 9     |      1.3422 us |     0.1931 us |   0.0106 us |      1.3526 us |  0.03 |    0.00 |  48826.8 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 65536       | 9     |      0.6219 us |     0.0912 us |   0.0050 us |      0.6254 us |  0.01 |    0.00 | 105383.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 65536       | 9     |     45.9871 us |     0.9029 us |   0.0495 us |     46.0360 us |  1.00 |    0.00 |   1425.1 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 65536       | 9     |     71.6257 us |     5.0034 us |   0.2743 us |     71.8874 us |  1.56 |    0.01 |    915.0 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 65536       | 9     |      1.3711 us |     0.2703 us |   0.0148 us |      1.3855 us |  0.03 |    0.00 |  47798.3 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 65536       | 9     |      0.6249 us |     0.1125 us |   0.0062 us |      0.6310 us |  0.01 |    0.00 | 104873.6 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 65536       | 9     |     45.9111 us |     1.8860 us |   0.1034 us |     46.0129 us |  1.00 |    0.00 |   1427.5 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 65536       | 9     |     63.4346 us |     5.6171 us |   0.3079 us |     63.7349 us |  1.38 |    0.01 |   1033.1 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 65536       | 9     |      1.3555 us |     0.0660 us |   0.0036 us |      1.3587 us |  0.03 |    0.00 |  48348.8 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 65536       | 9     |      0.6435 us |     0.1326 us |   0.0073 us |      0.6496 us |  0.01 |    0.00 | 101850.8 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 1048576     | 1     |    506.9077 us |     3.3660 us |   0.1845 us |    507.0828 us |  1.00 |    0.00 |   2068.6 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 1048576     | 1     |    694.6767 us |     5.5113 us |   0.3021 us |    694.9118 us |  1.37 |    0.00 |   1509.4 |      - |      65 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 1048576     | 1     |     14.4273 us |     0.2761 us |   0.0151 us |     14.4421 us |  0.03 |    0.00 |  72680.1 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 1048576     | 1     |     15.1698 us |     2.9308 us |   0.1606 us |     15.2977 us |  0.03 |    0.00 |  69122.7 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 1048576     | 1     |    513.4840 us |    71.6029 us |   3.9248 us |    517.3432 us |  1.00 |    0.01 |   2042.1 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 1048576     | 1     |    889.5012 us |   291.2886 us |  15.9665 us |    905.2463 us |  1.73 |    0.03 |   1178.8 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 1048576     | 1     |     22.1116 us |    24.2599 us |   1.3298 us |     23.4201 us |  0.04 |    0.00 |  47422.0 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 1048576     | 1     |     16.7150 us |    11.6792 us |   0.6402 us |     17.3461 us |  0.03 |    0.00 |  62732.5 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 1048576     | 1     |    506.1585 us |     4.4045 us |   0.2414 us |    506.3884 us |  1.00 |    0.00 |   2071.6 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 1048576     | 1     |    719.1485 us |    22.4114 us |   1.2284 us |    719.9948 us |  1.42 |    0.00 |   1458.1 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 1048576     | 1     |     14.3340 us |     1.8187 us |   0.0997 us |     14.4244 us |  0.03 |    0.00 |  73153.3 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 1048576     | 1     |     13.0198 us |     1.1056 us |   0.0606 us |     13.0737 us |  0.03 |    0.00 |  80536.8 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 1048576     | 1     |    511.2553 us |    56.3321 us |   3.0878 us |    514.2943 us |  1.00 |    0.01 |   2051.0 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 1048576     | 1     |    699.8667 us |    81.0987 us |   4.4453 us |    703.6021 us |  1.37 |    0.01 |   1498.3 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 1048576     | 1     |     14.3532 us |     1.8359 us |   0.1006 us |     14.4524 us |  0.03 |    0.00 |  73055.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 1048576     | 1     |     12.9814 us |     0.6606 us |   0.0362 us |     13.0142 us |  0.03 |    0.00 |  80775.1 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 1048576     | 1     |    513.2166 us |    20.6028 us |   1.1293 us |    514.2785 us |  1.00 |    0.00 |   2043.1 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 1048576     | 1     |    875.2539 us |   118.4282 us |   6.4914 us |    881.5206 us |  1.71 |    0.01 |   1198.0 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 1048576     | 1     |     14.6490 us |     3.9414 us |   0.2160 us |     14.8591 us |  0.03 |    0.00 |  71580.3 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 1048576     | 1     |     12.9946 us |     1.9969 us |   0.1095 us |     13.0964 us |  0.03 |    0.00 |  80693.3 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 1048576     | 1     |    514.8116 us |   150.1321 us |   8.2292 us |    522.9119 us |  1.00 |    0.02 |   2036.8 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 1048576     | 1     |    722.2857 us |   238.6837 us |  13.0831 us |    735.1695 us |  1.40 |    0.03 |   1451.7 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 1048576     | 1     |     14.4682 us |     4.3574 us |   0.2388 us |     14.6941 us |  0.03 |    0.00 |  72474.3 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 1048576     | 1     |     13.7144 us |    21.2124 us |   1.1627 us |     14.8615 us |  0.03 |    0.00 |  76457.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 1048576     | 3     |    667.9286 us |    52.5823 us |   2.8822 us |    670.7718 us |  1.00 |    0.01 |   1569.9 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 1048576     | 3     |    857.9738 us |     6.3472 us |   0.3479 us |    858.3138 us |  1.28 |    0.00 |   1222.2 |      - |      65 B |          NA |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 1048576     | 3     |     14.2596 us |     0.0797 us |   0.0044 us |     14.2631 us |  0.02 |    0.00 |  73534.7 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 1048576     | 3     |     13.0983 us |     3.1099 us |   0.1705 us |     13.2658 us |  0.02 |    0.00 |  80054.1 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 1048576     | 3     |    667.1562 us |    38.5993 us |   2.1158 us |    668.7823 us |  1.00 |    0.00 |   1571.7 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 1048576     | 3     |  1,072.2686 us |    44.6637 us |   2.4482 us |  1,074.4966 us |  1.61 |    0.01 |    977.9 |      - |      64 B |          NA |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 1048576     | 3     |     15.2042 us |     0.1442 us |   0.0079 us |     15.2102 us |  0.02 |    0.00 |  68966.3 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 1048576     | 3     |     13.2677 us |     7.2878 us |   0.3995 us |     13.6618 us |  0.02 |    0.00 |  79032.2 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 1048576     | 3     |    659.9186 us |    32.2468 us |   1.7676 us |    661.6621 us |  1.00 |    0.00 |   1588.9 |      - |         - |          NA |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 1048576     | 3     |    906.4797 us |    44.9298 us |   2.4628 us |    907.9653 us |  1.37 |    0.00 |   1156.8 |      - |      64 B |          NA |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 1048576     | 3     |     14.4551 us |     0.1988 us |   0.0109 us |     14.4655 us |  0.02 |    0.00 |  72540.1 |      - |         - |          NA |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 1048576     | 3     |     13.1379 us |     0.0803 us |   0.0044 us |     13.1422 us |  0.02 |    0.00 |  79813.4 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 1048576     | 3     |    661.4363 us |    89.5266 us |   4.9073 us |    666.1403 us |  1.00 |    0.01 |   1585.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 1048576     | 3     |    846.1424 us |    39.1849 us |   2.1479 us |    847.4794 us |  1.28 |    0.01 |   1239.2 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 1048576     | 3     |     14.1909 us |     0.3070 us |   0.0168 us |     14.2075 us |  0.02 |    0.00 |  73891.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 1048576     | 3     |     13.1872 us |     3.3880 us |   0.1857 us |     13.3006 us |  0.02 |    0.00 |  79514.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 1048576     | 3     |    678.6959 us |   380.2036 us |  20.8402 us |    694.4359 us |  1.00 |    0.04 |   1545.0 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 1048576     | 3     |  1,045.9587 us |   290.5961 us |  15.9286 us |  1,061.5984 us |  1.54 |    0.05 |   1002.5 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 1048576     | 3     |     23.3506 us |     4.6596 us |   0.2554 us |     23.5896 us |  0.03 |    0.00 |  44905.8 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 1048576     | 3     |     13.0836 us |     1.1389 us |   0.0624 us |     13.1451 us |  0.02 |    0.00 |  80144.2 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 1048576     | 3     |    683.4393 us |   286.1767 us |  15.6863 us |    698.9150 us |  1.00 |    0.03 |   1534.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 1048576     | 3     |    873.8902 us |    56.2831 us |   3.0851 us |    876.5283 us |  1.28 |    0.03 |   1199.9 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 1048576     | 3     |     14.4799 us |     1.2849 us |   0.0704 us |     14.5349 us |  0.02 |    0.00 |  72416.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 1048576     | 3     |     14.5462 us |     7.1899 us |   0.3941 us |     14.9272 us |  0.02 |    0.00 |  72086.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 1048576     | 9     |  1,926.6648 us |   636.3083 us |  34.8782 us |  1,961.0458 us | 1.000 |    0.02 |    544.2 |      - |       3 B |        1.00 |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 1048576     | 9     |  2,151.3326 us |   723.6258 us |  39.6644 us |  2,189.3877 us | 1.117 |    0.02 |    487.4 |      - |      66 B |       22.00 |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 1048576     | 9     |     14.4321 us |     0.1941 us |   0.0106 us |     14.4403 us | 0.007 |    0.00 |  72655.7 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 1048576     | 9     |     13.3060 us |     3.1619 us |   0.1733 us |     13.4685 us | 0.007 |    0.00 |  78804.9 |      - |      56 B |       18.67 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 1048576     | 9     |  1,890.8322 us |   368.9029 us |  20.2208 us |  1,910.7815 us | 1.000 |    0.01 |    554.6 |      - |       1 B |        1.00 |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 1048576     | 9     |  2,313.4723 us |   911.5130 us |  49.9631 us |  2,343.8434 us | 1.224 |    0.03 |    453.2 |      - |      65 B |       65.00 |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 1048576     | 9     |     14.7452 us |     2.9481 us |   0.1616 us |     14.9046 us | 0.008 |    0.00 |  71112.9 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 1048576     | 9     |     12.9320 us |     1.0271 us |   0.0563 us |     12.9874 us | 0.007 |    0.00 |  81083.9 |      - |      56 B |       56.00 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 1048576     | 9     |  1,907.5752 us |   141.3822 us |   7.7496 us |  1,914.9625 us | 1.000 |    0.00 |    549.7 |      - |       1 B |        1.00 |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 1048576     | 9     |  2,131.5698 us |   746.9388 us |  40.9422 us |  2,160.7839 us | 1.117 |    0.02 |    491.9 |      - |      65 B |       65.00 |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 1048576     | 9     |     14.4718 us |     0.5766 us |   0.0316 us |     14.5024 us | 0.008 |    0.00 |  72456.7 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 1048576     | 9     |     13.3634 us |     3.4023 us |   0.1865 us |     13.4977 us | 0.007 |    0.00 |  78466.5 |      - |      56 B |       56.00 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 1048576     | 9     |  1,871.5604 us |   342.6418 us |  18.7814 us |  1,890.0869 us | 1.000 |    0.01 |    560.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 1048576     | 9     |  2,130.2268 us | 1,445.1363 us |  79.2128 us |  2,175.9658 us | 1.138 |    0.04 |    492.2 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 1048576     | 9     |     14.1985 us |     0.1420 us |   0.0078 us |     14.2062 us | 0.008 |    0.00 |  73850.9 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 1048576     | 9     |     13.0501 us |     1.4616 us |   0.0801 us |     13.1290 us | 0.007 |    0.00 |  80349.9 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 1048576     | 9     |  1,897.6454 us |   956.7630 us |  52.4434 us |  1,949.3739 us | 1.001 |    0.03 |    552.6 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 1048576     | 9     |  2,261.8993 us | 1,029.4572 us |  56.4280 us |  2,317.5694 us | 1.193 |    0.04 |    463.6 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 1048576     | 9     |     14.3965 us |     0.4601 us |   0.0252 us |     14.4136 us | 0.008 |    0.00 |  72835.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 1048576     | 9     |     12.9471 us |     1.7175 us |   0.0941 us |     13.0338 us | 0.007 |    0.00 |  80989.1 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 1048576     | 9     |  1,880.8772 us |   882.5833 us |  48.3774 us |  1,928.5813 us | 1.000 |    0.03 |    557.5 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 1048576     | 9     |  2,110.3960 us |   778.9085 us |  42.6946 us |  2,145.4550 us | 1.123 |    0.03 |    496.9 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 1048576     | 9     |     14.5496 us |     4.7703 us |   0.2615 us |     14.8076 us | 0.008 |    0.00 |  72069.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 1048576     | 9     |     12.9180 us |     2.6982 us |   0.1479 us |     13.0639 us | 0.007 |    0.00 |  81172.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 16777216    | 1     |  9,102.3021 us | 5,593.9298 us | 306.6222 us |  9,403.5578 us |  1.00 |    0.04 |   1843.2 |      - |      10 B |        1.00 |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 16777216    | 1     | 12,068.1880 us |    47.0699 us |   2.5801 us | 12,070.5708 us |  1.33 |    0.04 |   1390.2 |      - |      70 B |        7.00 |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 16777216    | 1     |    359.6402 us |    88.9013 us |   4.8730 us |    363.2643 us |  0.04 |    0.00 |  46650.0 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 16777216    | 1     |    314.8265 us |    42.9596 us |   2.3548 us |    316.8360 us |  0.03 |    0.00 |  53290.3 |      - |      56 B |        5.60 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 16777216    | 1     |  8,870.8203 us |   148.6973 us |   8.1506 us |  8,878.8608 us |  1.00 |    0.00 |   1891.3 |      - |       5 B |        1.00 |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 16777216    | 1     | 15,546.0911 us | 5,021.9504 us | 275.2700 us | 15,753.4223 us |  1.75 |    0.03 |   1079.2 |      - |      64 B |       12.80 |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 16777216    | 1     |    289.3605 us |    28.0935 us |   1.5399 us |    290.8481 us |  0.03 |    0.00 |  57980.3 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 16777216    | 1     |    286.5869 us |    71.0730 us |   3.8958 us |    290.4303 us |  0.03 |    0.00 |  58541.5 |      - |      56 B |       11.20 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 16777216    | 1     |  8,902.9919 us |   108.1665 us |   5.9290 us |  8,908.5736 us |  1.00 |    0.00 |   1884.4 |      - |       1 B |        1.00 |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 16777216    | 1     | 12,778.2193 us | 6,645.3743 us | 364.2554 us | 13,135.7663 us |  1.44 |    0.04 |   1313.0 |      - |      65 B |       65.00 |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 16777216    | 1     |    316.7664 us |    13.2611 us |   0.7269 us |    317.3811 us |  0.04 |    0.00 |  52964.0 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 16777216    | 1     |    269.7250 us |    38.9435 us |   2.1346 us |    271.8223 us |  0.03 |    0.00 |  62201.2 |      - |      56 B |       56.00 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 16777216    | 1     |  9,183.8620 us | 2,786.5226 us | 152.7387 us |  9,334.2358 us |  1.00 |    0.02 |   1826.8 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 16777216    | 1     | 12,317.0354 us | 6,921.1383 us | 379.3709 us | 12,690.2436 us |  1.34 |    0.04 |   1362.1 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 16777216    | 1     |    337.1872 us |   240.0252 us |  13.1566 us |    350.0831 us |  0.04 |    0.00 |  49756.4 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 16777216    | 1     |    295.4082 us |   110.5284 us |   6.0584 us |    301.3852 us |  0.03 |    0.00 |  56793.3 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 16777216    | 1     |  9,110.9880 us |   225.4121 us |  12.3556 us |  9,120.3162 us |  1.00 |    0.00 |   1841.4 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 16777216    | 1     | 15,330.6594 us | 3,518.5317 us | 192.8626 us | 15,459.6545 us |  1.68 |    0.02 |   1094.4 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 16777216    | 1     |    327.6020 us |    32.6443 us |   1.7893 us |    329.3673 us |  0.04 |    0.00 |  51212.2 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 16777216    | 1     |    302.3319 us |   114.0560 us |   6.2518 us |    308.4757 us |  0.03 |    0.00 |  55492.7 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 16777216    | 1     |  9,108.9849 us | 1,370.5846 us |  75.1264 us |  9,182.9045 us |  1.00 |    0.01 |   1841.8 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 16777216    | 1     | 12,686.0901 us | 3,887.7794 us | 213.1023 us | 12,895.0767 us |  1.39 |    0.02 |   1322.5 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 16777216    | 1     |    331.8310 us |    46.5404 us |   2.5510 us |    334.3059 us |  0.04 |    0.00 |  50559.5 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 16777216    | 1     |    311.6692 us |    91.2660 us |   5.0026 us |    316.5593 us |  0.03 |    0.00 |  53830.2 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 16777216    | 3     |  9,466.3336 us |   126.9139 us |   6.9566 us |  9,473.1267 us |  1.00 |    0.00 |   1772.3 |      - |      10 B |        1.00 |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 16777216    | 3     | 13,285.2661 us |   441.1180 us |  24.1792 us | 13,301.5039 us |  1.40 |    0.00 |   1262.8 |      - |      65 B |        6.50 |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 16777216    | 3     |    338.9029 us |    45.2977 us |   2.4829 us |    341.3442 us |  0.04 |    0.00 |  49504.5 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 16777216    | 3     |    298.3899 us |    19.8051 us |   1.0856 us |    299.4477 us |  0.03 |    0.00 |  56225.8 |      - |      56 B |        5.60 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 16777216    | 3     |  9,914.4089 us | 4,047.4130 us | 221.8524 us | 10,128.8503 us |  1.00 |    0.03 |   1692.2 |      - |       5 B |        1.00 |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 16777216    | 3     | 16,349.8703 us |   712.6632 us |  39.0635 us | 16,373.6325 us |  1.65 |    0.03 |   1026.1 |      - |      74 B |       14.80 |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 16777216    | 3     |    317.7885 us |    41.9354 us |   2.2986 us |    319.3992 us |  0.03 |    0.00 |  52793.7 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 16777216    | 3     |    319.1897 us |    16.5118 us |   0.9051 us |    319.9538 us |  0.03 |    0.00 |  52561.9 |      - |      56 B |       11.20 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 16777216    | 3     |  9,586.8797 us |   821.4569 us |  45.0268 us |  9,628.1008 us |  1.00 |    0.01 |   1750.0 |      - |       5 B |        1.00 |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 16777216    | 3     | 13,753.5385 us | 1,183.3852 us |  64.8653 us | 13,807.8937 us |  1.43 |    0.01 |   1219.8 |      - |      69 B |       13.80 |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 16777216    | 3     |    348.4022 us |    78.9976 us |   4.3301 us |    352.1800 us |  0.04 |    0.00 |  48154.7 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 16777216    | 3     |    311.8665 us |     9.3371 us |   0.5118 us |    312.2741 us |  0.03 |    0.00 |  53796.2 |      - |      56 B |       11.20 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 16777216    | 3     |  9,822.5552 us | 3,602.2224 us | 197.4500 us | 10,017.3098 us |  1.00 |    0.02 |   1708.0 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 16777216    | 3     | 13,158.6891 us | 6,144.2596 us | 336.7876 us | 13,489.8988 us |  1.34 |    0.04 |   1275.0 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 16777216    | 3     |    321.4671 us |    10.0372 us |   0.5502 us |    321.8099 us |  0.03 |    0.00 |  52189.5 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 16777216    | 3     |    309.6829 us |    61.8063 us |   3.3878 us |    313.0247 us |  0.03 |    0.00 |  54175.5 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 16777216    | 3     |  9,716.3979 us | 2,039.3101 us | 111.7815 us |  9,826.6733 us |  1.00 |    0.01 |   1726.7 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 16777216    | 3     | 16,090.8396 us | 5,459.4669 us | 299.2518 us | 16,382.4178 us |  1.66 |    0.03 |   1042.7 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 16777216    | 3     |    342.7701 us |   114.4072 us |   6.2710 us |    348.9569 us |  0.04 |    0.00 |  48946.0 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 16777216    | 3     |    331.0380 us |   278.5428 us |  15.2679 us |    346.0690 us |  0.03 |    0.00 |  50680.6 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 16777216    | 3     |  9,712.7771 us |   144.1869 us |   7.9034 us |  9,720.5522 us |  1.00 |    0.00 |   1727.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 16777216    | 3     | 13,513.2703 us |   349.1799 us |  19.1397 us | 13,531.3416 us |  1.39 |    0.00 |   1241.5 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 16777216    | 3     |    376.1352 us |   136.1938 us |   7.4652 us |    382.4968 us |  0.04 |    0.00 |  44604.2 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 16777216    | 3     |    310.5882 us |    24.8117 us |   1.3600 us |    311.9298 us |  0.03 |    0.00 |  54017.5 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 10.0 | NativeAOT 10.0 | 16777216    | 9     | 12,773.4216 us |   236.7893 us |  12.9792 us | 12,786.2237 us |  1.00 |    0.00 |   1313.4 |      - |      10 B |        1.00 |
| ZstdSharp.Compress   | AOT 10.0 | NativeAOT 10.0 | 16777216    | 9     | 17,028.2406 us |   777.6723 us |  42.6268 us | 17,070.2228 us |  1.33 |    0.00 |    985.3 |      - |      76 B |        7.60 |
| Native.Decompress    | AOT 10.0 | NativeAOT 10.0 | 16777216    | 9     |    349.1095 us |   136.9742 us |   7.5080 us |    355.1391 us |  0.03 |    0.00 |  48057.2 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 10.0 | NativeAOT 10.0 | 16777216    | 9     |    349.8565 us |    47.6265 us |   2.6106 us |    351.8725 us |  0.03 |    0.00 |  47954.6 |      - |      56 B |        5.60 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 8.0  | NativeAOT 8.0  | 16777216    | 9     | 14,052.1729 us | 2,032.6473 us | 111.4163 us | 14,150.0747 us |  1.00 |    0.01 |   1193.9 |      - |       5 B |        1.00 |
| ZstdSharp.Compress   | AOT 8.0  | NativeAOT 8.0  | 16777216    | 9     | 21,407.2781 us | 7,102.4512 us | 389.3093 us | 21,791.3184 us |  1.52 |    0.03 |    783.7 |      - |      64 B |       12.80 |
| Native.Decompress    | AOT 8.0  | NativeAOT 8.0  | 16777216    | 9     |    363.2015 us |    33.3316 us |   1.8270 us |    364.6325 us |  0.03 |    0.00 |  46192.6 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 8.0  | NativeAOT 8.0  | 16777216    | 9     |    336.5402 us |    38.7729 us |   2.1253 us |    338.5999 us |  0.02 |    0.00 |  49852.0 |      - |      56 B |       11.20 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | AOT 9.0  | NativeAOT 9.0  | 16777216    | 9     | 13,596.0453 us | 1,200.5337 us |  65.8053 us | 13,660.9295 us |  1.00 |    0.01 |   1234.0 |      - |       1 B |        1.00 |
| ZstdSharp.Compress   | AOT 9.0  | NativeAOT 9.0  | 16777216    | 9     | 18,601.4500 us | 4,623.3208 us | 253.4198 us | 18,835.6919 us |  1.37 |    0.02 |    901.9 |      - |      74 B |       74.00 |
| Native.Decompress    | AOT 9.0  | NativeAOT 9.0  | 16777216    | 9     |    374.5031 us |   265.4191 us |  14.5485 us |    388.8436 us |  0.03 |    0.00 |  44798.6 |      - |         - |        0.00 |
| ZstdSharp.Decompress | AOT 9.0  | NativeAOT 9.0  | 16777216    | 9     |    337.1134 us |   182.1897 us |   9.9864 us |    345.8790 us |  0.02 |    0.00 |  49767.3 |      - |      56 B |       56.00 |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 10.0 | .NET 10.0      | 16777216    | 9     | 13,435.8693 us |   729.8975 us |  40.0081 us | 13,459.4231 us |  1.00 |    0.00 |   1248.7 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 10.0 | .NET 10.0      | 16777216    | 9     | 17,515.1271 us | 6,222.6140 us | 341.0825 us | 17,851.5903 us |  1.30 |    0.02 |    957.9 |      - |      64 B |          NA |
| Native.Decompress    | JIT 10.0 | .NET 10.0      | 16777216    | 9     |    363.5715 us |    87.6846 us |   4.8063 us |    368.3125 us |  0.03 |    0.00 |  46145.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 10.0 | .NET 10.0      | 16777216    | 9     |    312.2792 us |    32.0616 us |   1.7574 us |    313.9045 us |  0.02 |    0.00 |  53725.0 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 8.0  | .NET 8.0       | 16777216    | 9     | 13,946.7750 us | 4,057.3400 us | 222.3965 us | 14,163.6081 us |  1.00 |    0.02 |   1202.9 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 8.0  | .NET 8.0       | 16777216    | 9     | 20,734.3208 us | 4,957.0098 us | 271.7104 us | 20,988.4075 us |  1.49 |    0.03 |    809.2 |      - |      64 B |          NA |
| Native.Decompress    | JIT 8.0  | .NET 8.0       | 16777216    | 9     |    361.9815 us |    19.9851 us |   1.0955 us |    363.0618 us |  0.03 |    0.00 |  46348.3 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 8.0  | .NET 8.0       | 16777216    | 9     |    312.6726 us |    42.7632 us |   2.3440 us |    314.9850 us |  0.02 |    0.00 |  53657.5 |      - |      56 B |          NA |
|                      |          |                |             |       |                |               |             |                |       |         |          |        |           |             |
| Native.Compress      | JIT 9.0  | .NET 9.0       | 16777216    | 9     | 13,559.2255 us | 3,779.6324 us | 207.1744 us | 13,744.6338 us |  1.00 |    0.02 |   1237.3 |      - |         - |          NA |
| ZstdSharp.Compress   | JIT 9.0  | .NET 9.0       | 16777216    | 9     | 17,978.2687 us | 3,209.4837 us | 175.9226 us | 18,138.0759 us |  1.33 |    0.02 |    933.2 |      - |      64 B |          NA |
| Native.Decompress    | JIT 9.0  | .NET 9.0       | 16777216    | 9     |    366.1263 us |    28.4486 us |   1.5594 us |    367.5441 us |  0.03 |    0.00 |  45823.6 |      - |         - |          NA |
| ZstdSharp.Decompress | JIT 9.0  | .NET 9.0       | 16777216    | 9     |    317.8782 us |    63.4520 us |   3.4780 us |    321.2499 us |  0.02 |    0.00 |  52778.8 |      - |      56 B |          NA |

> **Decompression and P/Invoke overhead:** Native decompression measures ~0.78–0.81 μs on this CPU at 4 KB — most of that is the P/Invoke call itself, not the codec. ZstdSharp avoids it entirely, which is why it is 4–6× faster at 4 KB and 2× faster at 64 KB. At 1 MiB the codec work (~14 μs) dwarfs the call overhead and both libraries converge. If you decompress many small independent frames, benchmark your actual payload: the P/Invoke cost is fixed at roughly 0.65–0.80 μs regardless of frame size.

Run the benchmarks yourself:

```bash
dotnet run -c Release --project tests/Zstandard.Benchmarks -- --filter "*CompressionBenchmarks*"
dotnet run -c Release --project tests/Zstandard.Benchmarks -- --filter "*StreamingBenchmarks*"
```

The harness runs six jobs (JIT + NativeAOT for .NET 8, 9, and 10) and adds a custom `MB/s` column. Results are sensitive to CPU microarchitecture, power profile, and available ISA extensions — run on your target hardware before drawing conclusions.

---

## Streaming API & context reuse

```csharp
using var c = new ZstdStreamCompressor(
    compressionLevel: 3,
    writeChecksum:    true,
    workerThreads:    Environment.ProcessorCount); // libzstd multi-threading

Span<byte> outBuf = stackalloc byte[ZstdStreamCompressor.RecommendedOutputSize];

while (TryReadChunk(out var chunk, out var isLast))
{
    var r = c.Compress(
        chunk,
        outBuf,
        isLast ? ZstdEndDirective.End : ZstdEndDirective.Continue);

    Sink.Write(outBuf[..r.BytesWritten]);

    if (r.IsCompleted && isLast) break;
}
```

`ZstdStreamResult` reports `BytesConsumed`, `BytesWritten`, and `IsCompleted` so you can drive a producer/consumer loop without juggling raw libzstd return codes.

### Decompression mirror

```csharp
using var d = new ZstdStreamDecompressor();
Span<byte> outBuf = stackalloc byte[ZstdStreamDecompressor.RecommendedOutputSize];

var r = d.Decompress(compressedChunk, outBuf);
if (r.IsCompleted) { /* one full frame consumed */ }
```

### Stream adapters

`ZstdCompressionStream` and `ZstdDecompressionStream` wrap the low-level streaming types behind the standard `System.IO.Stream` interface for drop-in use with existing `Stream`-based pipelines:

```csharp
// Compress into a FileStream
await using var fs = File.OpenWrite("out.zst");
await using var zs = new ZstdCompressionStream(fs, compressionLevel: 3);
await source.CopyToAsync(zs);
// frame is sealed on Dispose — do not skip it

// Decompress from a FileStream
await using var fs2 = File.OpenRead("out.zst");
await using var zd = new ZstdDecompressionStream(fs2);
await zd.CopyToAsync(destination);
```

For maximum throughput on hot paths, prefer `ZstdStreamCompressor` / `ZstdStreamDecompressor` directly — the `Stream` adapters layer an extra copy and `ArrayPool` rent on top.

---

## Native runtime binaries

`Zstandard.Native` is a **pure managed wrapper** — it does not ship a `libzstd` binary itself (similar to how `Npgsql` doesn't ship Postgres). You supply the binary in one of three ways:

### Option 1: Companion runtime package (recommended)

```bash
dotnet add package Zstandard.Native
dotnet add package Zstandard.Native.Runtimes   # planned meta-package
```

The runtime package(s) drop binaries under `runtimes/<rid>/native/`:

```
runtimes/win-x64/native/libzstd.dll
runtimes/win-arm64/native/libzstd.dll
runtimes/linux-x64/native/libzstd.so
runtimes/linux-arm64/native/libzstd.so
runtimes/osx-x64/native/libzstd.dylib
runtimes/osx-arm64/native/libzstd.dylib
```

The `[ModuleInitializer]`-registered resolver probes `runtimes/<rid>/native/` first, so the binary is picked up automatically by both the standard host and `PublishAot=true` publishes.

### Option 2: Bring your own libzstd

Drop `libzstd.dll` (Windows), `libzstd.so` / `libzstd.so.1` (Linux), or `libzstd.dylib` (macOS) anywhere on the OS loader path or next to `AppContext.BaseDirectory`. The resolver probes, in order:

1. `runtimes/<rid>/native/<file>` next to the app
2. `<file>` next to the app
3. The bare library name (delegates to the OS loader)
4. Common alternates (`zstd.dll` on Windows, `libzstd.so.1` on Linux)

### Option 3: System package manager

```bash
# Debian / Ubuntu
sudo apt-get install libzstd1

# Alpine
apk add zstd-libs

# Fedora / RHEL
sudo dnf install libzstd

# macOS / Homebrew
brew install zstd

# Windows
winget install Facebook.Zstandard
```

### Version requirements

The bindings target **libzstd >= 1.5.0** (`ZSTD_compressStream2`, modern parameter API). Earlier versions are not supported.

---

## Thread safety & disposal

| Type | Thread-safe? | Disposal requirement |
|---|---|---|
| `ZstdCompressor` (static) | ✅ yes | n/a |
| `HardwareAccelerator` (static) | ✅ yes | n/a |
| `ZstdDictionaryTrainer` (static) | ✅ yes | n/a |
| `ZstdStreamCompressor` | ❌ **not** | required (`using` / `Dispose()`) |
| `ZstdStreamDecompressor` | ❌ **not** | required (`using` / `Dispose()`) |
| `ZstdCompressionStream` | ❌ **not** | required — frame is only closed on `Dispose()` |
| `ZstdDecompressionStream` | ❌ **not** | required (`using` / `Dispose()`) |
| `ZstdCompressionContextHandle` | dispose-safe | follows handle owner |
| `ZstdDecompressionContextHandle` | dispose-safe | follows handle owner |

The streaming classes carry a mutable native context. **One instance per thread, or external synchronization** — the `ConcurrencyTests` in this repo are the authoritative example: every thread builds its own pair and they round-trip independently. Forgetting to dispose leaks a pooled scratch array to GC; the underlying `SafeHandle` finalizer still frees the native pointer.

---

## Compatibility matrix

| Target framework | Supported | Notes |
|---|---|---|
| `net8.0` | ✅ | Full feature set. Vector512 and AVX-512F lit up on supporting CPUs. |
| `net9.0` | ✅ | Same feature set as net8.0; receives its own TFM slice for future net9 APIs. |
| `net10.0` | ✅ | Adds the AVX10.2 + SVE codegen paths (no source change required). |
| `net6.0`, `net7.0`, `netstandard2.x` | ❌ | `[LibraryImport]` and `nuint` require modern TFMs. |

| RID | CI | AOT gate |
|---|---|---|
| `win-x64` | ✅ | ✅ |
| `win-arm64` | ✅ (`windows-11-arm`) | ✅ |
| `linux-x64` | ✅ | ✅ |
| `linux-arm64` | ✅ | ✅ |
| `osx-x64` | ✅ (`macos-15-intel`) | ✅ |
| `osx-arm64` | ✅ (`macos-15`) | ✅ |

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide. The short version:

1. New libzstd surface goes through `Interop/ZstdNative.cs` as a `[LibraryImport]` partial.
2. Anything that holds a native pointer must use a `SafeHandle`.
3. Public APIs take `Span<byte>` / `ReadOnlySpan<byte>` and must not allocate on the hot path.
4. Every public symbol gets XML docs covering thread safety and disposal.
5. `dotnet build -warnaserror` must stay green — that includes every AOT/trim analyzer.

---

## License

[MIT](LICENSE). The Zstandard reference library is licensed under BSD by Meta — see [https://github.com/facebook/zstd](https://github.com/facebook/zstd).
