// A real executable lets Windows and Linux exercise the existing launch paths,
// without a native PDF tool, shell wrapper or production test hooks.
string? pidFile = Environment.GetEnvironmentVariable("QPDF_TEST_ORACLE_PID_FILE");
if (pidFile is not null)
{
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());
    Console.WriteLine("oracle stdout before timeout");
    Console.Error.WriteLine("oracle stderr before timeout");
    Console.Out.Flush();
    Console.Error.Flush();
    Thread.Sleep(TimeSpan.FromMinutes(3));
    return 0;
}

switch (args.Length == 0 ? string.Empty : args[0])
{
    case "--version":
        Console.WriteLine(Environment.GetEnvironmentVariable("QPDF_TEST_ORACLE_VERSION")
            ?? "qpdf version 12.4.2\nRun qpdf --copyright to see copyright and license information.");
        return 0;
    case "--show-npages":
        Console.WriteLine("1");
        return 0;
    case "--check":
        return 0;
    default:
        throw new ArgumentException("Unexpected oracle fixture invocation.");
}
