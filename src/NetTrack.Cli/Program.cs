using System.Text;

namespace NetTrack.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var app = new CliApp(Console.Out, Console.Error);
        return await app.RunAsync(args, cts.Token).ConfigureAwait(false);
    }
}
