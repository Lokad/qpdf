using System.Collections;
using System.Text.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>Transfers admitted configuration strings to their independently escaping aliases.</summary>
internal sealed class ConfigurationText : IDisposable
{
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private long _jsonCopies;

    private ConfigurationText(ResourceGovernor governor, ParserMemoryScope memory)
    { _governor = governor; _memory = memory; }

    internal static ConfigurationText Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            return new ConfigurationText(governor, memory);
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ConfigurationText));

    // The loader splits its already established document admission: this part
    // funds decoded copies, and the rest continues to fund encoded/DOM storage.
    // A nonempty JSON string needs at most two bytes per encoded byte plus its
    // header. Sixteen bytes per input byte covers both character storage and
    // headers even for arrays of one-character strings. Copies leave this pool
    // when accepted; independently escaping receipts keep that same admission.
    internal static long JsonCopyBytes(long inputBytes) => checked(inputBytes * 16L + 2048L);

    internal void AdmitJsonCopies(long bytes)
    {
        long total = checked(_jsonCopies + bytes);
        Memory.Reserve(bytes);
        _jsonCopies = total;
    }

    internal string? ReadJsonString(JsonElement element)
    {
        _governor.CheckCancellation();
        _governor.ChargeWork(1L);
        if (element.ValueKind == JsonValueKind.Null) { return null; }
        if (element.ValueKind == JsonValueKind.String && element.ValueEquals(string.Empty)) { return string.Empty; }
        string? value = element.GetString();
        if (value is null) { return null; }
        long bytes = GovernedText.StringBytes(value.Length);
        if (bytes > _jsonCopies)
        { throw new InvalidOperationException("Decoded configuration text exceeds its prepaid copy storage."); }
        Memory.RetainAllocation(value, bytes);
        _jsonCopies -= bytes;
        return value;
    }

    private string CopySubstring(string source, int start, int length)
    {
        if (start == 0 && length == source.Length) { return source; }
        // These fixed syntax values already have shared literal storage. Do not
        // allocate a substring/receipt when the parser can borrow that storage.
        if (length <= 11)
        {
            _governor.CheckCancellation(); _governor.ChargeWork(1L);
            string? shared = source.AsSpan(start, length) switch
            {
                "1" => "1", "2" => "2", "inline" => "inline", "file" => "file",
                "none" => "none", "all" => "all", "generalized" => "generalized",
                "specialized" => "specialized", "preserve" => "preserve",
                "compress" => "compress", "uncompress" => "uncompress",
                "auto" => "auto", "disable" => "disable", "generate" => "generate",
                "y" => "y", "n" => "n", "latest" => "latest", "qpdf" => "qpdf",
                "pages" => "pages", "attachments" => "attachments", "acroform" => "acroform",
                "pagelabels" => "pagelabels", "outlines" => "outlines", "objects" => "objects",
                _ => null,
            };
            if (shared is not null) { return shared; }
        }
        return GovernedText.CopySubstring(_governor, Memory, source, start, length);
    }

    internal static IReadOnlyList<string> Wrap(IReadOnlyList<string> values, ConfigurationText? owner)
    {
        if (owner is null) { return values; }
        owner.Memory.Reserve(40L);
        return new Arguments(values, owner);
    }

    internal static IReadOnlyList<string> Preserve(IReadOnlyList<string> original, IReadOnlyList<string> values)
        => Wrap(values, (original as Arguments)?.Owner);

    internal static string Slice(IReadOnlyList<string> arguments, string source, int start)
        => Slice(arguments, source, start, source.Length - start);

    internal static string Slice(IReadOnlyList<string> arguments, string source, int start, int length)
        => arguments is Arguments values
            ? values.Owner.CopySubstring(source, start, length) : source.Substring(start, length);

    private sealed class Arguments(IReadOnlyList<string> values, ConfigurationText owner) : IReadOnlyList<string>
    {
        internal ConfigurationText Owner => owner;
        public int Count => values.Count;
        public string this[int index] => values[index];
        public IEnumerator<string> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null;
        _jsonCopies = 0L;
        memory?.Dispose();
    }
}
