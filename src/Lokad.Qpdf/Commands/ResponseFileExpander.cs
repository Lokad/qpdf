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
    internal static async ValueTask<IReadOnlyList<string>> ExpandAsync(
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
        List<string> expanded = new(argv.Count);
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
                expanded.Add(token);
                continue;
            }

            string name = token.Substring(1);
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

            string? content = await TryReadTextFileAsync(host, path, limits, governor, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                expanded.Add(token);
                continue;
            }

            foreach (string line in SplitLines(content))
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                expanded.Add(line);
            }
        }

        return expanded;
    }

    internal static async ValueTask<string> ReadTextFileAsync(
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

            // The decoded configuration text has a different lifetime from
            // this private read copy. Retire only the byte attempt here;
            // expanded-token/text ownership remains the controller audit.
            using ParserMemoryScope bytesOwner = ParserMemoryScope.Create(governor);
            bytesOwner.Reserve(input.Length == 0L ? 0L : checked(32L + input.Length));
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new QpdfParseException($"Response file '{path.Value}' is not valid UTF-8.", 0L, null, ex);
            }
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<string?> TryReadTextFileAsync(
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

    private static List<string> SplitLines(string content)
    {
        List<string> lines = new();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
            {
                continue;
            }

            int end = i;
            if (end > start && content[end - 1] == '\r')
            {
                end -= 1;
            }

            lines.Add(content.Substring(start, end - start));
            start = i + 1;
        }

        if (start < content.Length)
        {
            lines.Add(content.Substring(start));
        }

        return lines;
    }
}
