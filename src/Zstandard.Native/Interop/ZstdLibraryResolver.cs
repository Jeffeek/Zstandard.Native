using System.Reflection;
using System.Runtime.InteropServices;

namespace Zstandard.Native.Interop;

/// <summary>
/// Resolves the native libzstd binary for the current platform/architecture.
/// Registered once via a module initializer; <see cref="NativeLibrary.SetDllImportResolver"/>
/// itself is AOT-safe (no reflection over types).
/// </summary>
internal static class ZstdLibraryResolver
{
    private static int _registered;

#pragma warning disable CA2255 // ModuleInitializer is the intended mechanism for one-time P/Invoke resolver registration.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
#pragma warning restore CA2255
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(ZstdNative).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ZstdNative.LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        string rid;
        string fileName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            rid = $"win-{arch}";
            fileName = "libzstd.dll";
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
            yield return Path.Combine(AppContext.BaseDirectory, fileName);
            yield return fileName;
            yield return "zstd.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            rid = $"linux-{arch}";
            fileName = "libzstd.so";
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
            yield return Path.Combine(AppContext.BaseDirectory, fileName);
            yield return fileName;
            yield return "libzstd.so.1";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            rid = $"osx-{arch}";
            fileName = "libzstd.dylib";
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);
            yield return Path.Combine(AppContext.BaseDirectory, fileName);
            yield return fileName;
        }
        else
        {
            yield return ZstdNative.LibraryName;
        }
    }
}
