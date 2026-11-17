using Lokad.Qpdf.Hosting;
using Microsoft.Win32.SafeHandles;

namespace Lokad.Qpdf.Probe;

internal static class ProbeInputFiles
{
    internal static Dictionary<string, string> Index(string workdir)
    {
        if (!Directory.Exists(workdir)) { throw new ProbeException($"Workdir not found: {workdir}.", 1); }
        string inputs = Path.Combine(workdir, "inputs");
        if (!Directory.Exists(inputs)) { throw new ProbeException($"Inputs directory not found: {inputs}.", 1); }
        Dictionary<string, string> available = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(inputs, "*", SearchOption.AllDirectories))
        { available.Add("inputs/" + Path.GetRelativePath(inputs, file).Replace(Path.DirectorySeparatorChar, '/'), file); }
        return available;
    }

    // ./ aliases borrow the same canonical lease. Physical reads occur only
    // when the engine requests an input; each fresh array transfers once.
    internal sealed class Requested(InMemoryQpdfHost host, Dictionary<string, string> available) : IQpdfHost, IQpdfJpegCodecHost
    {
        public IQpdfJpegCodec? JpegCodec => host.JpegCodec;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string name = path.Value;
            while (name.StartsWith("./", StringComparison.Ordinal)) { name = name[2..]; }
            if (available.Remove(name, out string? file) && file is not null)
            { host.AddInput(QpdfPath.From(name), ReadInputBytes(file), QpdfInputOwnership.Transfer); }
            return host.OpenInputAsync(QpdfPath.From(name), token);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => host.CreateOutputAsync(path, token);
    }
    internal static byte[] ReadInputBytes(string path)
    {
        FileOptions options = OperatingSystem.IsWindows() ? FileOptions.SequentialScan : FileOptions.None;
        using SafeFileHandle input = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, options);
        long length;
        try { length = RandomAccess.GetLength(input); }
        catch (NotSupportedException) { return ReadUnknownInputBytes(input); }
        if (length == 0L) { return ReadUnknownInputBytes(input); }
        if (length > Array.MaxLength) { throw new IOException("Input exceeds the supported buffer size."); }
        // Fill every byte before ownership transfers; premature EOF fails here.
        // The complete fresh file read remains inside each measured public job.
        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);
        int filled = 0;
        while (filled < bytes.Length)
        {
            int read = RandomAccess.Read(input, bytes.AsSpan(filled), filled);
            if (read == 0) { throw new EndOfStreamException("Input ended before its declared length."); }
            filled += read;
        }
        return bytes;
    }

    private static byte[] ReadUnknownInputBytes(SafeFileHandle input)
    {
        // An unknown length, including procfs, need not mean an empty file.
        using FileStream stream = new(input, FileAccess.Read);
        using MemoryStream storage = new();
        stream.CopyTo(storage);
        return storage.ToArray();
    }

}
