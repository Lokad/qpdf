using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lokad.Qpdf.Probe;

// Development-only warmed complete jobs. The line protocol and result reporting
// are outside measurement; each emit request still opens a fresh host/document.
internal static class WarmCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 0) { throw new ProbeException("warm accepts no arguments.", 1); }
        TextWriter protocol = Console.Out;
        TextWriter errors = Console.Error;
        for (int request = 0; request < 65536; request++)
        {
            string? line = Console.ReadLine();
            if (line is null) { return 0; }
            if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 1024)
            { throw new ProbeException("Invalid warm argument count.", 1); }
            string[] arguments = new string[count];
            int characters = 0;
            for (int i = 0; i < count; i++)
            {
                arguments[i] = Console.ReadLine() ?? throw new ProbeException("Incomplete warm request.", 1);
                characters = checked(characters + arguments[i].Length);
                if (characters > 4 * 1024 * 1024) { throw new ProbeException("Warm request too large.", 1); }
            }
            bool inputOnly = arguments[0] == "read-input";
            bool fileSelection = arguments[0] == "select-file";
            bool jobJson = arguments[0] == "emit-job";
            if (inputOnly && count != 2) { throw new ProbeException("read-input needs one file path.", 1); }
            if (!inputOnly && !fileSelection && !jobJson && arguments[0] != "emit")
            { throw new ProbeException("warm accepts emit/emit-job jobs, select-file or read-input diagnostics.", 1); }
            long allocated = inputOnly ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(false);
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            int exit;
            byte[]? input = null;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (inputOnly) { input = EmitCommand.ReadInputBytes(arguments[1]); exit = 0; }
                else if (fileSelection) { exit = FileSelectionCommand.Run(arguments[1..]); }
                else if (jobJson) { exit = EmitCommand.RunWarmEmitJob(arguments[1..]); }
                else { exit = EmitCommand.RunWarmEmit(arguments[1..]); }
            }
            finally { Console.SetOut(protocol); Console.SetError(errors); }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocatedBytes = (inputOnly ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(false)) - allocated;
            int[] collectionDelta = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray();
            // Validate the complete fresh load after timing/allocation capture.
            // This component diagnostic is separate from complete emit jobs.
            int? inputBytes = input?.Length;
            string? inputSha256 = input is null ? null : Convert.ToHexString(SHA256.HashData(input));
            input = null;
            protocol.WriteLine("WARM " + JsonSerializer.Serialize(new
            {
                elapsedMilliseconds = elapsed, exitCode = exit,
                allocatedBytes, collections = collectionDelta, inputBytes, inputSha256,
            }));
            protocol.Flush();
            if (exit != 0) { return exit; }
        }
        throw new ProbeException("warm exceeded 65536 jobs.", 1);
    }
}
