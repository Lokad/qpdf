using System.Reflection;

namespace Lokad.Qpdf.Tests;

internal static class TestSourceTree
{
    internal static string Root { get; } = typeof(TestSourceTree).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(item => item.Key == "QpdfSourceRoot").Value ?? throw new InvalidOperationException("The test build has no source root.");
}
