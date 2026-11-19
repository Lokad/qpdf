using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class AcroFormCopy
{
    private static readonly PdfName FieldKidsKey = PdfName.FromAscii("Kids");

    private static PdfArray? ReadFieldKids(PdfDictionary field, IIndirectResolver resolver)
    {
        if (!field.TryGet(FieldKidsKey, out QpdfObject raw)) { return null; }
        return raw is PdfReference reference
            ? resolver.TryResolve(reference, out QpdfObject? value) ? value as PdfArray : null
            : raw as PdfArray;
    }

    [Flags]
    private enum MemberStatus : byte
    {
        None = 0,
        HasWidget = 1,
        HasSurvivor = 2,
    }

    // Summaries are limited to one immutable pool and one set of retained widgets.
    private sealed class FieldMembership
    {
        private readonly record struct Summary(MemberStatus Status, int Height);

        private readonly IIndirectResolver _resolver;
        private readonly HashSet<int> _survivors;
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private readonly ParserMemoryScope _scratch;
        private readonly CancellationToken _token;
        private readonly Dictionary<(int Number, int Generation), Summary> _acyclic;
        private readonly HashSet<int> _active;
        private Dictionary<(int Number, int Generation), MemberStatus>? _replayed;

        private FieldMembership(IIndirectResolver resolver, HashSet<int> survivors, QpdfLimits limits,
            ResourceGovernor governor, ParserMemoryScope scratch, CancellationToken token)
        {
            _resolver = resolver;
            _survivors = survivors;
            _limits = limits;
            _governor = governor;
            _scratch = scratch;
            _token = token;
            _acyclic = ParserContainers.CreateDictionary<(int, int), Summary>(scratch);
            _active = ParserContainers.CreateSet<int>(scratch);
        }

        internal static FieldMembership Create(IIndirectResolver resolver, HashSet<int> survivors, QpdfLimits limits,
            ResourceGovernor governor, ParserMemoryScope scratch, CancellationToken token)
        {
            scratch.Reserve(88L);
            return new(resolver, survivors, limits, governor, scratch, token);
        }

        internal bool Keeps(PdfReference reference, PdfDictionary field, bool prune)
        {
            var key = (reference.ObjectNumber, reference.Generation);
            MemberStatus status;
            if (_replayed is not null && _replayed.TryGetValue(key, out status))
            {
                return Keeps(status, prune);
            }
            if (Summarize(reference, field, 0) is Summary summary)
            {
                return Keeps(summary.Status, prune);
            }

            // Cyclic or depth-sensitive paths retain the existing root walk's
            // visit order and limits. Its decision is reusable for that root.
            using ParserMemoryScope traversal = ParserMemoryScope.Create(_governor);
            HashSet<int> members = WalkFieldMembers(_resolver, field, key.ObjectNumber, _limits, _governor, traversal, _token);
            status = MemberStatus.None;
            foreach (int member in members)
            {
                Check();
                status |= WidgetStatus(member);
                if ((status & MemberStatus.HasSurvivor) != 0) { break; }
            }
            _replayed ??= ParserContainers.CreateDictionary<(int, int), MemberStatus>(_scratch);
            ParserContainers.EnsureEntryCapacity(_scratch, _replayed, sizeof(byte));
            _replayed.Add(key, status);
            return Keeps(status, prune);
        }

        private static bool Keeps(MemberStatus status, bool prune)
            => (status & MemberStatus.HasSurvivor) != 0 || (status == MemberStatus.None && !prune);

        private Summary? Summarize(PdfReference reference, PdfDictionary field, int depth)
        {
            Check();
            if (depth > _limits.MaxNestingDepth) { return null; }
            var key = (reference.ObjectNumber, reference.Generation);
            if (_acyclic.TryGetValue(key, out Summary cached))
            { return depth + cached.Height <= _limits.MaxNestingDepth ? cached : null; }
            if (!ParserContainers.AddSetItem(_scratch, _active, key.ObjectNumber, sizeof(int))) { return null; }

            try
            {
                MemberStatus status = IsWidget(field, _resolver, _governor, _token)
                    ? WidgetStatus(key.ObjectNumber) : MemberStatus.None;
                int height = 0;
                if (ReadFieldKids(field, _resolver) is PdfArray kids)
                {
                    for (int index = 0; index < kids.Count; index++)
                    {
                        Check();
                        if (kids[index] is not PdfReference child
                            || !PdfObjectLookup.TryResolve(child, _resolver, _governor, _token, out QpdfObject? value)
                            || value is not PdfDictionary childField) { continue; }
                        if (Summarize(child, childField, depth + 1) is not Summary nested) { return null; }
                        status |= nested.Status;
                        height = Math.Max(height, nested.Height + 1);
                    }
                }
                Summary summary = new(status, height);
                ParserContainers.EnsureEntryCapacity(_scratch, _acyclic, 8L);
                _acyclic.Add(key, summary);
                return summary;
            }
            finally { _active.Remove(key.ObjectNumber); }
        }

        private MemberStatus WidgetStatus(int number)
            => MemberStatus.HasWidget | (_survivors.Contains(number) ? MemberStatus.HasSurvivor : MemberStatus.None);

        private void Check()
        {
            _token.ThrowIfCancellationRequested();
            _governor.NoteOperation();
        }
    }
}
