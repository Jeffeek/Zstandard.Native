# Zstandard.Native

Ultra-fast **Native AOT-safe** Zstandard wrapper for .NET 8, .NET 9, and .NET 10 — zero-allocation `Span<byte>` APIs, source-generated `[LibraryImport]` P/Invoke, and hardware-accelerated buffer paths via AVX-512 / AVX10.2 and ARM64 SVE.

## Install

```bash
dotnet add package Zstandard.Native
```

You also need a `libzstd` binary on the loader path. See [Native runtime binaries](https://github.com/Jeffeek/Zstandard.Native#native-runtime-binaries) for options (companion runtime package, bring-your-own, or system package manager).

## Quick start

### One-shot compress / decompress

```csharp
using Zstandard.Native;

ReadOnlySpan<byte> src = File.ReadAllBytes("data.bin");

byte[] compressed = new byte[ZstdCompressor.GetCompressBound(src.Length)];
int n = ZstdCompressor.Compress(src, compressed, compressionLevel: 3);

byte[] back = new byte[src.Length];
ZstdCompressor.Decompress(compressed.AsSpan(0, n), back);
```

### Streaming with context reuse

```csharp
using var compressor = new ZstdStreamCompressor(compressionLevel: 3);
Span<byte> outBuf = stackalloc byte[ZstdStreamCompressor.RecommendedOutputSize];

foreach (var frame in frames)
{
    compressor.Reset(); // reuses ZSTD_CCtx — no allocation
    var r = compressor.Compress(frame.Span, outBuf, ZstdEndDirective.End);
    sink.Write(outBuf[..r.BytesWritten]);
}
```

## Features

- **Zero allocations** on the hot path — `Span<byte>`-only public API
- **Native AOT** compatible — every `IL2xxx`/`IL3xxx` warning is an error in CI; no reflection, no marshalling shims
- **`SafeHandle`** lifetime management for `ZSTD_CCtx` / `ZSTD_DCtx` — finalizer-safe even on process abort
- **Streaming** with `Reset()` context reuse — skips `ZSTD_createCCtx` per frame
- **Hardware acceleration** via `Vector512` (AVX-512 / AVX10.2) and SVE (ARM64 variable-width) for buffer scrub operations
- Targets **libzstd ≥ 1.5.0** on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`

## Performance highlights

| Scenario | Runtime | Throughput | Allocated |
|---|---|---|---|
| Stream compress, context reuse, 1 MiB | NativeAOT 10.0 | **12 510 MB/s** | **0 B** |
| Stream compress, context reuse, 64 KB | NativeAOT 10.0 | **12 015 MB/s** | **0 B** |
| One-shot compress vs ZstdSharp, 64 KB level 3 | AOT 10.0 | **+37% faster** | **0 B** vs 64 B |
| One-shot compress vs ZstdSharp, 1 MiB level 1 | AOT 10.0 | **+37% faster** | **0 B** vs 65 B |

[Full benchmark tables →](https://github.com/Jeffeek/Zstandard.Native/blob/master/tests/Zstandard.Benchmarks/README.md)

## Links

- [GitHub](https://github.com/Jeffeek/Zstandard.Native)
- [Full documentation](https://github.com/Jeffeek/Zstandard.Native#readme)
- [License: MIT](https://github.com/Jeffeek/Zstandard.Native/blob/master/LICENSE)
