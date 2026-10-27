using Lokad.Qpdf.Image;
using Lokad.Qpdf.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Lokad.Qpdf.Probe;

// Additional development comparison: the public typed selector reads a fresh
// file-backed host. The original complete memory-host emit jobs stay separate.
internal static class FileSelectionCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2) { throw new ProbeException("select-file needs input and output file paths.", 1); }
        InMemoryQpdfHost outputs = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        FileHost host = new(outputs, new Dictionary<string, string>(StringComparer.Ordinal)
            { ["input.pdf"] = Path.GetFullPath(args[0]) });
        QpdfPath output = QpdfPath.From("output.pdf");
        Qpdf.SelectPageAsync(host, QpdfPath.From("input.pdf"), output,
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None).GetAwaiter().GetResult();
        if (!outputs.TryGetOutput(output, out byte[] bytes))
        { throw new ProbeException("Typed file selection did not publish output.", 1); }
        File.WriteAllBytes(args[1], bytes);
        return 0;
    }

    internal sealed class FileHost(InMemoryQpdfHost outputs, IReadOnlyDictionary<string, string> inputs) : IQpdfHost, IQpdfJpegCodecHost
    {
        public IQpdfJpegCodec? JpegCodec => outputs.JpegCodec;

        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string name = path.Value;
            while (name.StartsWith("./", StringComparison.Ordinal)) { name = name[2..]; }
            if (!inputs.TryGetValue(name, out string? input)) { throw new FileNotFoundException("Input is unavailable.", name); }
            SafeFileHandle handle = File.OpenHandle(input, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            try { return new(new FileInput(path, input, handle, RandomAccess.GetLength(handle))); }
            catch { handle.Dispose(); throw; }
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => outputs.CreateOutputAsync(path, token);
    }

    private sealed class FileInput(QpdfPath path, string identity, SafeFileHandle handle, long length) : IQpdfInput
    {
        public QpdfPath Path => path;
        public string Identity => identity;
        public long Length => length;
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
        {
            if (offset < 0L || count < 0 || count > Length - offset) { throw new ArgumentOutOfRangeException(nameof(count)); }
            byte[] bytes = GC.AllocateUninitializedArray<byte>(count);
            int read = await RandomAccess.ReadAsync(handle, bytes, offset, token).ConfigureAwait(false);
            return bytes.AsMemory(0, read);
        }
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
    }
}
