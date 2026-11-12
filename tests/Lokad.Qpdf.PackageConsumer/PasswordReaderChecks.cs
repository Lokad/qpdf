using System.Text;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

internal static class PasswordReaderChecks
{
    internal static async Task RunAsync()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath encrypted = Add("security-r6-ascii.pdf"), empty = Add("security-r6-empty.pdf");
        byte[] ownerBytes = Encoding.ASCII.GetBytes("fixture-owner");
        PdfName titleName = new("Title"u8.ToArray());
        foreach ((QpdfPath path, QpdfPassword? password, bool owner) in new[]
        {
            (encrypted, QpdfPassword.FromText("reader"), false),
            (encrypted, QpdfPassword.FromBytes(ownerBytes), true),
            (empty, (QpdfPassword?)null, false),
        })
        {
            PdfByteString? title = null;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None))
            {
                Check(document.PageCount == 1, "authenticated page tree");
                QpdfEncryptionInfo info = document.Encryption ?? throw new InvalidOperationException("Missing encryption facts.");
                Check(info.Revision == 6 && info.KeyLengthBits == 256 && info.Permissions == -3388, "non-secret encryption facts");
                Check(info.OwnerPasswordMatched == owner && info.EncryptMetadata == (path == encrypted), "authentication and metadata facts");
                for (int number = 1; number < 8; number++)
                {
                    if (document.GetObject(number) is PdfDictionary dictionary && dictionary.TryGet(titleName, out QpdfObject value))
                    { title = value as PdfByteString; }
                }
                Check(password is null || password.ToString() == "QpdfPassword [redacted]", "redacted password formatting");
            }
            Check(title is not null && Encoding.ASCII.GetString(title.Value) == "TitleText", "decrypted value survives input disposal");
        }
        Check(ownerBytes.AsSpan().SequenceEqual("fixture-owner"u8), "borrowed password remains caller-owned");
        await Failure(null, QpdfSecurityFailure.PasswordRequired);
        await Failure(QpdfPassword.FromText(""), QpdfSecurityFailure.InvalidPassword);
        await Failure(QpdfPassword.FromText("consumer-secret"), QpdfSecurityFailure.InvalidPassword);
        Console.WriteLine("PASS public R6 reader, per-input text/raw credentials, empty password, non-secret facts and retained plaintext");

        QpdfPath Add(string file)
        {
            using Stream stream = typeof(PasswordReaderChecks).Assembly.GetManifestResourceStream("Fixtures." + file)
                ?? throw new InvalidOperationException("Missing password reader fixture.");
            using MemoryStream data = new(); stream.CopyTo(data);
            QpdfPath path = QpdfPath.From(file); host.AddInput(path, data.ToArray(), QpdfInputOwnership.Copy); return path;
        }

        async Task Failure(QpdfPassword? password, QpdfSecurityFailure reason)
        {
            try
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, encrypted, password, QpdfLimits.Default, CancellationToken.None);
                throw new InvalidOperationException("Incorrect password unexpectedly opened input.");
            }
            catch (QpdfSecurityException error)
            {
                Check(error.Reason == reason && !error.Message.Contains("consumer-secret", StringComparison.Ordinal), "distinct, secret-free credential outcome");
            }
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}
