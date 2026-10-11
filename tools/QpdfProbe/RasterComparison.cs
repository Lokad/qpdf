using System.Globalization;

namespace Lokad.Qpdf.Probe;

// Developer-only comparison of bounded P6 rasters emitted by pdftoppm.
internal static class RasterComparison
{
    private sealed record Raster(byte[] Bytes, int Offset, int Width, int Height);

    internal static int Run(string[] args)
    {
        if (args.Length != 14) { throw new ProbeException("compare-raster requires seven named options.", 1); }
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!options.TryAdd(args[i], args[i + 1])) { throw new ProbeException("Duplicate raster option.", 1); }
        }
        string Option(string name) => options.TryGetValue(name, out string? value) && value is not null
            ? value : throw new ProbeException("Missing raster option " + name, 1);
        double meanLimit = double.Parse(Option("--max-mean-error"), CultureInfo.InvariantCulture);
        double changedLimit = double.Parse(Option("--max-changed-fraction"), CultureInfo.InvariantCulture);
        int threshold = int.Parse(Option("--channel-threshold"), CultureInfo.InvariantCulture);
        int width = int.Parse(Option("--expected-width"), CultureInfo.InvariantCulture);
        int height = int.Parse(Option("--expected-height"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(meanLimit) || !double.IsFinite(changedLimit)
            || meanLimit < 0 || meanLimit > 1 || changedLimit < 0 || changedLimit > 1 || threshold < 0 || threshold > 255
            || width < 1 || width > 4096 || height < 1 || height > 4096)
        { throw new ProbeException("Invalid raster tolerance.", 1); }
        Raster left = Read(Option("--left"));
        Raster right = Read(Option("--right"));
        if (left.Width != width || right.Width != width || left.Height != height || right.Height != height)
        { Console.Error.WriteLine("raster: dimensions differ from the expected crop"); return 2; }
        long absolute = 0;
        int changed = 0;
        int visible = 0;
        int pixels = checked(left.Width * left.Height);
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            bool differs = false;
            bool ink = false;
            for (int channel = 0; channel < 3; channel++)
            {
                int at = pixel * 3 + channel;
                int delta = Math.Abs(left.Bytes[left.Offset + at] - right.Bytes[right.Offset + at]);
                absolute += delta;
                differs |= delta > threshold;
                ink |= right.Bytes[right.Offset + at] < 250;
            }
            if (differs) { changed++; }
            if (ink) { visible++; }
        }
        if (visible == 0) { throw new ProbeException("Reference crop is blank; it cannot qualify a visual feature.", 1); }
        double mean = absolute / (pixels * 3.0 * 255.0);
        double fraction = changed / (double)pixels;
        Console.WriteLine(FormattableString.Invariant($"raster: width={left.Width} height={left.Height} mean-error={mean:F6} changed-fraction={fraction:F6}"));
        return mean <= meanLimit && fraction <= changedLimit ? 0 : 2;
    }

    private static Raster Read(string path)
    {
        if (new FileInfo(path).Length > 64L * 1024L * 1024L) { throw new ProbeException("Raster exceeds 64 MiB.", 1); }
        byte[] bytes = File.ReadAllBytes(path);
        int at = 0;
        string Token()
        {
            while (at < bytes.Length && at < 1024)
            {
                if (bytes[at] == '#') { while (at < bytes.Length && at < 1024 && bytes[at] != '\n') { at++; } }
                else if (bytes[at] <= 32) { at++; }
                else { break; }
            }
            int start = at;
            while (at < bytes.Length && at < 1024 && bytes[at] > 32) { at++; }
            if (at >= 1024 || at == start) { throw new ProbeException("Invalid bounded PPM header.", 1); }
            return System.Text.Encoding.ASCII.GetString(bytes, start, at - start);
        }
        if (Token() != "P6") { throw new ProbeException("Only binary RGB P6 rasters are accepted.", 1); }
        int width = int.Parse(Token(), CultureInfo.InvariantCulture);
        int height = int.Parse(Token(), CultureInfo.InvariantCulture);
        if (Token() != "255" || width < 1 || width > 4096 || height < 1 || height > 4096
            || at >= bytes.Length || bytes[at] > 32)
        { throw new ProbeException("Invalid PPM dimensions/range.", 1); }
        if (bytes[at++] == '\r' && at < bytes.Length && bytes[at] == '\n') { at++; }
        if (bytes.Length - at != checked(width * height * 3)) { throw new ProbeException("PPM payload length mismatch.", 1); }
        return new(bytes, at, width, height);
    }
}
