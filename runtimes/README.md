# Native runtime binaries

This directory contains per-RID Zstandard native binaries that are packed into
`Zstandard.Native.nupkg` under `runtimes/<rid>/native/` (the standard .NET
runtime-asset layout) and copied next to the consuming app's binaries at build
time.

## Expected files

| RID            | File              |
|----------------|-------------------|
| `win-x64`      | `libzstd.dll`     |
| `win-arm64`    | `libzstd.dll`     |
| `linux-x64`    | `libzstd.so`      |
| `linux-arm64`  | `libzstd.so`      |
| `osx-x64`      | `libzstd.dylib`   |
| `osx-arm64`    | `libzstd.dylib`   |

## Populating this directory

The binaries are **not** checked into git — they are downloaded by
`scripts/fetch-natives.{ps1,sh}` from the upstream
[`facebook/zstd`](https://github.com/facebook/zstd/releases) release for the
current platform.

```pwsh
# Windows
pwsh scripts/fetch-natives.ps1
```

```bash
# Linux / macOS
bash scripts/fetch-natives.sh
```

After fetching, `dotnet build` will copy the binaries to each consumer's output
directory and `dotnet pack` will roll them into the nupkg.
