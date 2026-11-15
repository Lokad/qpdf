using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentPreparationAdmissionTests
{
    [Fact]
    public Task SharedPruningRefundsEveryOwnerIncludingPromotionAndPendingPublication()
    {
        QpdfPath output = QpdfPath.From("output");
        return PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(SharedPruningTests.SharedFixture(2, true, true), output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-z", "--",
                "--remove-unreferenced-resources=yes", "--stream-data=preserve", "--compress-streams=n", "output"]), token),
            output, 128, ["write", "cancel"]);
    }

    [Theory]
    [InlineData("coalesce")]
    [InlineData("normalize")]
    [InlineData("externalize")]
    public Task PreparedContentRefundsOwnersAndRespectsPublication(string mode)
    {
        QpdfPath output = QpdfPath.From("output");
        string option = mode switch { "coalesce" => "--coalesce-contents", "normalize" => "--normalize-content=y",
            "externalize" => "--externalize-inline-images", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        return PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(ContentSafetyTests.Fixture(
            Encoding.ASCII.GetBytes(new string(' ', 524288) + "BI /W 1 /H 1 /BPC 8 /CS /G ID X EI"), "", false, 1), output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", option, "--ii-min-bytes=0", "--", "output"]), token), output, 64, ["write"]);
    }
}
