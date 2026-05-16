---
name: Bug Report
about: Report a bug in Zstandard.Native
title: '[BUG] '
labels: 'bug'
assignees: ''

---

## Bug Description
A clear description of the bug.

## To Reproduce
Minimal code example that reproduces the issue:

```csharp
ReadOnlySpan<byte> source = ...;
Span<byte> destination = new byte[ZstdCompressor.GetCompressBound(source.Length)];
int written = ZstdCompressor.Compress(source, destination, compressionLevel: 3);
// observed: ...
// expected: ...
```

**Steps**:
1. Run the snippet above (or describe the streaming setup).
2. Observe the behavior.
3. See error / unexpected result.

## Expected Behavior
What you expected to happen.

## Actual Behavior
What actually happened — include the full exception message and stack trace if any.

## Environment
- **Zstandard.Native version**: e.g., `0.1.0-preview.7`
- **Target framework**: e.g., `net10.0`
- **OS / RID**: e.g., `Windows 11 x64`, `Ubuntu 24.04 arm64`, `macOS 14 arm64`
- **libzstd version**: e.g., `1.5.6` (run `ZstdNative.ZSTD_versionNumber()` if unsure)
- **Source of libzstd**: bundled in package / system package manager / hand-placed
- **.NET SDK**: output of `dotnet --info`
- **AOT?**: yes / no — if yes, include the publish command

## Additional Context
- Is this regression from a previous version? If so, which one worked?
- Does it reproduce on every run, or intermittently?
- Have you tried with `HardwareAccelerator.IsHardwareAccelerated == false`?
- Anything in the payload size, compression level, or thread count that affects reproducibility?
