using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

/// <summary>Compares borrowed dictionary values with an admitted iterative stack.</summary>
internal sealed class PdfObjectComparison : IDisposable
{
    private enum Kind : byte { Value, Array, Dictionary, Stream }
    private struct Frame(QpdfObject? left, QpdfObject? right, bool referenceShortcut)
    {
        internal QpdfObject? Left = left;
        internal QpdfObject? Right = right;
        internal bool ReferenceShortcut = referenceShortcut;
        internal Kind Kind;
        internal int Index;
        internal PdfDictionary.EntryEnumerator Entries;
    }

    // Includes the concrete dictionary cursor and both borrowed values.
    private const long FrameBytes = 128L;
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<Frame>? _frames;
    private CancellationToken _token;

    private PdfObjectComparison(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _frames = ParserContainers.CreateList<Frame>(memory); }

    internal static PdfObjectComparison Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(PdfObjectComparison));
    private List<Frame> Frames => _frames ?? throw new ObjectDisposedException(nameof(PdfObjectComparison));
    private void Visit() { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L); }

    internal int Hash(PdfDictionary dictionary) { Visit(); return dictionary.Count; }

    private void Push(QpdfObject? left, QpdfObject? right, bool referenceShortcut)
    {
        Visit(); List<Frame> frames = Frames;
        if (frames.Count == frames.Capacity)
        {
            Governor.ChargeWork(checked(frames.Count + Math.Min(Array.MaxLength, Math.Max(4L, frames.Capacity * 2L))));
            _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        }
        ParserContainers.AddItem(_memory ?? throw new ObjectDisposedException(nameof(PdfObjectComparison)),
            frames, new Frame(left, right, referenceShortcut), FrameBytes);
    }

    internal bool AreEqual(PdfDictionary left, PdfDictionary right)
    {
        List<Frame> frames = Frames;
        try
        {
            Push(left, right, false);
            while (frames.Count != 0)
            {
                Visit(); int at = frames.Count - 1; Frame frame = frames[at];
                if (frame.Kind == Kind.Array)
                {
                    PdfArray array = (PdfArray)(frame.Left ?? throw new InvalidOperationException());
                    PdfArray other = (PdfArray)(frame.Right ?? throw new InvalidOperationException());
                    if (frame.Index == array.Count) { frames.RemoveAt(at); continue; }
                    int item = frame.Index++; frames[at] = frame;
                    // Span.SequenceEqual uses EqualityComparer<QpdfObject>,
                    // whose virtual equality does not bypass equal references.
                    Push(array[item], other[item], false); continue;
                }
                if (frame.Kind == Kind.Dictionary)
                {
                    if (!frame.Entries.MoveNext(Governor, _token))
                    { frame.Entries.Dispose(); frames.RemoveAt(at); continue; }
                    frames[at] = frame;
                    PdfDictionary other = (PdfDictionary)(frame.Right ?? throw new InvalidOperationException());
                    KeyValuePair<PdfName, QpdfObject> entry = frame.Entries.Current;
                    if (!PdfDictionaryLookup.TryGet(other, entry.Key.RawSpan, Governor, _token, out QpdfObject value)) { return false; }
                    // Dictionary.Equals uses object.Equals for its values.
                    Push(entry.Value, value, true); continue;
                }
                if (frame.Kind == Kind.Stream)
                {
                    PdfStream stream = (PdfStream)(frame.Left ?? throw new InvalidOperationException());
                    PdfStream other = (PdfStream)(frame.Right ?? throw new InvalidOperationException());
                    if (!BytesEqual(stream.RawMemory.Span, other.RawMemory.Span)) { return false; }
                    frames.RemoveAt(at); continue;
                }
                QpdfObject? valueLeft = frame.Left, valueRight = frame.Right;
                if (frame.ReferenceShortcut && ReferenceEquals(valueLeft, valueRight)) { frames.RemoveAt(at); continue; }
                if (valueLeft is null || valueRight is null)
                { if (valueLeft is not null || valueRight is not null) { return false; } frames.RemoveAt(at); continue; }
                if (valueLeft.GetType() != valueRight.GetType()) { return false; }
                switch (valueLeft)
                {
                    case PdfNull: break;
                    case PdfBoolean boolean: if (boolean.Value != ((PdfBoolean)valueRight).Value) { return false; } break;
                    case PdfInteger integer:
                        PdfInteger otherInteger = (PdfInteger)valueRight;
                        if (!TextEqual(integer.Text, otherInteger.Text) || integer.Value != otherInteger.Value) { return false; } break;
                    case PdfReal real:
                        PdfReal otherReal = (PdfReal)valueRight;
                        if (!TextEqual(real.Text, otherReal.Text) || !real.Value.Equals(otherReal.Value)) { return false; } break;
                    case PdfName name: if (!BytesEqual(name.RawSpan, ((PdfName)valueRight).RawSpan)) { return false; } break;
                    case PdfByteString bytes:
                        PdfByteString otherBytes = (PdfByteString)valueRight;
                        if (bytes.Hex != otherBytes.Hex || !BytesEqual(bytes.ValueSpan, otherBytes.ValueSpan)) { return false; } break;
                    case PdfReference reference:
                        PdfReference otherReference = (PdfReference)valueRight;
                        if (reference.ObjectNumber != otherReference.ObjectNumber || reference.Generation != otherReference.Generation) { return false; } break;
                    case PdfArray array:
                        if (array.Count != ((PdfArray)valueRight).Count) { return false; }
                        frame.Kind = Kind.Array; frames[at] = frame; continue;
                    case PdfDictionary dictionary:
                        if (dictionary.Count != ((PdfDictionary)valueRight).Count) { return false; }
                        frame.Kind = Kind.Dictionary; frame.Entries = dictionary.GetEntryEnumerator(); frames[at] = frame; continue;
                    case PdfStream stream:
                        frame.Kind = Kind.Stream; frames[at] = frame;
                        Push(stream.Dictionary, ((PdfStream)valueRight).Dictionary, false); continue;
                    default: throw new QpdfParseException("Unsupported PDF value in resource-scope comparison.", null, null);
                }
                frames.RemoveAt(at);
            }
            return true;
        }
        finally
        {
            try { Governor.ChargeWork(frames.Count); }
            finally
            {
                foreach (Frame frame in frames) { if (frame.Kind == Kind.Dictionary) { frame.Entries.Dispose(); } }
                frames.Clear();
            }
        }
    }

    private bool BytesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length) { return false; }
        for (int index = 0; index < left.Length; index++) { Visit(); if (left[index] != right[index]) { return false; } }
        return true;
    }
    private bool TextEqual(string left, string right)
    {
        if (ReferenceEquals(left, right)) { return true; }
        if (left.Length != right.Length) { return false; }
        for (int index = 0; index < left.Length; index++) { Visit(); if (left[index] != right[index]) { return false; } }
        return true;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _frames = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
