using Lokad.Qpdf.Jobs;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class EncryptionPermissionTests
{
    [Theory]
    [InlineData("modify", "all", -4)]
    [InlineData("modify", "annotate", -12)]
    [InlineData("modify", "form", -44)]
    [InlineData("modify", "assembly", -300)]
    [InlineData("modify", "none", -1324)]
    [InlineData("print", "none", -2056)]
    [InlineData("print", "low", -2052)]
    [InlineData("print", "full", -4)]
    [InlineData("accessibility", "n", -4)]
    [InlineData("accessibility", "y", -4)]
    [InlineData("extract", "n", -20)]
    [InlineData("extract", "y", -4)]
    [InlineData("annotate", "n", -36)]
    [InlineData("annotate", "y", -4)]
    [InlineData("assemble", "n", -1028)]
    [InlineData("assemble", "y", -4)]
    [InlineData("form", "n", -260)]
    [InlineData("form", "y", -4)]
    [InlineData("modifyOther", "n", -12)]
    [InlineData("modifyOther", "y", -4)]
    public void SettersProduceTheRecordedPermissionWords(string flag, string value, int expected)
        => Assert.Equal(expected, EncryptionPermissions.Apply(-4, flag, value));
}
