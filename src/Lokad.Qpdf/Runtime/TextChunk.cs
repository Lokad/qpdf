namespace Lokad.Qpdf.Runtime;

/// <summary>One admitted piece of temporary display text, never pooled or published.</summary>
internal sealed class TextChunk
{
    internal TextChunk(int capacity)
    {
        Buffer = new char[capacity];
    }

    internal char[] Buffer { get; }

    internal int Used { get; set; }

    internal TextChunk? Next { get; set; }
}
