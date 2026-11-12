using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

// Keep text setters out of generated record formatting.
internal readonly struct PasswordAssignment(string value, bool isFile)
{
    internal string Value { get; } = value;
    internal bool IsFile { get; } = isFile;
}

internal sealed class CommandPasswordFiles(IReadOnlyList<PasswordAssignment> assignments, bool hex, bool allowRecovery)
{
    internal IReadOnlyList<PasswordAssignment> Assignments { get; } = assignments;
    internal bool Hex { get; } = hex;
    internal bool AllowRecovery { get; } = allowRecovery;
}

// Owns file-derived secret bytes for the entire operation, including input close.
internal sealed class CommandPasswordLease : IDisposable
{
    private readonly ParserMemoryScope _memory;
    private readonly byte[] _prefix;
    private readonly byte[] _candidate;
    internal QpdfPassword? Password { get; private set; }

    private CommandPasswordLease(ParserMemoryScope memory)
    { _memory = memory; _prefix = new byte[127]; _candidate = new byte[127]; }

    internal static async ValueTask<CommandPasswordLease> ResolveAsync(IQpdfHost host, CommandPasswordFiles files,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        CommandPasswordLease result;
        try { memory.Reserve(96L + 2L * (127L + 32L)); result = new(memory); }
        catch { memory.Dispose(); throw; }
        try
        {
            string? text = null;
            bool hasBytes = false;
            int count = 0;
            foreach (PasswordAssignment assignment in files.Assignments)
            {
                governor.CheckCancellation();
                if (!assignment.IsFile)
                {
                    CryptographicOperations.ZeroMemory(result._prefix);
                    text = assignment.Value; hasBytes = false;
                    continue;
                }
                try
                {
                    PasswordFileLine line = await PasswordFileReader.ReadAsync(host, QpdfPath.From(assignment.Value),
                        result._candidate, files.Hex, limits, governor, token).ConfigureAwait(false);
                    if (line.HasMoreLines)
                    {
                        diagnostics.Add(QpdfDiagnosticCode.PasswordFileNote, QpdfDiagnosticSeverity.Info,
                            "The password file has multiple lines; only the first line is used.");
                    }
                    if (line.HasLine)
                    {
                        CryptographicOperations.ZeroMemory(result._prefix);
                        result._candidate.AsSpan(0, line.Count).CopyTo(result._prefix);
                        text = null; hasBytes = true; count = line.Count;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(result._candidate); }
            }
            governor.CheckCancellation();
            result.Password = hasBytes ? QpdfPassword.FromCommandBytes(result._prefix.AsMemory(0, count), files.AllowRecovery)
                : text is null ? null : files.Hex ? QpdfPassword.FromHexText(text, files.AllowRecovery)
                : QpdfPassword.FromText(text, files.AllowRecovery);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        Password = null;
        CryptographicOperations.ZeroMemory(_prefix);
        CryptographicOperations.ZeroMemory(_candidate);
        _memory.Dispose();
    }
}
