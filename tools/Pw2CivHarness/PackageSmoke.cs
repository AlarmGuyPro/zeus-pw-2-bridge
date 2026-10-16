// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Zeus.Plugins.Contracts;

internal static class PackageSmoke
{
    public static int Run(string directory)
    {
        var root = Path.GetFullPath(directory);
        var entrypoint = Path.Combine(root, "Zeus.Community.Pw2Bridge.dll");
        var resolver = new AssemblyDependencyResolver(entrypoint);
        // The runtime canonicalizes ancestor symlinks (for example /var on macOS).
        entrypoint = resolver.ResolveAssemblyToPath(new AssemblyName("Zeus.Community.Pw2Bridge"))
            ?? throw new InvalidOperationException("Package cannot resolve its entrypoint");
        root = Path.GetDirectoryName(entrypoint)!;
        var serialPath = resolver.ResolveAssemblyToPath(new AssemblyName("System.IO.Ports"))
            ?? throw new InvalidOperationException("Package cannot resolve System.IO.Ports");
        var platform = OperatingSystem.IsWindows() ? "win" : "unix";
        var expected = Path.Combine(root, "runtimes", platform, "lib", "net9.0", "System.IO.Ports.dll");
        if (!string.Equals(serialPath, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Wrong serial implementation: {serialPath}; expected {expected}");

        // Load the package's implementation explicitly so the harness's own
        // System.IO.Ports cannot hide a missing or wrong packaged dependency.
        var isolated = new PackageContext(entrypoint, shareSystem: false);
        try
        {
            var serial = isolated.LoadFromAssemblyPath(serialPath);
            var type = serial.GetType("System.IO.Ports.SerialPort", throwOnError: true)!;
            using var port = (IDisposable)Activator.CreateInstance(type)!;
            if (!OperatingSystem.IsWindows())
            {
                var nativePath = resolver.ResolveUnmanagedDllToPath("System.IO.Ports.Native")
                    ?? throw new InvalidOperationException("Package cannot resolve native serial library");
                var native = NativeLibrary.Load(nativePath);
                try
                {
                    _ = NativeLibrary.GetExport(native, "SystemIoPortsNative_SerialPortOpen");
                    Console.WriteLine($"PASS: native serial library loaded: {nativePath}");
                }
                finally { NativeLibrary.Free(native); }
            }
            Console.WriteLine($"PASS: packaged serial implementation: {serial.Location}");
        }
        finally { isolated.Unload(); }

        // Match Zeus's shared System.*, Microsoft.* and contracts policy.
        // Construct only: InitializeAsync could auto-connect to real hardware.
        var host = new PackageContext(entrypoint, shareSystem: true);
        try
        {
            var assembly = host.LoadFromAssemblyPath(entrypoint);
            var type = assembly.GetType("Zeus.Community.Pw2Bridge.Pw2BridgePlugin", throwOnError: true)!;
            _ = (IZeusPlugin)Activator.CreateInstance(type)!;
            var serialReference = assembly.GetReferencedAssemblies()
                .Single(name => name.Name == "System.IO.Ports");
            var sharedSerial = host.LoadFromAssemblyName(serialReference);
            if (sharedSerial != typeof(System.IO.Ports.SerialPort).Assembly ||
                sharedSerial.GetName().Version?.Major != 10)
                throw new InvalidOperationException("Plugin did not bind to the host's System.IO.Ports 10");
            using var hostPort = new System.IO.Ports.SerialPort();
            Console.WriteLine("PASS: plugin binds its serial reference to shared host System.IO.Ports 10");
            Console.WriteLine("PASS: plugin constructs with shared host contracts");
        }
        finally { host.Unload(); }
        return 0;
    }

    private sealed class PackageContext(string entrypoint, bool shareSystem)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entrypoint);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is { } n &&
                (n.StartsWith("Zeus.Plugins.Contracts", StringComparison.Ordinal) ||
                 n.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                 (shareSystem && n.StartsWith("System.", StringComparison.Ordinal)) ||
                 n == "netstandard"))
                return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
