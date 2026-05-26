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

The whole library compiles cleanly under `PublishAot=true` on .NET 8, .NET 9, and .NET 10, **with every `IL2xxx` and `IL3xxx` warning promoted to an error in `Directory.Build.props`**. The CI publish pipeline runs a dedicated AOT validation gate that does a real `dotnet publish -p:PublishAot=true` of a consumer probe project against the just-built nupkg and refuses to ship if the resulting native binary doesn't pass a round-trip test.

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

[Full benchmark tables →](tests/Zstandard.Benchmarks/README.md)

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
dotnet add package Zstandard.Native.Runtimes
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
