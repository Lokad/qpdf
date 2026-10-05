using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Syntax;

/// <summary>A value and the exclusive admission contributed by its parser subtree.</summary>
// These receipts stay within one attempt. Resolver loads have separate scopes;
// borrowing a previously loaded value never adds its admission to this receipt.
internal readonly record struct ParsedValue(QpdfObject Value, long ReservedBytes);
