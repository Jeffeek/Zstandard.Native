---
name: Feature Request
about: Suggest a new feature or enhancement for Zstandard.Native
title: '[FEATURE] '
labels: 'enhancement'
assignees: ''

---

## Problem / Use Case
A clear description of the problem this feature would solve.

**Example**: "I need to compress 200-byte JSON payloads with a pre-trained dictionary and a single CCtx reused across thousands of frames..."

## Proposed Solution

### API Design
Sketch the ideal API:

```csharp
using var compressor = new ZstdStreamCompressor(
    compressionLevel: 5,
    dictionary: trainedDict); // proposed addition

compressor.Reset();
var r = compressor.Compress(payload, outBuf, ZstdEndDirective.End);
```

### Behavior
- What should happen when…?
- How should errors be handled?
- Allocation profile on the hot path?
- AOT-compatible?
- Thread-safety expectations?

## Alternatives Considered

1. **Alternative 1**: …
   - Pros / Cons
2. **Alternative 2**: …
   - Pros / Cons

## Workaround (if any)
```csharp
// Current workaround using existing API
```

## Impact and Priority

### Who benefits?
- [ ] All users
- [ ] Specific scenarios — which?
- [ ] Advanced / production users only

### Priority
- [ ] Critical — blocking
- [ ] High — significant improvement
- [ ] Medium — nice to have
- [ ] Low — future enhancement

### Breaking Changes
- [ ] Requires breaking changes
- [ ] Can be added non-breakingly
- [ ] Unsure

## libzstd surface
- libzstd function(s) involved: e.g., `ZSTD_CCtx_loadDictionary`, `ZDICT_finalizeDictionary`
- Minimum libzstd version required: e.g., `>= 1.5.0`

## Checklist
- [ ] I searched existing issues to avoid duplicates
- [ ] I described the use case and motivation
- [ ] I proposed an API design (or described desired behavior)
- [ ] I considered alternatives and trade-offs
- [ ] I am willing to contribute this feature (optional)
