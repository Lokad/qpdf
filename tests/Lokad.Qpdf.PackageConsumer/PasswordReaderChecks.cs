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
        QpdfPath legacy = Add("security-r4-pdfdoc.pdf");
        byte[] ownerBytes = Encoding.ASCII.GetBytes("fixture-owner");
        PdfName titleName = new("Title"u8.ToArray());
        foreach ((QpdfPath path, QpdfPassword? password, bool owner, int revision) in new[]
        {
            (encrypted, QpdfPassword.FromText("reader"), false, 6),
            (encrypted, QpdfPassword.FromBytes(ownerBytes), true, 6),
            (empty, (QpdfPassword?)null, false, 6),
            (legacy, QpdfPassword.FromText("café•"), false, 4),
            (legacy, QpdfPassword.FromBytes(Convert.FromHexString("6F776E6572E991")), true, 4),
        })
        {
            PdfByteString? title = null;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, password, QpdfLimits.Default, CancellationToken.None))
            {
                Check(document.PageCount == 1, "authenticated page tree");
                QpdfEncryptionInfo info = document.Encryption ?? throw new InvalidOperationException("Missing encryption facts.");
                Check(info.Revision == revision && info.KeyLengthBits == (revision == 6 ? 256 : 128) && info.Permissions == -3388, "non-secret encryption facts");
                Check(info.OwnerPasswordMatched == owner && info.EncryptMetadata == (path != empty), "authentication and metadata facts");
                for (int number = 1; number < 8; number++)
                {
                    if (document.GetObject(number) is PdfDictionary dictionary && dictionary.TryGet(titleName, out QpdfObject value))
                    { title = value as PdfByteString; }
                }
                Check(password is null || password.ToString() == "QpdfPassword [redacted]", "redacted password formatting");
            }
            Check(title is not null && Encoding.ASCII.GetString(title.Value) == "TitleText", "decrypted value survives input disposal");
            QpdfCheckResult inspection = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions { Password = password }, QpdfLimits.Default, CancellationToken.None);
            Check(inspection.PageCount == 1 && inspection.Checks.All(check => check.Status != QpdfCheckStatus.Failed), "authenticated structural checks");
            Check(inspection.Checks.Single(check => check.Name == "trailer").Detail.Contains("password authentication", StringComparison.Ordinal), "authenticated trailer reporting");
        }
        Check(ownerBytes.AsSpan().SequenceEqual("fixture-owner"u8), "borrowed password remains caller-owned");
        QpdfExecutionResult command = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            [encrypted.Value, "--password=reader", "--check", "--show-npages"]), CancellationToken.None);
        Check(command.Status == QpdfExecutionStatus.Completed && command.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.PageCount), "password command inspection");
        QpdfExecutionResult missing = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"security-r6-ascii.pdf","check":""}""", CancellationToken.None);
        Check(missing.Diagnostics.Single().Code == QpdfDiagnosticCode.PasswordRequired, "password-required command diagnostic");
        QpdfExecutionResult query = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            [encrypted.Value, "--password=reader", "--requires-password", "--warning-exit-0"]), CancellationToken.None);
        Check(query.Status == QpdfExecutionStatus.Completed && (int)query.ExitCode == 3 && query.Diagnostics.Count == 0,
            "native password predicate status independent of warnings");
        QpdfExecutionResult report = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"security-r6-ascii.pdf","showEncryption":"","password":"consumer-secret","check":""}""", CancellationToken.None);
        Check(report.Status == QpdfExecutionStatus.Completed && report.ExitCode == QpdfExitCode.Success
            && report.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.EncryptionReport && item.Message == "R = 6")
            && report.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.CheckSkipped)
            && report.Diagnostics.All(item => !item.Message.Contains("consumer-secret", StringComparison.Ordinal)),
            "secret-free report with unauthenticated checks skipped");
        await Failure(null, QpdfSecurityFailure.PasswordRequired);
        await Failure(QpdfPassword.FromText(""), QpdfSecurityFailure.InvalidPassword);
        await Failure(QpdfPassword.FromText("consumer-secret"), QpdfSecurityFailure.InvalidPassword);
        Console.WriteLine("PASS public R4/R6 reader, per-input text/raw credentials, empty password, non-secret facts and retained plaintext");

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
