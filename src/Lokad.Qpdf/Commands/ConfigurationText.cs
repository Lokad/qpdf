using System.Collections;
using System.Text.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>Owns copied configuration text through parsing and execution.</summary>
internal sealed class ConfigurationText : IDisposable
{
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private long _jsonCopies;
    internal CollationPreparation? Collation { get; set; }

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

    internal ResourceGovernor Governor => _governor;
    internal ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ConfigurationText));

    // Small documents use a cheap blanket admission split: this part funds
    // decoded copies, and the rest funds encoded/DOM storage. Large documents
    // derive their aggregate copy credit from token/string density instead.
    // A nonempty JSON string needs at most two bytes per encoded byte plus its
    // header. Sixteen bytes per input byte covers both character storage and
    // headers even for arrays of one-character strings. Accepted copies consume
    // this pool and stay funded by their configuration or execution owner.
    internal static long JsonCopyBytes(long inputBytes) => checked(inputBytes * 16L + 2048L);

    internal void AdmitJsonCopies(long bytes)
    {
        long total = checked(_jsonCopies + bytes);
        Memory.Reserve(bytes);
        _jsonCopies = total;
    }

    // Parsing has consumed the pool. Keep accepted strings under this same
    // component owner; refund only unused prepaid capacity.
    internal void CompleteJsonCopies()
    {
        long unused = _jsonCopies;
        _jsonCopies = 0L;
        Memory.Release(unused);
    }

    internal string? ReadJsonString(JsonElement element)
    {
        _governor.CheckCancellation();
        if (element.ValueKind == JsonValueKind.Null) { return null; }
        if (element.ValueKind == JsonValueKind.String && element.ValueEquals(string.Empty)) { return string.Empty; }
        string? value = element.GetString();
        if (value is null) { return null; }
        long bytes = GovernedText.StringBytes(value.Length);
        if (bytes > _jsonCopies)
        { throw new InvalidOperationException("Decoded configuration text exceeds its prepaid copy storage."); }
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
            _governor.CheckCancellation();
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

    internal static ConfigurationText? OwnerOf(IReadOnlyList<string> arguments)
        => (arguments as Arguments)?.Owner;

    internal static string Slice(ConfigurationText? owner, string source, int start, int length)
        => owner is null ? source.Substring(start, length) : owner.CopySubstring(source, start, length);

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

    // The standalone parser returns a plan without a component owner. Preserve
    // only its materialized text under the enclosing execution, not unused
    // prepaid JSON capacity or the configuration controller itself.
    internal void RetainForExecution()
        => Memory.RetainPrivateStorage(checked(Memory.ReservedBytes - _jsonCopies - 64L));

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null;
        _jsonCopies = 0L;
        memory?.Dispose();
    }
}
