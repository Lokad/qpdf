using System.Text;
using System.Text.Json;

namespace Lokad.Qpdf.Probe;

// Reuses CLR/JIT setup only. Program.Main still constructs a fresh public job
// and host for each request. Output goes to exclusive request files, so large
// listings never accumulate in a protocol response or across requests.
internal static class BatchCommand
{
    internal const int MaximumRequests = 64;
    private const int MaximumRequestCharacters = 4 * 1024 * 1024;

    internal static int Run(string[] args)
    {
        if (args.Length != 0)
        {
            throw new ProbeException("batch accepts no command-line arguments.", 1);
        }

        using StreamReader input = new(Console.OpenStandardInput(), new UTF8Encoding(false, true), false);
        TextWriter protocol = Console.Out;
        TextWriter originalError = Console.Error;
        string originalDirectory = Directory.GetCurrentDirectory();
        for (int index = 0; index < MaximumRequests; index++)
        {
            string? line = ReadRequest(input);
            if (line is null) { return 0; }
            Request? request;
            try { request = JsonSerializer.Deserialize<Request>(line); }
            catch (JsonException exception)
            {
                throw new ProbeException("Invalid batch JSON: " + exception.Message, 1);
            }

            if (request is null || request.Id != index || request.Arguments is null ||
                string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
                string.IsNullOrWhiteSpace(request.StandardOutput) ||
                string.IsNullOrWhiteSpace(request.StandardError) ||
                request.Arguments.Any(argument => argument is null) ||
                (request.Arguments.Length != 0 && request.Arguments[0] == "batch"))
            {
                throw new ProbeException("Invalid or out-of-order batch request.", 1);
            }

            int exit;
            using (StreamWriter stdout = new(new FileStream(request.StandardOutput, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
            using (StreamWriter stderr = new(new FileStream(request.StandardError, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
            {
                try
                {
                    Directory.SetCurrentDirectory(request.WorkingDirectory);
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    exit = Program.Main(request.Arguments);
                }
                finally
                {
                    Console.SetOut(protocol);
                    Console.SetError(originalError);
                    Directory.SetCurrentDirectory(originalDirectory);
                }
            }

            protocol.WriteLine(JsonSerializer.Serialize(new Response(request.Id, exit)));
            protocol.Flush();
        }

        return 0;
    }

    private static string? ReadRequest(TextReader input)
    {
        StringBuilder line = new();
        while (true)
        {
            int value = input.Read();
            if (value < 0)
            {
                if (line.Length == 0) { return null; }
                throw new ProbeException("Incomplete batch request.", 1);
            }
            if (value == '\n') { return line.ToString(); }
            if (line.Length == MaximumRequestCharacters)
            {
                throw new ProbeException("Batch request exceeds the four MiB character bound.", 1);
            }
            line.Append((char)value);
        }
    }

    private sealed record Request(int Id, string[] Arguments, string WorkingDirectory, string StandardOutput, string StandardError);
    private sealed record Response(int Id, int ExitCode);
}
