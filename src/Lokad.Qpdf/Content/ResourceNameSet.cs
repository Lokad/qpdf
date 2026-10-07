using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Owns decoded pruning names in the shared governed name table.</summary>
internal sealed class ResourceNameSet : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private PdfNameTable<byte>? _names;
    private CancellationToken _token;

    private ResourceNameSet(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _names = PdfNameTable<byte>.Create(memory, governor, sizeof(byte), token); }
    internal static ResourceNameSet Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ResourceNameSet));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ResourceNameSet));
    private PdfNameTable<byte> Names => _names ?? throw new ObjectDisposedException(nameof(ResourceNameSet));
    internal int Count => Names.Count;
    internal PdfNameTable<byte>.Enumerator GetEnumerator() => Names.GetEnumerator();
    internal bool AddRaw(ReadOnlySpan<byte> raw) => Add(PdfNameBytes.Decode(raw, Governor, _token));
    internal bool AddDecoded(ReadOnlySpan<byte> decoded) => Add(new PdfNameBytes(decoded));
    internal bool ContainsRaw(ReadOnlySpan<byte> raw) => Contains(PdfNameBytes.Decode(raw, Governor, _token));
    internal bool ContainsDecoded(ReadOnlySpan<byte> decoded) => Contains(new PdfNameBytes(decoded));
    private bool Contains(PdfNameBytes name) => Names.TryGetValue(name, Governor, _token, out _);
    private bool Add(PdfNameBytes name)
    {
        ResourceGovernor governor = Governor; ParserMemoryScope memory = Memory;
        _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (Names.TryGetValue(name, governor, _token, out _)) { return false; }
        long bytes = checked(64L + name.Length); // Name wrapper and one immutable byte array.
        memory.Reserve(bytes);
        try
        {
            byte[] raw = new byte[name.Length]; name.CopyTo(raw, governor, _token);
            PdfName key = PdfName.FromOwnedBytes(raw);
            if (Names.TryAdd(memory, governor, key, 0, _token)) { return true; }
        }
        catch { memory.Release(bytes); throw; }
        memory.Release(bytes); return false;
    }
    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _names = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
