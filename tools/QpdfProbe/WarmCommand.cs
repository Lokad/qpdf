using System.Diagnostics;
using System.Globalization;
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
            if (arguments[0] != "emit") { throw new ProbeException("warm only accepts emit jobs.", 1); }
            long allocated = GC.GetTotalAllocatedBytes(false);
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            int exit;
            long start = Stopwatch.GetTimestamp();
            try { exit = EmitCommand.RunWarmEmit(arguments[1..]); }
            finally { Console.SetOut(protocol); Console.SetError(errors); }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            protocol.WriteLine("WARM " + JsonSerializer.Serialize(new
            {
                elapsedMilliseconds = elapsed, exitCode = exit,
                allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated,
                collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
            }));
            protocol.Flush();
            if (exit != 0) { return exit; }
        }
        throw new ProbeException("warm exceeded 65536 jobs.", 1);
    }
}
