# Security Policy

## Supported Versions

The following versions of Zstandard.Native receive security updates:

| Version | Supported | End of Support |
| ------- | --------- | -------------- |
| 0.x     | :white_check_mark: | Active development (pre-1.0; latest patch only) |

Once we hit 1.0.0, the policy will mirror semver: the current `MAJOR.MINOR.x` line plus the previous one.

## Reporting a Vulnerability

**DO NOT** report security vulnerabilities through public GitHub issues.

### How to Report

Use GitHub's private vulnerability reporting:

1. Open [https://github.com/Jeffeek/Zstandard.Native/security](https://github.com/Jeffeek/Zstandard.Native/security)
2. Click **Report a vulnerability**
3. Fill in the details (see below).

If private reporting is unavailable, contact the maintainer via GitHub profile.

### What to Include

- **Type of vulnerability** (DoS, memory corruption, info disclosure, RCE, supply-chain)
- **Affected versions**
- **Affected components** (e.g. `ZstdStreamCompressor`, the P/Invoke surface, native binary handling)
- **Full paths of affected source files** (if known)
- **Reproduction steps**
- **Proof-of-concept or exploit code** (if possible)
- **Impact assessment**
- **Suggested fix** (if you have one)

### Response Timeline

| Stage | Timeline |
|-------|----------|
| Initial acknowledgment | Within 48 hours (2 business days) |
| Preliminary assessment | Within 7 days |
| Status updates | Every 7–14 days until resolved |
| Fix development | 30–90 days (depending on severity) |
| Coordinated disclosure | After fix is released |

### Severity Classification

We follow [CVSS v3.1](https://www.first.org/cvss/v3.1/specification-document):

- **Critical (9.0–10.0)** — patch within 7 days
- **High (7.0–8.9)** — patch within 30 days
- **Medium (4.0–6.9)** — patch within 60 days
- **Low (0.1–3.9)** — patch within 90 days

### What to Expect

**If accepted:**

- We'll coordinate disclosure timing with you.
- You'll be credited in the security advisory unless you request anonymity.
- A CVE will be requested for trackable vulnerabilities.
- A security patch will be released.

**If declined:**

- We'll explain why the issue doesn't qualify and may suggest workarounds.
- The issue may be converted to a regular bug report (with your permission).

### Disclosure Policy

Coordinated disclosure — typically 90 days from initial report, adjustable.

### Out of Scope

The following are not considered security vulnerabilities:

- Decompression of an attacker-supplied frame causing high CPU / memory if the caller didn't bound the output buffer or check `ZSTD_getFrameContentSize` (caller responsibility — see "Best practices" below).
- Issues in the bundled or system-supplied `libzstd` binary (report upstream at [facebook/zstd](https://github.com/facebook/zstd)).
- Issues requiring physical machine access.
- Social engineering.
- Issues in sample / benchmark code (`tests/Zstandard.Benchmarks`, `eng/AotProbe`).

### Best Practices

When using Zstandard.Native on untrusted input:

1. **Bound the output buffer.** Call `ZstdCompressor.GetFrameContentSize(...)` and reject frames whose content size is missing or unreasonable for your use case before allocating.
2. **Use a `ZstdStreamDecompressor` with `windowLogMax`** to cap memory use against malicious headers.
3. **Catch `ZstdException`** at the decode boundary; never let frame errors propagate into business logic.
4. **Keep `libzstd` updated** — track [facebook/zstd security advisories](https://github.com/facebook/zstd/security/advisories).
5. **Verify the native binary** if you supply your own — match SHA against the upstream release.

### References

- [NuGet supply-chain security](https://learn.microsoft.com/nuget/concepts/security-best-practices)
- [.NET Native AOT security](https://learn.microsoft.com/dotnet/core/deploying/native-aot/security)

Thank you for helping keep Zstandard.Native secure. 🔒
