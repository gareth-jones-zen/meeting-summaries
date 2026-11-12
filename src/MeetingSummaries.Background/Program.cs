// meeting-summaries-background.exe: no window, ever. What a hook (teams-transcripts' after_run, doc-filer's) and the
// autostart task run; a hook's stdin still works.
using MeetingSummaries;

var background = Path.Combine(AppContext.BaseDirectory, "meeting-summaries-background.exe");
return Cli.Run(args, new SummariesHost { Out = TextWriter.Null, Err = TextWriter.Null, StartQueueRunner = SummariesHost.Detached(background) });
