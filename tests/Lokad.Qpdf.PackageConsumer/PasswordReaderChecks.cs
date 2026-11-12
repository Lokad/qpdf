using System.Text;
using System.Text.Json;
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
        foreach ((QpdfPath source, QpdfPassword? user, QpdfPassword owner) in new[]
        {
            (encrypted, QpdfPassword.FromText("reader"), QpdfPassword.FromBytes(ownerBytes)),
            (empty, (QpdfPassword?)null, QpdfPassword.FromBytes(ownerBytes)),
            (legacy, QpdfPassword.FromText("café•"), QpdfPassword.FromBytes(Convert.FromHexString("6F776E6572E991"))),
        })
        {
            QpdfPath selected = QpdfPath.From("selected-" + source.Value);
            PdfPageSelectionResult selection = await Qpdf.SelectPageAsync(host, source, selected,
                PdfPageSelectionOptions.FromPages([1, 1]), user, CancellationToken.None);
            Check(selection.PageCount == 2, "password-aware repeated page selection");
            Check(host.TryGetOutput(selected, out byte[] output), "protected selection publication");
            QpdfPath reopened = QpdfPath.From("reopened-" + source.Value);
            host.AddInput(reopened, output, QpdfInputOwnership.Copy);
            foreach (QpdfPassword? credential in new[] { user, owner })
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened, credential, QpdfLimits.Default, CancellationToken.None);
                Check(document.PageCount == 2 && document.Encryption?.Permissions == -3388
                    && document.Encryption?.EncryptMetadata == (source != empty), "preserved user/owner passwords and policy");
            }
        }
        QpdfPath removed = QpdfPath.From("removed.pdf");
        _ = await Qpdf.SelectPageAsync(host, encrypted, removed, new PdfPageSelectionOptions(1),
            QpdfPassword.FromText("reader"), QpdfOutputEncryption.Remove, CancellationToken.None);
        Check(host.TryGetOutput(removed, out byte[] plain), "explicit decryption publication");
        QpdfPath plainInput = QpdfPath.From("plain-input.pdf"); host.AddInput(plainInput, plain, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, plainInput, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption is null, "explicitly removed protection"); }
        QpdfPath attachmentSource = Add("security-r4-attachment.pdf");
        QpdfExecutionResult binaryAttachment = await Qpdf.ExecuteAsync(host, new([attachmentSource.Value, "--password=café•",
            "--show-attachment=private.bin", "--", "authenticated-payload.bin"]), CancellationToken.None);
        Check(binaryAttachment.Status == QpdfExecutionStatus.Completed
            && host.TryGetOutput(QpdfPath.From("authenticated-payload.bin"), out byte[] authenticatedPayload)
            && authenticatedPayload.AsSpan().SequenceEqual("Private attachment payload.\n"u8), "authenticated binary attachment command");
        host.AddInput(QpdfPath.From("extraction-password"), "6f776e6572e991\n"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult binaryStream = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            { inputFile = attachmentSource.Value, outputFile = "authenticated-stream.bin", passwordFile = "extraction-password",
                passwordMode = "hex-bytes", showObject = "9", filteredStreamData = "" }), CancellationToken.None);
        Check(binaryStream.Status == QpdfExecutionStatus.Completed
            && host.TryGetOutput(QpdfPath.From("authenticated-stream.bin"), out byte[] authenticatedStream)
            && authenticatedStream.AsSpan().SequenceEqual("BT (Page) Tj ET"u8), "authenticated binary stream job");
        QpdfAttachmentEditResult authenticatedCopy = await Qpdf.CopyAttachmentsAsync(host, plainInput, QpdfPath.From("attachment-copy.pdf"),
            [new QpdfAttachmentCopy(attachmentSource, "copy-", QpdfPassword.FromText("café•"))], CancellationToken.None);
        Check(authenticatedCopy.AttachmentCount == 1, "typed authenticated attachment source");
        Check(host.TryGetOutput(QpdfPath.From("attachment-copy.pdf"), out byte[] attachmentCopyBytes), "authenticated attachment publication");
        host.AddInput(QpdfPath.From("attachment-copy-input.pdf"), attachmentCopyBytes, QpdfInputOwnership.Copy);
        _ = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("attachment-copy-input.pdf"), "copy-private.bin", QpdfPath.From("copied-payload.bin"), CancellationToken.None);
        Check(host.TryGetOutput(QpdfPath.From("copied-payload.bin"), out byte[] copiedPayload)
            && copiedPayload.AsSpan().SequenceEqual("Private attachment payload.\n"u8), "independently encrypted attachment payload");
        QpdfExecutionResult placement = await Qpdf.ExecuteAsync(host, new([encrypted.Value, "placement.pdf", "--password=reader",
            "--overlay", legacy.Value, "--password=café•", "--", "--linearize", "--object-streams=generate"]), CancellationToken.None);
        Check(placement.Status == QpdfExecutionStatus.Completed, "independent placement credentials");
        Check(host.TryGetOutput(QpdfPath.From("placement.pdf"), out byte[] placementBytes), "protected placement publication");
        QpdfPath placedInput = QpdfPath.From("placed-input.pdf"); host.AddInput(placedInput, placementBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, placedInput, QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption?.Revision == 6, "placement preserves primary protection"); }
        QpdfExecutionResult placedJson = await Qpdf.ExecuteAsync(host, new([placedInput.Value, "--password=reader", "--json", "--json-key=qpdf",
            "--json-stream-data=inline", "--", "placed.json"]), CancellationToken.None);
        Check(placedJson.Status == QpdfExecutionStatus.Completed && host.TryGetOutput(QpdfPath.From("placed.json"), out _), "placement content inspection");
        Check(host.TryGetOutput(QpdfPath.From("placed.json"), out byte[] placedJsonBytes), "placement JSON publication");
        using (JsonDocument document = JsonDocument.Parse(placedJsonBytes))
        {
            int forms = 0;
            foreach (JsonProperty property in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                if (!property.Value.TryGetProperty("stream", out JsonElement stream)
                    || !stream.GetProperty("dict").TryGetProperty("/Subtype", out JsonElement subtype) || subtype.GetString() != "/Form") { continue; }
                forms++; Check(Encoding.ASCII.GetString(Convert.FromBase64String(stream.GetProperty("data").GetString() ?? "")).Contains("(Page)", StringComparison.Ordinal), "decrypted placement payload");
            }
            Check(forms == 2, "primary and placement forms retained");
        }
        _ = await Qpdf.SelectPageAsync(host, plainInput, QpdfPath.From("from-template.pdf"), new PdfPageSelectionOptions(1), null,
            QpdfOutputEncryption.CopyFrom(legacy, QpdfPassword.FromText("café•")), CancellationToken.None);
        Check(host.TryGetOutput(QpdfPath.From("from-template.pdf"), out byte[] templateBytes), "typed template publication");
        QpdfPath templateCopy = QpdfPath.From("template-copy.pdf"); host.AddInput(templateCopy, templateBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, templateCopy, QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991")), QpdfLimits.Default, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption is { Revision: 4, Permissions: -3388, EncryptMetadata: true }, "template retains the other password and permissions"); }
        foreach (bool jobTemplate in new[] { false, true })
        {
            string target = jobTemplate ? "job-template.pdf" : "argv-template.pdf";
            QpdfExecutionResult copiedTemplate = jobTemplate ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = encrypted.Value, outputFile = target, password = "reader", copyEncryption = empty.Value,
                    encryptionFilePassword = "", linearize = "", objectStreams = "generate" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([encrypted.Value, target, "--password=reader", "--copy-encryption=" + empty.Value,
                    "--encryption-file-password=", "--linearize", "--object-streams=generate"]), CancellationToken.None);
            Check(copiedTemplate.Status == QpdfExecutionStatus.Completed, "command template copy");
            Check(host.TryGetOutput(QpdfPath.From(target), out byte[] copiedTemplateBytes), "command template publication");
            QpdfPath reopened = QpdfPath.From("reopened-" + target); host.AddInput(reopened, copiedTemplateBytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened, CancellationToken.None);
            Check(document.PageCount == 1 && document.Encryption is { Revision: 6, Permissions: -3388, EncryptMetadata: false }, "template overrides primary protection");
        }
        QpdfPath updateJson = QpdfPath.From("encrypted-update.json");
        host.AddInput(updateJson, """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Copy);
        foreach (bool remove in new[] { false, true })
        {
            QpdfPath updated = QpdfPath.From(remove ? "updated-plain.pdf" : "updated-protected.pdf");
            QpdfJsonImportResult update = remove
                ? await Qpdf.UpdatePdfFromJsonAsync(host, encrypted, updateJson, updated, QpdfPassword.FromText("reader"),
                    QpdfOutputEncryption.Remove, QpdfLimits.Default, CancellationToken.None)
                : await Qpdf.UpdatePdfFromJsonAsync(host, encrypted, updateJson, updated, QpdfPassword.FromText("reader"),
                    QpdfOutputEncryption.Preserve, CancellationToken.None);
            Check(update.PageCount == 1 && host.TryGetOutput(updated, out _), "authenticated JSON update publication");
            Check(host.TryGetOutput(updated, out byte[] updateBytes), "updated PDF bytes");
            QpdfPath reopened = QpdfPath.From("reopened-" + updated.Value); host.AddInput(reopened, updateBytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened,
                remove ? null : QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None);
            Check(document.PageCount == 1 && (remove ? document.Encryption is null : document.Encryption?.Revision == 6), "JSON update output protection");
        }
        foreach (bool jobUpdate in new[] { false, true })
        {
            QpdfPath target = QpdfPath.From(jobUpdate ? "job-update.pdf" : "argv-update.pdf");
            QpdfExecutionResult updated = jobUpdate ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = encrypted.Value, outputFile = target.Value, password = "reader", updateFromJson = updateJson.Value, decrypt = "" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([encrypted.Value, target.Value, "--password=reader", "--update-from-json=" + updateJson.Value, "--decrypt"]), CancellationToken.None);
            Check(updated.Status == QpdfExecutionStatus.Completed, "authenticated command JSON update");
            Check(host.TryGetOutput(target, out byte[] updateBytes), "command update bytes");
            QpdfPath reopened = QpdfPath.From("reopened-" + target.Value); host.AddInput(reopened, updateBytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened, CancellationToken.None);
            Check(document.PageCount == 1 && document.Encryption is null, "command update removes protection explicitly");
        }
        QpdfOutputEncryption newProtection = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("writer-reader"),
            QpdfPassword.FromBytes("writer-owner"u8.ToArray()), -3388, false);
        IReadOnlyList<QpdfAttachmentInfo> primaryAttachments = await Qpdf.ListAttachmentsAsync(host, attachmentSource,
            QpdfPassword.FromText("café•"), QpdfLimits.Default, CancellationToken.None);
        Check(primaryAttachments.Count == 1 && primaryAttachments[0].Key == "private.bin", "typed authenticated attachment listing");
        _ = await Qpdf.ExtractAttachmentAsync(host, attachmentSource, "private.bin", QpdfPath.From("primary-payload.bin"),
            QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991")), QpdfLimits.Default, CancellationToken.None);
        Check(host.TryGetOutput(QpdfPath.From("primary-payload.bin"), out byte[] primaryPayload)
            && primaryPayload.AsSpan().SequenceEqual("Private attachment payload.\n"u8), "typed authenticated attachment extraction");
        host.AddInput(QpdfPath.From("extra.bin"), "Added bytes"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfPath addedPrimary = QpdfPath.From("primary-added.pdf");
        QpdfAttachmentEditResult addedAttachments = await Qpdf.AddAttachmentsAsync(host, attachmentSource, addedPrimary,
            [new QpdfAttachmentSpec("extra.bin", "extra.bin", "extra.bin", null, null, null, null, false)],
            QpdfPassword.FromText("café•"), newProtection, QpdfLimits.Default, CancellationToken.None);
        Check(addedAttachments.AttachmentCount == 2 && host.TryGetOutput(addedPrimary, out _), "typed primary attachment addition and re-encryption");
        Check(host.TryGetOutput(addedPrimary, out byte[] addedPrimaryBytes), "primary addition bytes");
        QpdfPath addedPrimaryInput = QpdfPath.From("primary-added-input.pdf"); host.AddInput(addedPrimaryInput, addedPrimaryBytes, QpdfInputOwnership.Copy);
        QpdfAttachmentEditResult removedAttachments = await Qpdf.RemoveAttachmentsAsync(host, addedPrimaryInput, QpdfPath.From("primary-removed.pdf"), ["extra.bin"],
            QpdfPassword.FromText("writer-reader"), QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Check(removedAttachments.AttachmentCount == 1, "typed primary attachment removal");
        QpdfAttachmentEditResult copiedAttachments = await Qpdf.CopyAttachmentsAsync(host, addedPrimaryInput, QpdfPath.From("primary-copied.pdf"),
            [new(attachmentSource, "again-", QpdfPassword.FromText("café•"))], QpdfPassword.FromText("writer-owner"),
            QpdfOutputEncryption.Remove, QpdfLimits.Default, CancellationToken.None);
        Check(copiedAttachments.AttachmentCount == 3 && host.TryGetOutput(QpdfPath.From("primary-copied.pdf"), out _), "independent primary/source attachment credentials");
        QpdfPath creationJson = QpdfPath.From("creation.json");
        _ = await Qpdf.GetDocumentJsonAsync(host, plainInput, creationJson,
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true), CancellationToken.None);
        Check(host.TryGetOutput(creationJson, out byte[] creationBytes), "plaintext JSON for protected creation");
        QpdfPath creationInput = QpdfPath.From("creation-input.json"); host.AddInput(creationInput, creationBytes, QpdfInputOwnership.Copy);
        QpdfPath fromJson = QpdfPath.From("created-from-json.pdf");
        QpdfJsonImportResult createdJson = await Qpdf.CreatePdfFromJsonAsync(host, creationInput, fromJson, newProtection, QpdfLimits.Default, CancellationToken.None);
        Check(createdJson.PageCount == 1 && host.TryGetOutput(fromJson, out _), "protected JSON creation");
        Check(host.TryGetOutput(fromJson, out byte[] createdJsonBytes), "protected JSON creation bytes");
        QpdfPath reopenedJson = QpdfPath.From("reopened-json.pdf"); host.AddInput(reopenedJson, createdJsonBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, reopenedJson, QpdfPassword.FromText("writer-owner"), QpdfLimits.Default, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption is { Revision: 6, Permissions: -3388, EncryptMetadata: false }, "JSON creation password and metadata policy"); }
        foreach (bool jobCreate in new[] { false, true })
        {
            QpdfPath target = QpdfPath.From(jobCreate ? "job-created.pdf" : "argv-created.pdf");
            QpdfExecutionResult creation = jobCreate ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = creationInput.Value, outputFile = target.Value, jsonInput = "", copyEncryption = legacy.Value, encryptionFilePassword = "café•" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([creationInput.Value, target.Value, "--json-input", "--copy-encryption=" + legacy.Value, "--encryption-file-password=café•"]), CancellationToken.None);
            Check(creation.Status == QpdfExecutionStatus.Completed, "command JSON creation with template protection");
            Check(host.TryGetOutput(target, out byte[] creationOutput), "protected command creation bytes");
            QpdfPath reopened = QpdfPath.From("reopened-" + target.Value); host.AddInput(reopened, creationOutput, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened, QpdfPassword.FromBytes(Convert.FromHexString("6f776e6572e991")), QpdfLimits.Default, CancellationToken.None);
            Check(document.PageCount == 1 && document.Encryption is { Revision: 4, Permissions: -3388, EncryptMetadata: true }, "command JSON creation retains both template passwords");
        }
        QpdfPath fullJson = QpdfPath.From("full-encrypted-export.json");
        _ = await Qpdf.GetDocumentJsonAsync(host, encrypted, fullJson,
            new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], true) { Password = QpdfPassword.FromText("reader") }, CancellationToken.None);
        Check(host.TryGetOutput(fullJson, out byte[] fullJsonBytes), "full encrypted JSON export");
        QpdfPath fullJsonInput = QpdfPath.From("full-encrypted-input.json"); host.AddInput(fullJsonInput, fullJsonBytes, QpdfInputOwnership.Copy);
        foreach (bool createFromFull in new[] { false, true })
        {
            QpdfPath target = QpdfPath.From(createFromFull ? "full-created.pdf" : "full-updated.pdf");
            if (createFromFull) { _ = await Qpdf.CreatePdfFromJsonAsync(host, fullJsonInput, target, QpdfOutputEncryption.Remove, QpdfLimits.Default, CancellationToken.None); }
            else { _ = await Qpdf.UpdatePdfFromJsonAsync(host, encrypted, fullJsonInput, target, QpdfPassword.FromText("reader"), QpdfOutputEncryption.Preserve, CancellationToken.None); }
            Check(host.TryGetOutput(target, out byte[] fullOutput), "full encrypted JSON round trip");
            QpdfPath reopened = QpdfPath.From("reopened-" + target.Value); host.AddInput(reopened, fullOutput, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened,
                createFromFull ? null : QpdfPassword.FromText("fixture-owner"), QpdfLimits.Default, CancellationToken.None);
            Check(document.PageCount == 1 && (createFromFull ? document.Encryption is null : document.Encryption?.Revision == 6), "full JSON declared handler follows explicit policy");
        }
        QpdfPath created = QpdfPath.From("created.pdf");
        _ = await Qpdf.SelectPageAsync(host, plainInput, created, new PdfPageSelectionOptions(1), null,
            newProtection, QpdfLimits.Default, CancellationToken.None);
        Check(host.TryGetOutput(created, out byte[] protectedBytes), "new encryption publication");
        QpdfPath protectedInput = QpdfPath.From("protected-input.pdf"); host.AddInput(protectedInput, protectedBytes, QpdfInputOwnership.Copy);
        foreach (string credential in new[] { "writer-reader", "writer-owner" })
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, protectedInput,
                QpdfPassword.FromText(credential), QpdfLimits.Default, CancellationToken.None);
            Check(document.PageCount == 1 && document.Encryption?.Revision == 6
                && document.Encryption?.Permissions == -3388 && document.Encryption?.EncryptMetadata == false,
                "new protection passwords, permissions and clear metadata");
        }
        PdfMultiPageSelectionResult merged = await Qpdf.SelectPagesAsync(host,
            [new(legacy, new(1), QpdfPassword.FromText("café•")), new(encrypted, new(1), QpdfPassword.FromText("reader"))],
            QpdfPath.From("merged.pdf"), new PdfPageMergeOptions { SplitPages = 1, OutputEncryption = newProtection }, CancellationToken.None);
        Check(merged.PageCount == 2 && merged.OutputPaths.Count == 2, "independent source credentials and encrypted splitting");
        foreach (QpdfPath path in merged.OutputPaths)
        {
            Check(host.TryGetOutput(path, out byte[] output), "encrypted split publication");
            QpdfPath reopened = QpdfPath.From("reopened-" + path.Value); host.AddInput(reopened, output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reopened,
                QpdfPassword.FromText("writer-owner"), QpdfLimits.Default, CancellationToken.None);
            Check(document.PageCount == 1 && document.Encryption?.Revision == 6, "protected split consumer result");
        }
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
        QpdfExecutionResult exact = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            [legacy.Value, "--password=café•", "--suppress-password-recovery", "--show-npages"]), CancellationToken.None);
        Check(exact.Status == QpdfExecutionStatus.Failed && exact.Diagnostics.Single().Code == QpdfDiagnosticCode.InvalidPassword,
            "password recovery suppression honors the supplied UTF-8 bytes");
        QpdfExecutionResult hex = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            [legacy.Value, "--password=636166e980", "--password-mode=hex-bytes", "--show-npages"]), CancellationToken.None);
        Check(hex.Status == QpdfExecutionStatus.Completed && hex.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.PageCount),
            "hex password mode accepts raw PDFDocEncoding bytes");
        host.AddInput(QpdfPath.From("password"), "reader\nignored\n"u8.ToArray(), QpdfInputOwnership.Copy);
        QpdfExecutionResult fromFile = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(
            [encrypted.Value, "--password-file=password", "--show-npages"]), CancellationToken.None);
        Check(fromFile.Status == QpdfExecutionStatus.Completed && fromFile.ExitCode == QpdfExitCode.Success
            && fromFile.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.PasswordFileNote)
            && fromFile.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.PageCount), "host password file and non-secret multiline note");
        QpdfExecutionResult transformed = await Qpdf.ExecuteAsync(host, new(
            [encrypted.Value, "command-output.pdf", "--password-file=password", "--linearize", "--object-streams=generate"]), CancellationToken.None);
        Check(transformed.Status == QpdfExecutionStatus.Completed, "authenticated command transformation");
        Check(host.TryGetOutput(QpdfPath.From("command-output.pdf"), out byte[] commandBytes), "protected command publication");
        host.AddInput(QpdfPath.From("command-copy.pdf"), commandBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("command-copy.pdf"),
            QpdfPassword.FromBytes(ownerBytes), QpdfLimits.Default, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption?.Revision == 6, "command preservation with original owner password"); }
        QpdfExecutionResult decrypted = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"security-r6-ascii.pdf","outputFile":"decrypted-command.pdf","password":"reader","decrypt":""}""", CancellationToken.None);
        Check(decrypted.Status == QpdfExecutionStatus.Completed, "explicit command decryption");
        Check(host.TryGetOutput(QpdfPath.From("decrypted-command.pdf"), out byte[] decryptedBytes), "decrypted command publication");
        host.AddInput(QpdfPath.From("decrypted-command-copy.pdf"), decryptedBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("decrypted-command-copy.pdf"), CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption is null, "command removal policy"); }
        QpdfExecutionResult pageCredentials = await Qpdf.ExecuteAsync(host, new(["--empty", "page-passwords.pdf", "--pages",
            legacy.Value, "--password=café•", "1", encrypted.Value, "1", "--password=reader", "--"]), CancellationToken.None);
        Check(pageCredentials.Status == QpdfExecutionStatus.Completed, "independent command page credentials");
        Check(host.TryGetOutput(QpdfPath.From("page-passwords.pdf"), out byte[] pageCredentialBytes), "page credential publication");
        host.AddInput(QpdfPath.From("page-passwords-copy.pdf"), pageCredentialBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("page-passwords-copy.pdf"), CancellationToken.None))
        { Check(document.PageCount == 2 && document.Encryption is null, "empty-primary command merge remains plaintext"); }
        QpdfExecutionResult createdCommand = await Qpdf.ExecuteAsync(host, new(["decrypted-command-copy.pdf", "created-command.pdf",
            "--encrypt", "--user-password=consumer-reader", "--owner-password=consumer-owner", "--bits=256", "--modify=none", "--cleartext-metadata", "--"]), CancellationToken.None);
        Check(createdCommand.Status == QpdfExecutionStatus.Completed, "command AES-256 creation");
        Check(host.TryGetOutput(QpdfPath.From("created-command.pdf"), out byte[] createdBytes), "created command publication");
        host.AddInput(QpdfPath.From("created-command-copy.pdf"), createdBytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("created-command-copy.pdf"),
            QpdfPassword.FromText("consumer-owner"), QpdfLimits.Default, CancellationToken.None))
        { Check(document.PageCount == 1 && document.Encryption is { Revision: 6, Permissions: -1324, EncryptMetadata: false },
            "command new policy with requested permissions and metadata"); }
        QpdfExecutionResult report = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"security-r6-ascii.pdf","showEncryption":"","password":"consumer-secret","check":""}""", CancellationToken.None);
        Check(report.Status == QpdfExecutionStatus.Completed && report.ExitCode == QpdfExitCode.Success
            && report.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.EncryptionReport && item.Message == "R = 6")
            && report.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.CheckSkipped)
            && report.Diagnostics.All(item => !item.Message.Contains("consumer-secret", StringComparison.Ordinal)),
            "secret-free report with unauthenticated checks skipped");
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(host, encrypted, QpdfPath.From("security.json"),
            new QpdfJsonOptions(2, ["encrypt"]) { Password = QpdfPassword.FromBytes(ownerBytes) }, CancellationToken.None);
        using (JsonDocument json = JsonDocument.Parse(exported.Json))
        {
            JsonElement facts = json.RootElement.GetProperty("encrypt");
            Check(facts.GetProperty("encrypted").GetBoolean() && facts.GetProperty("ownerpasswordmatched").GetBoolean()
                && facts.GetProperty("recovereduserpassword").ValueKind == JsonValueKind.Null
                && facts.GetProperty("parameters").GetProperty("key").ValueKind == JsonValueKind.Null, "authenticated non-secret JSON facts");
        }
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
