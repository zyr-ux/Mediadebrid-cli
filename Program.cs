using System.CommandLine;
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
            cts.Cancel();
            // Force exit if things are stuck
            Environment.Exit(0);
        };

        // ── Interactive mode (no args) ─────────────────────────────────────
        if (args.Length == 0)
        {
            TuiApp.ShowLogo();

            try
            {
                var magnet = AnsiConsole.Prompt(
                    new TextPrompt<string>("Enter [green]Magnet Link[/]:")
                        .PromptStyle("green")
                        .Validate(m =>
                        {
                            if (string.IsNullOrWhiteSpace(m)) return ValidationResult.Error("[red]Magnet link cannot be empty.[/]");
                            if (!m.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return ValidationResult.Error("[red]Invalid magnet link format.[/]");
                            return ValidationResult.Success();
                        }));

                await RunAppAsync(magnet, showLogo: false, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // Already handled by CancelKeyPress and RunAppAsync
            }
            catch (OperationCanceledException)
            {
                // Already handled
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
            AnsiConsole.MarkupLine("\n[yellow]Termination requested. Cleaning up...[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
        }
    }
}
