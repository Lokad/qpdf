namespace Lokad.Qpdf.Security;

// ISO 32000 permission bits describe the document's requested restrictions.
// They do not grant host capabilities or alter password authentication.
internal readonly record struct EncryptionPermissions(int Revision, int Flags)
{
    private bool Has(int bit) => (Flags & (1 << (bit - 1))) != 0;
    internal bool Accessibility => Has(Revision < 3 ? 5 : 10);
    internal bool Extract => Has(5);
    internal bool PrintLow => Has(3);
    internal bool PrintHigh => PrintLow && (Revision < 3 || Has(12));
    internal bool Assemble => Has(Revision < 3 ? 4 : 11);
    internal bool Forms => Has(Revision < 3 ? 6 : 9);
    internal bool Annotations => Has(6);
    internal bool ModifyOther => Has(4);
    internal bool ModifyAll => Annotations && ModifyOther && Forms && Assemble;
}
