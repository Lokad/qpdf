using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class OutputSecurityCopyTests
{
    [Theory]
    [MemberData(nameof(Revision6EncryptionTests.Vectors), MemberType = typeof(Revision6EncryptionTests))]
    public void EitherAuthenticatedPasswordPreservesBothEntriesAfterSourceDisposal(string file, string dictionary,
        string userHex, string ownerHex, string keyHex)
    {
        Assert.NotEmpty(file);
        foreach (bool identity in new[] { false, true })
        foreach (string passwordHex in new[] { userHex, ownerHex })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            OutputSecurity output;
            byte[] identifier = "original identifier"u8.ToArray(), expectedIdentifier = (byte[])identifier.Clone();
            Dictionary<string, byte[]> entries = new();
            using (ParserMemoryScope input = ParserMemoryScope.Create(governor))
            {
                string fields = identity ? dictionary.Replace("/StmF /StdCF /StrF /StdCF", "/StmF /Identity /StrF /Identity /EFF /Identity", StringComparison.Ordinal) : dictionary;
                PdfDictionary encryption = Parse(fields, governor, input);
                foreach ((string name, int length) in new[] { ("U", 48), ("O", 48), ("UE", 32), ("OE", 32), ("Perms", 16) })
                { entries.Add(name, EncryptionDictionary.ReadBytes(encryption, Encoding.ASCII.GetBytes(name), length, governor).ToArray()); }
                Assert.NotEqual(PasswordAuthentication.None, DocumentSecurity.TryCreate(encryption, identifier,
                    Convert.FromHexString(passwordHex), governor, out DocumentSecurity? authenticated));
                using (authenticated)
                {
                    Assert.NotNull(authenticated);
                    Assert.Equal(identity ? PdfCipher.Identity : PdfCipher.Aes256, authenticated.Parameters.Strings);
                    output = authenticated.PrepareOutput(identifier);
                    Assert.NotSame(encryption, output.Dictionary);
                    authenticated.Dispose();
                    Assert.Throws<ObjectDisposedException>(() => authenticated.PrepareOutput(identifier));
                }
            }
            using (output)
            using (ParserMemoryScope payload = ParserMemoryScope.Create(governor))
            {
                identifier.AsSpan().Fill(0);
                Assert.Equal(expectedIdentifier, Assert.IsType<ReadOnlyMemory<byte>>(output.PreservedIdentifier).ToArray());
                foreach ((string name, byte[] expected) in entries)
                { Assert.Equal(expected, EncryptionDictionary.ReadBytes(output.Dictionary, Encoding.ASCII.GetBytes(name), expected.Length, governor).ToArray()); }
                foreach ((string password, PasswordAuthentication match) in new[] { (userHex, PasswordAuthentication.User), (ownerHex, PasswordAuthentication.Owner) })
                {
                    Assert.Equal(match, DocumentSecurity.TryCreate(output.Dictionary, expectedIdentifier,
                        Convert.FromHexString(password), governor, out DocumentSecurity? reopened));
                    using (reopened)
                    {
                        Assert.NotNull(reopened);
                        Assert.Equal(PdfCipher.Aes256, reopened.Parameters.Strings);
                        Assert.Equal(PdfCipher.Aes256, reopened.Parameters.Streams);
                        Assert.Equal(PdfCipher.Aes256, reopened.Parameters.Files);
                    }
                }
                byte[] ciphertext = output.Encrypt("copied protection"u8, 4, 0, payload, governor);
                using Aes aes = Aes.Create(); aes.SetKey(Convert.FromHexString(keyHex));
                Assert.Equal("copied protection"u8.ToArray(), aes.DecryptCbc(ciphertext.AsSpan(16), ciphertext.AsSpan(0, 16), PaddingMode.PKCS7));
            }
            Assert.Throws<ObjectDisposedException>(() => output.PreservedIdentifier);
            Assert.Equal(0, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public void CopyAdmissionFailuresRefundStorageAndLeaveAuthenticatedInputUsable()
    {
        (int before, int growths) = Run(new(1048576), false);
        Assert.True(growths > 0);
        for (int growth = 1; growth <= growths; growth++)
        { _ = Run(new(1048576) { RefuseOnGrowth = before + growth }, true); }

        static (int Before, int Growths) Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope source = ParserMemoryScope.Create(governor);
            object[] vector = Revision6EncryptionTests.Vectors().First();
            PdfDictionary dictionary = Parse((string)vector[1], governor, source);
            ResourceGovernor funded = new(QpdfLimits.Default, CancellationToken.None, host);
            Assert.Equal(PasswordAuthentication.User, DocumentSecurity.TryCreate(dictionary, [], "reader"u8, funded, out DocumentSecurity? authenticated));
            using (authenticated)
            {
                Assert.NotNull(authenticated);
                int before = budget.GrowthCalls; long live = funded.Snapshot().LiveMemoryBytes;
                byte[] identifier = new byte[65537]; identifier[^1] = 42;
                if (refuses)
                {
                    Assert.Throws<QpdfMemoryAdmissionException>(() => authenticated.PrepareOutput(identifier));
                    Assert.Equal(live, funded.Snapshot().LiveMemoryBytes);
                }
                int growths;
                using (OutputSecurity output = authenticated.PrepareOutput(identifier))
                {
                    growths = budget.GrowthCalls - before;
                    Assert.Equal(identifier, Assert.IsType<ReadOnlyMemory<byte>>(output.PreservedIdentifier).ToArray());
                }
                Assert.Equal(live, funded.Snapshot().LiveMemoryBytes);
                authenticated.Dispose(); Assert.Equal(0, budget.ReservedBytes);
                return (before, growths);
            }
        }
    }

    [Fact]
    public void CanceledCopyDoesNotAllocateAnOutputOwner()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        object[] vector = Revision6EncryptionTests.Vectors().First();
        PdfDictionary dictionary = Parse((string)vector[1], governor, source);
        _ = DocumentSecurity.TryCreate(dictionary, [], "reader"u8, governor, out DocumentSecurity? authenticated);
        using (authenticated)
        {
            Assert.NotNull(authenticated); long live = governor.Snapshot().LiveMemoryBytes;
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => authenticated.PrepareOutput([]));
            Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        }
    }

    private static PdfDictionary Parse(string text, ResourceGovernor governor, ParserMemoryScope owner)
        => Assert.IsType<PdfDictionary>(new PdfParser(QpdfLimits.Default, governor, new(governor), null)
            .ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, owner));
}
