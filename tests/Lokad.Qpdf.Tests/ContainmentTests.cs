using System.Reflection;

namespace Lokad.Qpdf.Tests;

public sealed class ContainmentTests
{
    private static readonly string[] AllowedReferences =
    [
        "System.Collections",
        "System.IO.Compression",
        "System.Linq",
        "System.Memory",
        "System.Runtime",
        "System.Runtime.InteropServices",
        // MD5 fingerprints deterministic IDs and attachment digests only; excluded crypto operations never appear.
        "System.Security.Cryptography",
        "System.Text.Encoding.Extensions",
        "System.Text.Json",
        // Monitor serializes the borrowed JPEG memory budget within each call.
        "System.Threading",
    ];

    [Fact]
    public void LibraryReferencesOnlyApprovedAssemblies()
    {
        Assembly assembly = typeof(Qpdf).Assembly;
        string[] referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(AllowedReferences, referenced);
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Net.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Drawing", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryDeclaresNoNativeEntryPoints()
    {
        Assembly assembly = typeof(Qpdf).Assembly;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        List<string> native = new();
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    native.Add(type.FullName + "." + method.Name);
                }
            }
        }

        Assert.Empty(native);
    }
}
