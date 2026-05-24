# Contributing to Zstandard.Native

Thanks for your interest. This document is opinionated on purpose — the library exists to be ultra-fast and AOT-safe, and most contribution friction comes from those two constraints.

If a section feels too strict for your change, open an issue and we'll talk before you sink time into a PR.

---

## Table of contents

- [Ground rules](#ground-rules)
- [Local development](#local-development)
- [Adding a new libzstd feature](#adding-a-new-libzstd-feature)
  - [Worked example: dictionary training (`ZDICT_trainFromBuffer`)](#worked-example-dictionary-training-zdict_trainfrombuffer)
- [Public API checklist](#public-api-checklist)
- [Performance hygiene](#performance-hygiene)
- [Testing requirements](#testing-requirements)
- [Documentation requirements](#documentation-requirements)
- [Commits, branches, releases](#commits-branches-releases)
- [Security disclosures](#security-disclosures)

---

## Ground rules

1. **Native AOT must keep compiling.** The CI publish workflow runs an AOT validation gate against a real `dotnet publish -p:PublishAot=true`. PRs that introduce `IL2xxx`/`IL3xxx` warnings are blocked.
2. **Zero managed allocations on the hot path.** Use `Span<byte>` / `ReadOnlySpan<byte>`. If a feature genuinely needs a buffer, rent from `ArrayPool<byte>.Shared` and return it on `Dispose`.
3. **No reflection. Ever.** `Activator.CreateInstance`, `Type.GetType`, `Expression.Compile`, `Marshal.PtrToStructure` are all out.
4. **Source-generated P/Invoke only.** New native imports go through `[LibraryImport]` partials.
5. **Native pointers live behind a `SafeHandle`.** Raw `IntPtr` ownership in public API is a non-starter.
6. **Public types document thread safety and disposal.** A type without that note is incomplete.

---

## Local development

```bash
git clone https://github.com/Jeffeek/Zstandard.Native
cd Zstandard.Native

dotnet restore Zstandard.Native.slnx
dotnet build   Zstandard.Native.slnx -c Release -warnaserror
dotnet test    tests/Zstandard.Native.Tests
```

You need:

- .NET 8, .NET 9, and .NET 10 SDKs (the library targets all three).
- A `libzstd` binary on the loader path. The simplest setup on Windows is `winget install Facebook.Zstandard`; on Linux `apt-get install libzstd1`; on macOS `brew install zstd`. See the README for the resolver probe order.
- PowerShell 7+ if you want to run `scripts/release.ps1`. The bash equivalent works anywhere.

To exercise the AOT gate locally:

```bash
dotnet pack src/Zstandard.Native -c Release -o artifacts/nupkg \
  -p:VersionPrefix=0.1.0 -p:VersionSuffix=local

# point the probe at the local feed (or copy NuGet.config from the publish.yml step)
dotnet publish eng/AotProbe -c Release -r linux-x64 \
  -p:PublishAot=true -p:ZstandardNativeVersion=0.1.0-local
./eng/AotProbe/bin/Release/net10.0/linux-x64/publish/AotProbe
```

---

## Adding a new libzstd feature

The structure for adding any new libzstd surface is the same. Walk through every step — skipping one is the most common reason a PR gets bounced.

### 1. Add the native signature

Add a `[LibraryImport]` partial to `src/Zstandard.Native/Interop/ZstdNative.cs`. **Use only blittable types** (`void*`, `nuint`, `ulong`, `nint`, blittable structs). No marshalling attributes on primitives — they imply runtime marshalling and break AOT.

```csharp
[LibraryImport(LibraryName, EntryPoint = "ZSTD_someNewApi")]
internal static unsafe partial nuint ZSTD_someNewApi(void* arg, nuint size);
```

### 2. If the API returns or accepts a pointer that needs cleanup → `SafeHandle`

Create a sealed class deriving from `System.Runtime.InteropServices.SafeHandle` under `src/Zstandard.Native/SafeHandles/`. Implement `ReleaseHandle()` by calling the matching libzstd free function. Follow the pattern in `ZstdCompressionContextHandle`.

### 3. Wrap in a public API

Public methods accept `Span<byte>` / `ReadOnlySpan<byte>`, return primitives or `record struct` results. Use `fixed` + `MemoryMarshal.GetReference` to pin and call native. Never `ToArray()` a span on the hot path.

### 4. Error handling

Wrap every libzstd return code in `ZstdException.ThrowIfError(code)`. Don't invent custom exception types for individual error codes — consumers can branch on `ZstdException.ErrorCode` if needed.

### 5. Tests

Add at least:

- A round-trip test (encode → decode → byte equality) in `tests/Zstandard.Native.Tests`.
- An error path test (invalid input throws `ZstdException`).
- If the feature involves state, a concurrency test using one instance per thread.

### 6. Benchmark (if perf-relevant)

Add a benchmark in `tests/Zstandard.Benchmarks` with `[MemoryDiagnoser]` and a sensible `[Params]` sweep. Compare against `ZstdSharp.Port` when there's a counterpart.

### 7. XML docs

Every public symbol — type, method, property, enum value. Cover **thread safety** and **disposal** in `<remarks>` for any type that owns a native handle. See the existing classes for tone.

### 8. README

Update the README if the feature changes the public surface in a user-visible way.

---

### Worked example: dictionary training (`ZDICT_trainFromBuffer`)

Suppose you want to expose [`ZDICT_trainFromBuffer`](https://facebook.github.io/zstd/zstd_manual.html#Chapter22) so callers can train a dictionary from a corpus.

**Step 1 — native signature.** Add to `Interop/ZstdNative.cs`:

```csharp
[LibraryImport(LibraryName, EntryPoint = "ZDICT_trainFromBuffer")]
internal static unsafe partial nuint ZDICT_trainFromBuffer(
    void* dictBuffer, nuint dictBufferCapacity,
    void* samplesBuffer, nuint* samplesSizes, uint nbSamples);

[LibraryImport(LibraryName, EntryPoint = "ZDICT_isError")]
internal static partial uint ZDICT_isError(nuint code);
```

(Note `ZDICT_*` functions actually live in `libzstd` proper on most builds; if your environment splits them into `libzdict` add a second `LibraryName` constant and a second resolver entry.)

**Step 2 — public API.** Add `src/Zstandard.Native/ZstdDictionaryTrainer.cs`:

```csharp
public static class ZstdDictionaryTrainer
{
    /// <summary>
    /// Trains a Zstandard dictionary from a concatenated sample buffer.
    /// </summary>
    /// <param name="samples">All samples packed end-to-end.</param>
    /// <param name="sampleSizes">Length of each sample, in order.</param>
    /// <param name="dictionary">Destination buffer; typical size 100 KiB.</param>
    /// <returns>Bytes written into <paramref name="dictionary"/>.</returns>
    /// <remarks>
    /// Thread-safe: the underlying API is stateless. Allocate at least
    /// <c>112_640</c> bytes for the destination per zstd guidance.
    /// </remarks>
    public static int Train(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<nuint> sampleSizes,
        Span<byte> dictionary)
    {
        unsafe
        {
            fixed (byte* s = &MemoryMarshal.GetReference(samples))
            fixed (nuint* sz = &MemoryMarshal.GetReference(sampleSizes))
            fixed (byte* d = &MemoryMarshal.GetReference(dictionary))
            {
                var written = ZstdNative.ZDICT_trainFromBuffer(
                    d, (nuint)dictionary.Length,
                    s, sz, (uint)sampleSizes.Length);

                ZstdException.ThrowIfError(written); // ZDICT errors share the size_t convention
                return checked((int)written);
            }
        }
    }
}
```

**Step 3 — tests.** Train against a small corpus, verify the result starts with the Zstandard dictionary magic number `0xEC30A437`, and use it with `ZSTD_CCtx_loadDictionary` to compress + decompress a sample.

**Step 4 — benchmark.** Optional — dictionary training itself is rarely on a hot path, but using the trained dictionary at compress/decompress time is. Add a benchmark that compares dictionary-aware vs dictionary-free compression of the same corpus.

**Step 5 — XML + README.** Document the new class and add a paragraph to the README "Streaming API" section pointing at it.

That sequence — *signature → optional SafeHandle → public Span API → tests → benchmark → docs* — is the template for any feature: long-distance mode, ZSTD_CCtx_refPrefix, ZSTD_seekable_*, mtConsume, etc.

---

## Public API checklist

Before opening a PR, walk through every new public symbol against this list:

- [ ] Source-generated `[LibraryImport]` (no `[DllImport]`).
- [ ] Blittable native signatures only.
- [ ] Span-shaped public surface (no `byte[]` parameters).
- [ ] Allocation-free on success path (verify with `[MemoryDiagnoser]`).
- [ ] Native pointers wrapped in a `SafeHandle`.
- [ ] `ZstdException.ThrowIfError` used for every native return code.
- [ ] XML doc with thread-safety + disposal note.
- [ ] Round-trip test.
- [ ] Error-path test.
- [ ] Concurrency test if the type owns mutable native state.
- [ ] `dotnet build -warnaserror` is clean on `net8.0`, `net9.0`, and `net10.0`.
- [ ] AOT gate (locally or in CI) is green.

---

## Performance hygiene

- Don't `ToArray()` or `AsMemory()` a span on the hot path.
- Don't allocate inside `Compress` / `Decompress` — use stack buffers or pooled scratch.
- Don't add a `try`/`catch` inside a tight loop; throw exceptions, but don't speculatively guard.
- Don't introduce a `lock` in the codec path. Document non-thread-safety in XML instead.
- Don't widen `int` to `long` on hot return values without `checked()` — overflow in `size_t` paths is a real bug.
- Prefer `MemoryMarshal.GetReference` + `fixed` over `Span.Pin()` for native interop.

---

## Testing requirements

`tests/Zstandard.Native.Tests` uses xUnit. New tests should follow the existing structure:

| File | What goes there |
|---|---|
| `RoundTripTests.cs` | encode → decode → equality across sizes/levels |
| `EdgeCaseTests.cs` | empty, corrupted, truncated, oversized payloads |
| `ConcurrencyTests.cs` | parallel one-instance-per-thread workloads |
| `HardwareAcceleratorTests.cs` | vector utilities, boundary sweeps |

For the **>2 GiB payload** case, gate the test on `ZSTD_RUN_LARGE_TESTS=1` (early `return` if unset) to keep CI fast.

---

## Documentation requirements

- Every `public` type/method/property gets `<summary>`. Internals are exempt.
- Types owning native state (`SafeHandle` derivatives, streaming classes) get `<remarks>` covering:
  - whether instances are thread-safe;
  - whether `Dispose()` is required;
  - what happens if you forget (finalizer behavior, pool leak, etc.).
- Use `<see cref="…"/>` for cross-references — broken crefs are build errors with `GenerateDocumentationFile=true`.

---

## Commits, branches, releases

- **Branch from `master`** for features. Use short, kebab-cased names: `feat/dict-training`, `fix/resolver-rid-arm64`.
- **Conventional commit subjects** are appreciated but not enforced.
- **Releases are cut from `release/v<semver>` branches.** `scripts/release.{sh,ps1}` derives the version automatically. Pushing to `master` produces a `*-preview.<run>` build, pushing to `release/vX.Y.Z` produces a stable release. See [`publish.yml`](.github/workflows/publish.yml).
- Don't update `<VersionPrefix>` in `Directory.Build.props` as part of a feature PR. Cut a release branch when ready.

---

## Security disclosures

Don't open a public issue for a security problem. Email the maintainer listed in the package metadata, or use [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) on this repo. CodeQL and `NuGetAudit` are enabled in CI as a backstop.
