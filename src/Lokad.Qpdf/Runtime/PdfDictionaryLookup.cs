using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Borrowed name lookup with explicit work/cancellation and no execution state in PDF values.</summary>
internal static class PdfDictionaryLookup
{
    internal static bool TryGet(PdfDictionary dictionary, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken token, out QpdfObject value)
        => Lookup(dictionary, key, governor, token, null, out value);

    internal static bool TryGetObserved(PdfDictionary dictionary, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken token, Action observe, out QpdfObject value)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return Lookup(dictionary, key, governor, token, observe, out value);
    }

    private static bool Lookup(PdfDictionary dictionary, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken token, Action? observe, out QpdfObject value)
    {
        ArgumentNullException.ThrowIfNull(dictionary); ArgumentNullException.ThrowIfNull(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (dictionary.AllocationCarrier is PdfNameTable<QpdfObject> names)
        {
            bool found = observe is null ? names.TryGetValue(key, governor, token, out QpdfObject? candidate)
                : names.TryGetObserved(key, governor, token, observe, out candidate);
            if (found && candidate is not null) { value = candidate; return true; }
        }
        else
        {
            // Legacy backing is borrowed through the concrete enumerator. A
            // comparer cannot observe every BCL bucket visit, so scan entries
            // explicitly without allocating a temporary name or boxed view.
            using var entries = dictionary.GetEntryEnumerator();
            while (true)
            {
                Step(governor, token);
                if (!entries.MoveNext()) { break; }
                if (Equal(entries.Current.Key.RawSpan, key, governor, token, observe))
                {
                    if (entries.Current.Value is QpdfObject candidate) { value = candidate; return true; }
                    break;
                }
            }
        }
        value = PdfNull.Instance; return false;
    }

    internal static bool NameEquals(PdfName name, ReadOnlySpan<byte> expected,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(governor);
        Step(governor, token);
        return Equal(name.RawSpan, expected, governor, token, null);
    }

    private static bool Equal(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right,
        ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        if (left.Length != right.Length) { return false; }
        for (int index = 0; index < left.Length; index++)
        {
            Step(governor, token);
            if (left[index] != right[index]) { return false; }
            if ((index + 1) % 256 == 0) { observe?.Invoke(); }
        }
        return true;
    }

    private static void Step(ResourceGovernor governor, CancellationToken token)
    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L); }
}
