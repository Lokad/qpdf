using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentJsonNameKeysTests
{
    [Theory]
    [InlineData(1, false, "typed")]
    [InlineData(1, true, "argv")]
    [InlineData(2, false, "job")]
    [InlineData(2, true, "typed")]
    public async Task FilenameAndStreamMapsUsePdfNameKeys(int version, bool indirect, string route)
    {
        string[] keys = ["F", "UF", "Unix", "DOS", "Mac"];
        string names = string.Join(' ', keys.Select((key, index) => $"/{key} " + (indirect ? $"{index + 6} 0 R" : $"({key}.txt)")));
        string streams = string.Join(' ', keys.Select(key => $"/{key} 5 0 R"));
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 4 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, $"<< /Type /Filespec {names} /EF << {streams} >> >>", null),
            (5, "<< /Type /EmbeddedFile /Length 1 >>", "X"u8.ToArray())];
        for (int index = 0; index < keys.Length; index++) { objects.Add((index + 6, $"({keys[index]}.txt)", null)); }
        byte[] input = PdfTestDocument.Assemble(objects, 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        using JsonDocument json = JsonDocument.Parse(await AttachmentJsonTests.ExportAttachmentsAsync(host, version, route));
        JsonElement attachment = json.RootElement.GetProperty("attachments").GetProperty("K");
        foreach (string map in new[] { "names", "streams" })
        { Assert.Equal(keys.Select(key => "/" + key).Order(), attachment.GetProperty(map).EnumerateObject().Select(item => item.Name).Order()); }
        foreach (string key in keys) { Assert.Equal(key + ".txt", attachment.GetProperty("names").GetProperty("/" + key).GetString()); }
        Assert.Equal("UF.txt", attachment.GetProperty("preferredname").GetString());
    }
}
