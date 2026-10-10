using System.Text;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>
/// Single-level response-file expansion through the host. Tokens starting with
/// `@` name a logical response file holding one argument per line;
/// `@-` (standard input) has no host descriptor and fails explicitly. Missing
/// files stay literal like the reference parser; nested `@` tokens are never
/// re-expanded. File bytes and expanded lines charge the job budgets.
/// </summary>
internal static class ResponseFileExpander
{
    internal static async ValueTask<ExpandedArguments> ExpandAsync(
        IQpdfHost host,
        IReadOnlyList<string> argv,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (argv is null)
        {
            throw new ArgumentNullException(nameof(argv));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        ExpandedArguments expanded = ExpandedArguments.Create(governor);
        try
        {
            for (int i = 0; i < argv.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? token = argv[i];
                if (token is null)
                {
                    throw new QpdfParseException("Null argument.", i, null);
                }

                if (token.Length < 2 || token[0] != '@')
                {
                    expanded.AddBorrowed(token);
                    continue;
                }

                string name = expanded.CopySubstring(token, 1, token.Length - 1);
                if (name == "-")
                {
                    throw new QpdfParseException("Response arguments from standard input need a stdin descriptor, which this profile does not provide.", i, null);
                }

                QpdfPath path;
                try
                {
                    path = QpdfPath.From(name);
                }
                catch (ArgumentException ex)
                {
                    throw new QpdfParseException($"Response file name is not a valid logical path: {ex.Message}.", i, null, ex);
                }

                using GovernedText? content = await TryReadTextFileAsync(host, path, limits, governor, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    expanded.AddBorrowed(token);
                    continue;
                }

                expanded.AddResponseLines(content);
            }

            return expanded;
        }
        catch { expanded.Dispose(); throw; }
    }

    internal static async ValueTask<GovernedText> ReadTextFileAsync(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        GovernedText? decoded = null;
        try
        {
            if (input.Length > limits.MaxInputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
            }

            if (input.Length > int.MaxValue)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
            }

            if (input.Length > limits.MaxMemoryBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), input.Length, limits.MaxMemoryBytes);
            }

            // Encoded bytes and decoded text have overlapping physical storage.
            // Keep the text's independent owner through input close and splitting.
            using ParserMemoryScope bytesOwner = ParserMemoryScope.Create(governor);
            bytesOwner.Reserve(checked(32L + input.Length));
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            try
            {
                decoded = GovernedText.DecodeUtf8(governor, bytes);
                return decoded;
            }
            catch (DecoderFallbackException ex)
            {
                throw new QpdfParseException($"Response file '{path.Value}' is not valid UTF-8.", 0L, null, ex);
            }
        }
        finally
        {
            try { await input.DisposeAsync().ConfigureAwait(false); }
            catch { decoded?.Dispose(); throw; }
        }
    }

    private static async ValueTask<GovernedText?> TryReadTextFileAsync(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTextFileAsync(host, path, limits, governor, cancellationToken).ConfigureAwait(false);
        }
        catch (QpdfHostException ex) when (ex.Reason == QpdfHostFailureReason.NotFound)
        {
            return null;
        }
    }

}
