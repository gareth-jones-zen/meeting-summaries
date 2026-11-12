// meeting-summaries.exe: every command, with output. Output to a file or pipe is UTF-8.
using System.Text;
using MeetingSummaries;

if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
var background = Path.Combine(AppContext.BaseDirectory, "meeting-summaries-background.exe");
return Cli.Run(args, new SummariesHost { LogEcho = Console.Out, StartQueueRunner = SummariesHost.Detached(background) });
