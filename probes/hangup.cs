// Connects to a cslq session's pipe and hangs up without sending anything, over and over.
// That half-open connection is what races the session's WaitForConnectionAsync: the accept
// gets `IOException: the pipe is being closed` instead of a connection, and a session that
// rethrew it logged `stopped:` and exited -- the caller then fell back, and the next call
// paid the whole workspace load again. It is a race, so a green run proves nothing; this
// generates it on demand.
//
// A file-based app rather than a project, like probes/hold-mutex.cs: it has to be a .NET
// process because a named pipe client is what the race needs, and `dotnet run probes/x.cs`
// costs a second and no .csproj.
using System.IO.Pipes;

if (args.Length is not (1 or 2))
{
    Console.Error.WriteLine("usage: dotnet run probes/hangup.cs -- <pipe name> [count]");
    return 2;
}

var pipe = args[0];
var count = args.Length == 2 ? int.Parse(args[1]) : 50;
var connected = 0;

for (var i = 0; i < count; i++)
{
    using var client = new NamedPipeClientStream(
        ".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await client.ConnectAsync(5000);
        connected++;
    }
    catch (Exception ex)
    {
        // Reported rather than fatal: the assertion belongs to the caller, which asks the
        // session a question afterwards. A pipe that stopped accepting halfway through is
        // exactly the failure under test, and it shows up there.
        Console.Error.WriteLine($"connect {i + 1} of {count} failed: {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"hung up {connected} of {count} connections on {pipe}");
return connected == count ? 0 : 1;
