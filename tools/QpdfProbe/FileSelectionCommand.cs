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
            SafeFileHandle handle = File.OpenHandle(input, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
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
        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (offset < 0 || offset > Length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            if (destination.IsEmpty) { return new(0); }
            return RandomAccess.ReadAsync(handle, destination[..(int)Math.Min(destination.Length, Length - offset)], offset, token);
        }
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
    }
}
