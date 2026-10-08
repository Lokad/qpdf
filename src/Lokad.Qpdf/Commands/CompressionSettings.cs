namespace Lokad.Qpdf.Commands;

/// <summary>Writer compression setters, applied in configuration order.</summary>
internal readonly struct CompressionSettings
{
    private const byte Initialized = 1;
    private const byte Compress = 2;
    private const byte CompressSet = 4;
    private const byte DecodeSet = 8;
    private const int DecodeShift = 4;
    private readonly byte _state;

    private CompressionSettings(byte state) => _state = state;

    internal static CompressionSettings NativeDefault => new(Initialized | Compress | (1 << DecodeShift));

    internal byte State => _state;

    internal static CompressionSettings FromState(byte state) => new(state);

    internal bool IsInitialized => (_state & Initialized) != 0;

    internal string CompressStreams => (_state & Compress) != 0 ? "y" : "n";

    internal string DecodeLevel => (_state >> DecodeShift) switch
    {
        0 => "none",
        1 => "generalized",
        2 => "specialized",
        _ => "all",
    };

    internal CompressionSettings WithStreamData(string value) => value switch
    {
        "preserve" => WithDecode(0).WithCompress(false),
        "compress" => WithDecode(Math.Max(1, _state >> DecodeShift)).WithCompress(true),
        "uncompress" => WithDecode(Math.Max(1, _state >> DecodeShift)).WithCompress(false),
        _ => throw new QpdfParseException("--stream-data must be compress, preserve, or uncompress.", 0L, null),
    };

    internal CompressionSettings WithCompressStreams(string value) => value switch
    {
        "y" => WithCompress(true),
        "n" => WithCompress(false),
        _ => throw new QpdfParseException("--compress-streams must be y or n.", 0L, null),
    };

    internal CompressionSettings WithDecodeLevel(string value) => value switch
    {
        "none" => WithDecode(0),
        "generalized" => WithDecode(1),
        "specialized" => WithDecode(2),
        "all" => WithDecode(3),
        _ => throw new QpdfParseException("--decode-level must be none, generalized, specialized, or all.", 0L, null),
    };

    internal CompressionSettings WithQdf()
    {
        CompressionSettings result = this;
        if ((_state & CompressSet) == 0) { result = result.WithCompress(false); }
        if ((_state & DecodeSet) == 0) { result = result.WithDecode(1); }
        return result;
    }

    private CompressionSettings WithCompress(bool value)
        => new((byte)((_state & ~Compress) | Initialized | CompressSet | (value ? Compress : 0)));

    private CompressionSettings WithDecode(int value)
        => new((byte)((_state & 15) | Initialized | DecodeSet | (value << DecodeShift)));
}
