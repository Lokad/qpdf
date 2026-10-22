using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class DictionaryRepairTests
{
    // Independently authored ASCII inputs; exact values observed with pinned qpdf 12.4.2.
    // Budget groups below are oracle snapshots, not predictions from parser logic.
    // Ordinary tests use this data without any external tool or checkout.
    private sealed record Observation(string Name, string Body, bool Sanity, long[] Budgets, string Value);

    // The authored PDF has objects 1–5, generation zero. Only nullness is
    // consulted during display; this fake never parses or expands their graphs.
    private sealed class FixtureResolver : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            bool found = reference.Generation == 0 && reference.ObjectNumber is >= 1 and <= 5;
            value = found ? PdfBoolean.True : PdfNull.Instance;
            return found;
        }
    }

    internal static IEnumerable<(string Name, string Body, bool Sanity, long Budget, string Expected)> Cases()
    {
        Observation[] observations =
        [
            new("array-orphan-gap", "<< [1 2] 3 @ /A 4 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 4 /QPDFFake1 [ 1 2 ] /QPDFFake2 3 /QPDFFake3 (@) >>"),
            new("array-orphan-gap", "<< [1 2] 3 @ /A 4 >>", false, [1L],
                "null"),
            new("array-orphan-gap", "<< [1 2] 3 @ /A 4 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 4 >>"),
            new("array-orphan-gap", "<< [1 2] 3 @ /A 4 >>", true, [1L],
                "null"),
            new("duplicate-integer-next-array", "<< @ /A 1 /A 2 [ @ ] >>", false, [0L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 [ (@) ] >>"),
            new("duplicate-integer-next-array", "<< @ /A 1 /A 2 [ @ ] >>", false, [1L, 2L, 3L],
                "null"),
            new("duplicate-integer-next-array", "<< @ /A 1 /A 2 [ @ ] >>", true, [0L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-next-array", "<< @ /A 1 /A 2 [ @ ] >>", true, [1L, 2L, 3L],
                "null"),
            new("duplicate-integer-next-error", "<< @ /A 1 /A 2 @ >>", false, [4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 (@) >>"),
            new("duplicate-integer-next-error", "<< @ /A 1 /A 2 @ >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-integer-next-error", "<< @ /A 1 /A 2 @ >>", true, [4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-next-error", "<< @ /A 1 /A 2 @ >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-integer-next-dictionary", "<< @ /A 1 /A 2 << /B @ >> >>", false, [0L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 << /B (@) >> >>"),
            new("duplicate-integer-next-dictionary", "<< @ /A 1 /A 2 << /B @ >> >>", false, [1L, 2L, 3L],
                "null"),
            new("duplicate-integer-next-dictionary", "<< @ /A 1 /A 2 << /B @ >> >>", true, [0L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-next-dictionary", "<< @ /A 1 /A 2 << /B @ >> >>", true, [1L, 2L, 3L],
                "null"),
            new("error-then-duplicate", "<< @ /A 1 /A 2 >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) >>"),
            new("error-then-duplicate", "<< @ /A 1 /A 2 >>", false, [1L, 2L],
                "null"),
            new("error-then-duplicate", "<< @ /A 1 /A 2 >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("error-then-duplicate", "<< @ /A 1 /A 2 >>", true, [1L, 2L],
                "null"),
            new("duplicate-reference-value", "<< @ /A 1 /A 2 0 R >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 0 R /QPDFFake1 (@) >>"),
            new("duplicate-reference-value", "<< @ /A 1 /A 2 0 R >>", false, [1L, 2L],
                "null"),
            new("duplicate-reference-value", "<< @ /A 1 /A 2 0 R >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 0 R >>"),
            new("duplicate-reference-value", "<< @ /A 1 /A 2 0 R >>", true, [1L, 2L],
                "null"),
            new("duplicate-integer-delayed-error", "<< @ /A 1 /A 2 3 @ >>", false, [5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 3 /QPDFFake3 (@) >>"),
            new("duplicate-integer-delayed-error", "<< @ /A 1 /A 2 3 @ >>", false, [0L, 1L, 2L, 3L, 4L],
                "null"),
            new("duplicate-integer-delayed-error", "<< @ /A 1 /A 2 3 @ >>", true, [5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-delayed-error", "<< @ /A 1 /A 2 3 @ >>", true, [0L, 1L, 2L, 3L, 4L],
                "null"),
            new("duplicate-integer-orphan", "<< @ /A 1 /A 2 3 >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 3 >>"),
            new("duplicate-integer-orphan", "<< @ /A 1 /A 2 3 >>", false, [1L, 2L],
                "null"),
            new("duplicate-integer-orphan", "<< @ /A 1 /A 2 3 >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-orphan", "<< @ /A 1 /A 2 3 >>", true, [1L, 2L],
                "null"),
            new("duplicate-integer-orphans", "<< @ /A 1 /A 2 3 4 >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) /QPDFFake2 3 /QPDFFake3 4 >>"),
            new("duplicate-integer-orphans", "<< @ /A 1 /A 2 3 4 >>", false, [1L, 2L],
                "null"),
            new("duplicate-integer-orphans", "<< @ /A 1 /A 2 3 4 >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-orphans", "<< @ /A 1 /A 2 3 4 >>", true, [1L, 2L],
                "null"),
            new("duplicate-integer-next-reference", "<< @ /A 1 /A 2 3 4 R >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 /QPDFFake1 (@) >>"),
            new("duplicate-integer-next-reference", "<< @ /A 1 /A 2 3 4 R >>", false, [1L, 2L],
                "null"),
            new("duplicate-integer-next-reference", "<< @ /A 1 /A 2 3 4 R >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-integer-next-reference", "<< @ /A 1 /A 2 3 4 R >>", true, [1L, 2L],
                "null"),
            new("duplicate-real-close", "<< @ /A 1 /A 2.0 >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /A 2.0 /QPDFFake1 (@) >>"),
            new("duplicate-real-close", "<< @ /A 1 /A 2.0 >>", false, [0L, 1L, 2L],
                "null"),
            new("duplicate-real-close", "<< @ /A 1 /A 2.0 >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< /A 2.0 >>"),
            new("duplicate-real-close", "<< @ /A 1 /A 2.0 >>", true, [0L, 1L, 2L],
                "null"),
            new("duplicate-null-value", "<< @ /A 1 /A null >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /QPDFFake1 (@) >>"),
            new("duplicate-null-value", "<< @ /A 1 /A null >>", false, [0L, 1L, 2L],
                "null"),
            new("duplicate-null-value", "<< @ /A 1 /A null >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< >>"),
            new("duplicate-null-value", "<< @ /A 1 /A null >>", true, [0L, 1L, 2L],
                "null"),
            new("duplicate-gap-below", "<< @ /A 1 /B @ /A 2 3 >>", false, [4L, 5L, 6L, 15L],
                "<< /A 2 /B (@) /QPDFFake1 (@) /QPDFFake2 3 >>"),
            new("duplicate-gap-below", "<< @ /A 1 /B @ /A 2 3 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-gap-below", "<< @ /A 1 /B @ /A 2 3 >>", true, [4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-gap-below", "<< @ /A 1 /B @ /A 2 3 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-gap-at", "<< @ /A 1 /B @ /A 2 3 4 >>", false, [4L, 5L, 6L, 15L],
                "<< /A 2 /B (@) /QPDFFake1 (@) /QPDFFake2 3 /QPDFFake3 4 >>"),
            new("duplicate-gap-at", "<< @ /A 1 /B @ /A 2 3 4 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-gap-at", "<< @ /A 1 /B @ /A 2 3 4 >>", true, [4L, 5L, 6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-gap-at", "<< @ /A 1 /B @ /A 2 3 4 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("duplicate-gap-reference-tail", "<< @ /A 1 /B @ /A 2 3 4 R @ >>", false, [6L, 15L],
                "<< /A 2 /B (@) /QPDFFake1 (@) /QPDFFake3 (@) >>"),
            new("duplicate-gap-reference-tail", "<< @ /A 1 /B @ /A 2 3 4 R @ >>", false, [0L, 1L, 2L, 3L, 4L, 5L],
                "null"),
            new("duplicate-gap-reference-tail", "<< @ /A 1 /B @ /A 2 3 4 R @ >>", true, [6L, 15L],
                "<< /A 2 >>"),
            new("duplicate-gap-reference-tail", "<< @ /A 1 /B @ /A 2 3 4 R @ >>", true, [0L, 1L, 2L, 3L, 4L, 5L],
                "null"),
            new("spaced-orphan-errors", "<< @ /A 1 /B 2 @ /C 3 /D 4 @ /E 5 >>", false, [0L, 4L, 5L, 6L, 15L],
                "<< /A 1 /B 2 /C 3 /D 4 /E 5 /QPDFFake1 (@) /QPDFFake2 (@) /QPDFFake3 (@) >>"),
            new("spaced-orphan-errors", "<< @ /A 1 /B 2 @ /C 3 /D 4 @ /E 5 >>", false, [1L, 2L, 3L],
                "null"),
            new("spaced-orphan-errors", "<< @ /A 1 /B 2 @ /C 3 /D 4 @ /E 5 >>", true, [0L, 4L, 5L, 6L, 15L],
                "<< /A 1 /B 2 /C 3 /D 4 /E 5 >>"),
            new("spaced-orphan-errors", "<< @ /A 1 /B 2 @ /C 3 /D 4 @ /E 5 >>", true, [1L, 2L, 3L],
                "null"),
            new("error-before-orphans", "<< @ 1 2 /A 3 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 (@) /QPDFFake2 1 /QPDFFake3 2 >>"),
            new("error-before-orphans", "<< @ 1 2 /A 3 >>", false, [1L],
                "null"),
            new("error-before-orphans", "<< @ 1 2 /A 3 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("error-before-orphans", "<< @ 1 2 /A 3 >>", true, [1L],
                "null"),
            new("child-orphan-guard", "<< /A << 1 2 @ /B 3 >> /C 4 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << /B 3 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 (@) >> /C 4 >>"),
            new("child-orphan-guard", "<< /A << 1 2 @ /B 3 >> /C 4 >>", false, [1L],
                "null"),
            new("child-orphan-guard", "<< /A << 1 2 @ /B 3 >> /C 4 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << /B 3 >> /C 4 >>"),
            new("child-orphan-guard", "<< /A << 1 2 @ /B 3 >> /C 4 >>", true, [1L],
                "null"),
            new("fake-key-collision-error", "<< /QPDFFake1 9 1 2 @ /A 3 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 9 /QPDFFake2 1 /QPDFFake3 2 /QPDFFake4 (@) >>"),
            new("fake-key-collision-error", "<< /QPDFFake1 9 1 2 @ /A 3 >>", false, [1L],
                "null"),
            new("fake-key-collision-error", "<< /QPDFFake1 9 1 2 @ /A 3 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 9 >>"),
            new("fake-key-collision-error", "<< /QPDFFake1 9 1 2 @ /A 3 >>", true, [1L],
                "null"),
            new("one-orphan-error", "<< 1 @ /A 3 >>", false, [2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 1 /QPDFFake2 (@) >>"),
            new("one-orphan-error", "<< 1 @ /A 3 >>", false, [0L, 1L],
                "null"),
            new("one-orphan-error", "<< 1 @ /A 3 >>", true, [2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("one-orphan-error", "<< 1 @ /A 3 >>", true, [0L, 1L],
                "null"),
            new("orphan-error-storm", "<< 1 2 @ @ @ @ @ @ /A 3 >>", false, [0L, 1L, 2L, 3L, 4L, 5L, 6L, 15L],
                "null"),
            new("orphan-error-storm", "<< 1 2 @ @ @ @ @ @ /A 3 >>", true, [0L, 1L, 2L, 3L, 4L, 5L, 6L, 15L],
                "null"),
            new("orphan-late-error", "<< 1 2 @ /A 3 >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 (@) >>"),
            new("orphan-late-error", "<< 1 2 @ /A 3 >>", false, [0L, 1L, 2L],
                "null"),
            new("orphan-late-error", "<< 1 2 @ /A 3 >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("orphan-late-error", "<< 1 2 @ /A 3 >>", true, [0L, 1L, 2L],
                "null"),
            new("array-then-key-error", "<< 1 2 /A [ @ ] @ /B 4 >>", false, [4L, 5L, 6L, 15L],
                "<< /A [ (@) ] /B 4 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 (@) >>"),
            new("array-then-key-error", "<< 1 2 /A [ @ ] @ /B 4 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("array-then-key-error", "<< 1 2 /A [ @ ] @ /B 4 >>", true, [4L, 5L, 6L, 15L],
                "<< /A [ null ] /B 4 >>"),
            new("array-then-key-error", "<< 1 2 /A [ @ ] @ /B 4 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("array-then-value-error", "<< 1 2 /A [ @ ] /B @ >>", false, [4L, 5L, 6L, 15L],
                "<< /A [ (@) ] /B (@) /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("array-then-value-error", "<< 1 2 /A [ @ ] /B @ >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("array-then-value-error", "<< 1 2 /A [ @ ] /B @ >>", true, [4L, 5L, 6L, 15L],
                "<< /A [ null ] >>"),
            new("array-then-value-error", "<< 1 2 /A [ @ ] /B @ >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("orphan-value-gap", "<< 1 2 /A @ >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /A (@) /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("orphan-value-gap", "<< 1 2 /A @ >>", false, [0L, 1L, 2L],
                "null"),
            new("orphan-value-gap", "<< 1 2 /A @ >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< >>"),
            new("orphan-value-gap", "<< 1 2 /A @ >>", true, [0L, 1L, 2L],
                "null"),
            new("child-dictionary-isolation", "<< 1 2 /A << /B @ /C @ >> >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << /B (@) /C (@) >> /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("child-dictionary-isolation", "<< 1 2 /A << /B @ /C @ >> >>", false, [1L, 2L],
                "null"),
            new("child-dictionary-isolation", "<< 1 2 /A << /B @ /C @ >> >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << >> >>"),
            new("child-dictionary-isolation", "<< 1 2 /A << /B @ /C @ >> >>", true, [1L, 2L],
                "null"),
            new("dictionary-then-key-error", "<< 1 2 /A << /B @ >> @ /C 4 >>", false, [4L, 5L, 6L, 15L],
                "<< /A << /B (@) >> /C 4 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 (@) >>"),
            new("dictionary-then-key-error", "<< 1 2 /A << /B @ >> @ /C 4 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("dictionary-then-key-error", "<< 1 2 /A << /B @ >> @ /C 4 >>", true, [4L, 5L, 6L, 15L],
                "<< /A << >> /C 4 >>"),
            new("dictionary-then-key-error", "<< 1 2 /A << /B @ >> @ /C 4 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("nested-guard-and-parent", "<< 1 2 /A << 1 @ /B 3 >> /C @ >>", false, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << /B 3 /QPDFFake1 1 /QPDFFake2 (@) >> /C (@) /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("nested-guard-and-parent", "<< 1 2 /A << 1 @ /B 3 >> /C @ >>", false, [1L, 2L],
                "null"),
            new("nested-guard-and-parent", "<< 1 2 /A << 1 @ /B 3 >> /C @ >>", true, [0L, 3L, 4L, 5L, 6L, 15L],
                "<< /A << /B 3 >> >>"),
            new("nested-guard-and-parent", "<< 1 2 /A << 1 @ /B 3 >> /C @ >>", true, [1L, 2L],
                "null"),
            new("orphan-duplicates", "<< 1 2 /A 3 /A 4 /A 5 >>", false, [4L, 5L, 6L, 15L],
                "<< /A 5 /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("orphan-duplicates", "<< 1 2 /A 3 /A 4 /A 5 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("orphan-duplicates", "<< 1 2 /A 3 /A 4 /A 5 >>", true, [4L, 5L, 6L, 15L],
                "<< /A 5 >>"),
            new("orphan-duplicates", "<< 1 2 /A 3 /A 4 /A 5 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("value-error-duplicate", "<< 1 2 /A 3 /B @ /B 4 >>", false, [4L, 5L, 6L, 15L],
                "<< /A 3 /B 4 /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("value-error-duplicate", "<< 1 2 /A 3 /B @ /B 4 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("value-error-duplicate", "<< 1 2 /A 3 /B @ /B 4 >>", true, [4L, 5L, 6L, 15L],
                "<< /A 3 /B 4 >>"),
            new("value-error-duplicate", "<< 1 2 /A 3 /B @ /B 4 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("nul-key-after-orphans", "<< 1 2 /A#00B 3 /C 4 >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /C 4 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake4 3 >>"),
            new("nul-key-after-orphans", "<< 1 2 /A#00B 3 /C 4 >>", false, [0L, 1L, 2L],
                "null"),
            new("nul-key-after-orphans", "<< 1 2 /A#00B 3 /C 4 >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< /C 4 >>"),
            new("nul-key-after-orphans", "<< 1 2 /A#00B 3 /C 4 >>", true, [0L, 1L, 2L],
                "null"),
            new("fake-key-collision-duplicate", "<< 1 2 /QPDFFake1 9 /A 3 /A 4 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 4 /QPDFFake1 9 /QPDFFake2 1 /QPDFFake3 2 >>"),
            new("fake-key-collision-duplicate", "<< 1 2 /QPDFFake1 9 /A 3 /A 4 >>", false, [1L],
                "null"),
            new("fake-key-collision-duplicate", "<< 1 2 /QPDFFake1 9 /A 3 /A 4 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 4 /QPDFFake1 9 >>"),
            new("fake-key-collision-duplicate", "<< 1 2 /QPDFFake1 9 /A 3 /A 4 >>", true, [1L],
                "null"),
            new("invalid-reference-followup", "<< 1 2 0 0 R @ /A 3 >>", false, [5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake4 (@) >>"),
            new("invalid-reference-followup", "<< 1 2 0 0 R @ /A 3 >>", false, [0L, 1L, 2L, 3L, 4L],
                "null"),
            new("invalid-reference-followup", "<< 1 2 0 0 R @ /A 3 >>", true, [5L, 6L, 15L],
                "<< /A 3 >>"),
            new("invalid-reference-followup", "<< 1 2 0 0 R @ /A 3 >>", true, [0L, 1L, 2L, 3L, 4L],
                "null"),
            new("invalid-reference-gap", "<< 1 2 0 0 R /A 3 >>", false, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 1 /QPDFFake2 2 >>"),
            new("invalid-reference-gap", "<< 1 2 0 0 R /A 3 >>", false, [1L],
                "null"),
            new("invalid-reference-gap", "<< 1 2 0 0 R /A 3 >>", true, [0L, 2L, 3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("invalid-reference-gap", "<< 1 2 0 0 R /A 3 >>", true, [1L],
                "null"),
            new("orphan-good-gap", "<< 1 2 3 @ /A 4 >>", false, [4L, 5L, 6L, 15L],
                "<< /A 4 /QPDFFake1 1 /QPDFFake2 2 /QPDFFake3 3 /QPDFFake4 (@) >>"),
            new("orphan-good-gap", "<< 1 2 3 @ /A 4 >>", false, [0L, 1L, 2L, 3L],
                "null"),
            new("orphan-good-gap", "<< 1 2 3 @ /A 4 >>", true, [4L, 5L, 6L, 15L],
                "<< /A 4 >>"),
            new("orphan-good-gap", "<< 1 2 3 @ /A 4 >>", true, [0L, 1L, 2L, 3L],
                "null"),
            new("null-orphan-error", "<< null null @ /A 3 >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake3 (@) >>"),
            new("null-orphan-error", "<< null null @ /A 3 >>", false, [0L, 1L, 2L],
                "null"),
            new("null-orphan-error", "<< null null @ /A 3 >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("null-orphan-error", "<< null null @ /A 3 >>", true, [0L, 1L, 2L],
                "null"),
            new("boolean-orphan-error", "<< true false @ /A 3 >>", false, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 /QPDFFake1 true /QPDFFake2 false /QPDFFake3 (@) >>"),
            new("boolean-orphan-error", "<< true false @ /A 3 >>", false, [0L, 1L, 2L],
                "null"),
            new("boolean-orphan-error", "<< true false @ /A 3 >>", true, [3L, 4L, 5L, 6L, 15L],
                "<< /A 3 >>"),
            new("boolean-orphan-error", "<< true false @ /A 3 >>", true, [0L, 1L, 2L],
                "null"),
        ];
        foreach (Observation observation in observations)
        {
            foreach (long budget in observation.Budgets)
            {
                yield return (observation.Name, observation.Body, observation.Sanity, budget, observation.Value);
            }
        }
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach ((string name, string body, bool sanity, long budget, string expected) in Cases())
        {
            yield return [name, body, sanity, budget, expected];
        }
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void OrphanErrorGuardsMatchPinnedValues(string name, string body, bool sanity, long budget, string expected)
    {
        Assert.NotEmpty(name);
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = budget };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true, SanityChecks = sanity };
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj " + body + " endobj")), 0, parsedMemory0);
        parsedMemory0.RetainGraph(); parsedMemory0.Dispose();
        using (var display = CanonicalDisplay.Render(value, limits, governor, new FixtureResolver())) { Assert.Equal(expected, display.Value); }
        Assert.NotEmpty(diagnostics.Entries);
    }

    [Fact]
    public void AbortedDictionaryDoesNotCarryOrphansIntoTheNextObject()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        using ParserMemoryScope parsedMemory1 = ParserMemoryScope.Create(governor);
        Assert.IsType<PdfNull>(parser.ParseObjectValue(new PdfTokenizer("<< 1 2 @ /A 3 >>"u8.ToArray()), 0, parsedMemory1));
        parsedMemory1.RetainGraph(); parsedMemory1.Dispose();
        Assert.True(parser.ObjectAborted);
        using ParserMemoryScope parsedMemory2 = ParserMemoryScope.Create(governor);
        QpdfObject next = parser.ParseObjectValue(new PdfTokenizer("[ @ ]"u8.ToArray()), 0, parsedMemory2);
        parsedMemory2.RetainGraph(); parsedMemory2.Dispose();
        Assert.False(parser.ObjectAborted);
        Assert.Equal(1L, parser.ToleratedErrors);
        using (var display = CanonicalDisplay.Render(next, limits, governor)) { Assert.Equal("[ (@) ]", display.Value); }
    }

    [Theory]
    [InlineData(nameof(QpdfLimits.MaxMemoryBytes))]
    [InlineData(nameof(QpdfLimits.MaxDiagnostics))]
    public void DictionaryRepairDoesNotSwallowHostLimits(string limit)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 0L };
        limits = limit switch
        {
            nameof(QpdfLimits.MaxMemoryBytes) => limits with { MaxMemoryBytes = 64L },
            _ => limits with { MaxDiagnostics = 1L },
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
        {
            using ParserMemoryScope parsedMemory3 = ParserMemoryScope.Create(governor);
            _ = parser.ParseObjectValue(new PdfTokenizer("<< 1 2 @ /A 3 >>"u8.ToArray()), 0, parsedMemory3);
        });
        Assert.Equal(limit, error.LimitName);
    }
}
