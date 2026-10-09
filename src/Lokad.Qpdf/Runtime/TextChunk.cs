namespace Lokad.Qpdf.Runtime;

/// <summary>One admitted piece of temporary display text, never pooled or published.</summary>
internal class TextChunk
{
    internal TextChunk(int capacity)
    {
        Buffer = new char[capacity];
    }

    protected TextChunk() { Buffer = Array.Empty<char>(); }

    internal char[] Buffer { get; }

    internal int Used { get; set; }

    internal TextChunk? Next { get; set; }

    internal virtual int Capacity => Buffer.Length;

    internal virtual bool Accepts(char value) => true;

    internal virtual void Append(char value) { Buffer[Used++] = value; }

    internal virtual void CopyTo(int offset, Span<char> target)
        => Buffer.AsSpan(offset, target.Length).CopyTo(target);
}
