namespace Lokad.Qpdf.Runtime;

/// <summary>One scoped preparation hook for either a command or typed result.</summary>
internal abstract class ExecutionPreparation
{
    internal abstract void Prepare();
}
