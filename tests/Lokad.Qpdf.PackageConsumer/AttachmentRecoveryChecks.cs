using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

internal static class AttachmentRecoveryChecks
{
    internal static async Task RunAsync(byte[] plain)
    {
        using Stream resource = typeof(AttachmentRecoveryChecks).Assembly.GetManifestResourceStream("Fixtures.security-r6-attachment.pdf")
            ?? throw new InvalidOperationException("Missing encrypted attachment fixture.");
        using MemoryStream fixture = new(); resource.CopyTo(fixture);
        byte[] damaged = fixture.ToArray();
        int offset = damaged.AsSpan().LastIndexOf("startxref\n"u8) + 10;
        Check(offset >= 10, "startxref control");
        while (damaged[offset] is >= (byte)'0' and <= (byte)'9') { damaged[offset++] = (byte)'9'; }
        QpdfPassword password = QpdfPassword.FromText("fixture-owner");
        QpdfPath input = QpdfPath.From("attachment-recovery"), output = QpdfPath.From("attachment-output"), source = QpdfPath.From("attachment-source");
        using InMemoryQpdfHost host = new();
        host.AddInput(input, damaged, QpdfInputOwnership.Copy);
        host.AddInput(source, damaged, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("plain"), plain, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("added"), "B"u8.ToArray(), QpdfInputOwnership.Copy);
        await Reject(() => Qpdf.ListAttachmentsAsync(host, input, password, QpdfLimits.Default, CancellationToken.None).AsTask());
        QpdfAttachmentListResult listed = await Qpdf.ListAttachmentsAsync(host, input, password, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(listed.Attachments.Count == 1 && listed.Attachments[0].Key == "private.bin", "repaired listing metadata");
        Check(listed.Warnings.Any(w => w.Code == QpdfDiagnosticCode.RecoveryEngaged) && listed.Counters.OpenHandles == 0, "listing warnings and counters");
        await Reject(() => Qpdf.ExtractAttachmentAsync(host, input, "private.bin", output, password, QpdfLimits.Default, CancellationToken.None).AsTask());
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, input, "private.bin", output, password, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(host.TryGetOutput(output, out byte[] payload) && payload.AsSpan().SequenceEqual("Private attachment payload.\n"u8)
            && extracted.Warnings.Any(w => w.Code == QpdfDiagnosticCode.RecoveryEngaged) && extracted.Counters.OpenHandles == 0, "repaired extraction");
        QpdfAttachmentSpec added = new("added", "extra", "extra", null, null, null, null, false);
        await Reject(() => Qpdf.AddAttachmentsAsync(host, input, output, [added], password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask());
        QpdfAttachmentEditResult add = await Qpdf.AddAttachmentsAsync(host, input, output, [added], password, QpdfOutputEncryption.Preserve, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(add.AttachmentCount == 2 && add.Warnings.Any(w => w.Code == QpdfDiagnosticCode.RecoveryEngaged), "repaired addition");
        await Reject(() => Qpdf.RemoveAttachmentsAsync(host, input, output, ["private.bin"], password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask());
        QpdfAttachmentEditResult remove = await Qpdf.RemoveAttachmentsAsync(host, input, output, ["private.bin"], password, QpdfOutputEncryption.Preserve, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(remove.AttachmentCount == 0 && remove.Counters.OpenHandles == 0, "repaired removal");
        await Reject(() => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("plain"), output, [new(source, "copy-", password)], null, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask());
        QpdfAttachmentEditResult copy = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("plain"), output, [new(source, "copy-", password)], null, QpdfOutputEncryption.Preserve, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(copy.AttachmentCount == 1 && copy.Warnings.Any(w => w.Code == QpdfDiagnosticCode.RecoveryEngaged), "independent copy-source recovery");
        QpdfAttachmentEditResult template = await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("plain"), output, [added], null,
            QpdfOutputEncryption.CopyFrom(source, password), QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Check(template.AttachmentCount == 1 && template.Warnings.Any(w => w.Code == QpdfDiagnosticCode.RecoveryEngaged), "independent template recovery");
        Check(host.TryGetOutput(output, out byte[] protectedBytes), "template output");
        host.AddInput(QpdfPath.From("protected-readback"), protectedBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument readback = await QpdfDocument.OpenAsync(host, QpdfPath.From("protected-readback"), password, QpdfLimits.Default, CancellationToken.None))
        { Check(readback.Encryption is not null && readback.PageCount == 2, "repaired template protects the output"); }
        Check(listed.Attachments[0].Key == "private.bin" && listed.Warnings.Count > 0 && host.OpenLeaseCount == 0, "escaped listing and closed leases");
    }

    private static async Task Reject(Func<Task> run)
    {
        try { await run(); }
        catch (QpdfParseException) { return; }
        throw new InvalidOperationException("Typed attachment default unexpectedly repaired an input.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Attachment recovery: " + message); } }
}
