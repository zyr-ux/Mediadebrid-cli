using System;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;
using MediaDebrid_cli;
using MediaDebrid_cli.Views;
using Spectre.Console;

namespace MediaDebrid_cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Settings.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("\n[yellow]Termination requested. Cleaning up...[/]");
            cts.Cancel();
            Environment.Exit(0);
        };

        // ── Interactive mode (no args) ─────────────────────────────────────
        if (args.Length == 0)
        {
            TuiApp.ShowLogo();

            var magnet = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]Magnet Link[/]:")
                    .PromptStyle("green"));

            if (!string.IsNullOrWhiteSpace(magnet))
            {
                await RunAppAsync(magnet, showLogo: false, cts.Token);
            }

            return 0;
        }

        // ── CLI mode ───────────────────────────────────────────────────────
        var rootCommand = new RootCommand("MediaDebrid — magnet-to-media downloader");

        var addCommand = new Command("add", "Add a magnet link and start downloading");
        var magnetArg = new Argument<string>("magnet") { Description = "Magnet link to process" };
        addCommand.AddArgument(magnetArg);

        addCommand.SetHandler(async (string magnet) =>
        {
            await RunAppAsync(magnet, showLogo: true, cts.Token);
        }, magnetArg);

        rootCommand.AddCommand(addCommand);
        return await rootCommand.InvokeAsync(args);
    }

    private static async Task RunAppAsync(string magnet, bool showLogo, CancellationToken cancellationToken)
    {
        var app = new TuiApp();
        try
        {
            await app.RunAsync(magnet, showLogo: showLogo, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful exit — no action needed
        }
    }
}
