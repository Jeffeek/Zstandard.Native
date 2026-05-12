# Zstandard.Native

[![ci](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/ci.yml/badge.svg)](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/ci.yml)
[![codeql](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/codeql.yml/badge.svg)](https://github.com/Jeffeek/Zstandard.Native/actions/workflows/codeql.yml)
[![NuGet](https://img.shields.io/nuget/v/Zstandard.Native.svg)](https://www.nuget.org/packages/Zstandard.Native)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Ultra-fast, **Native AOT-safe** Zstandard wrapper for **.NET 8** and **.NET 10** with zero-allocation `Span<byte>` APIs, source-generated `[LibraryImport]` bindings, and hardware-accelerated paths that target **AVX10.2** on x86 and **SVE** on ARM64 via the .NET 10 JIT.

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

Numbers below are from `samples/Zstandard.Benchmarks` running on **Ryzen 7 7840U (AVX-512), Windows 11, .NET 10 preview**, level 3, payload = 1 MiB of semi-compressible bytes. **Reproduce them locally — your CPU and inputs will shift the table.**

| Operation                          | Mean      | StdDev   | Allocated | MB/s   |
|------------------------------------|----------:|---------:|----------:|-------:|
| `Zstandard.Native` Compress        | ~1.8 ms   | <1 %     | **0 B**   | ~580   |
| `ZstdSharp.Port` Compress          | ~2.4 ms   | <2 %     | ~24 B     | ~430   |
| `Zstandard.Native` Decompress      | ~0.45 ms  | <1 %     | **0 B**   | ~2 300 |
| `ZstdSharp.Port` Decompress        | ~0.62 ms  | <2 %     | ~24 B     | ~1 690 |
| `Zstandard.Native` Stream (reuse)  | ~1.8 ms   | <1 %     | **0 B**   | ~580   |
| `Zstandard.Native` Stream (fresh)  | ~1.9 ms   | <2 %     | ~120 B    | ~550   |

> The “fresh” streaming row pays the `ZSTD_createCCtx` cost every call. The “reuse” row uses `Reset()` and is allocation-free past the first construction.

Run the benchmarks yourself:

```bash
dotnet run -c Release --project samples/Zstandard.Benchmarks -- --filter "*CompressionBenchmarks*"
dotnet run -c Release --project samples/Zstandard.Benchmarks -- --runtimes net8.0 nativeaot8.0
```

The bench harness includes a `JIT` vs `AOT` job pair and a custom `MB/s` column.

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
| `ZstdStreamCompressor` | ❌ **not** | required (`using` / `Dispose()`) |
| `ZstdStreamDecompressor` | ❌ **not** | required (`using` / `Dispose()`) |
| `ZstdCompressionContextHandle` | dispose-safe | follows handle owner |
| `ZstdDecompressionContextHandle` | dispose-safe | follows handle owner |

The streaming classes carry a mutable native context. **One instance per thread, or external synchronization** — the `ConcurrencyTests` in this repo are the authoritative example: every thread builds its own pair and they round-trip independently. Forgetting to dispose leaks a pooled scratch array to GC; the underlying `SafeHandle` finalizer still frees the native pointer.

---

## Compatibility matrix

| Target framework | Supported | Notes |
|---|---|---|
| `net8.0` | ✅ | Full feature set. Vector512 and AVX-512F lit up on supporting CPUs. |
| `net10.0` | ✅ | Adds the AVX10.2 + SVE codegen paths (no source change required). |
| `net6.0`, `net7.0`, `netstandard2.x` | ❌ | `[LibraryImport]` and `nuint` require modern TFMs. |

| RID | CI | AOT gate |
|---|---|---|
| `win-x64` | ✅ | ✅ |
| `linux-x64` | ✅ | ✅ |
| `linux-arm64` | ✅ | ✅ |
| `osx-x64` / `osx-arm64` | community | not in CI yet |

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
