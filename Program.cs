using System.CommandLine;
using MediaDebrid_cli.Services;
using MediaDebrid_cli.Tui;
using Spectre.Console;

namespace MediaDebrid_cli;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        Settings.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };

        Downloader.CleanupStaleFiles(Settings.Instance.MediaRoot);

        var app = new TuiApp();

        var rootCommand = new RootCommand("MediaDebrid — magnet-to-media downloader")
        {
            Name = "mediadebrid-cli"
        };

        // ── add Command ────────────────────────────────────────────────────
        var addCommand = new Command("add", "Add a magnet link and start downloading");
        var magnetArg = new Argument<string>("magnet") { Description = "Magnet link to process" };
        var typeOption = new Option<string?>("--type", "Media type (movie or show)");
        var titleOption = new Option<string?>("--title", "Title of the media");
        var yearOption = new Option<string?>("--year", "Year of release (optional)");
        var seasonOption = new Option<int?>("--season", "Season number (for shows)");

        addCommand.AddArgument(magnetArg);
        addCommand.AddOption(typeOption);
        addCommand.AddOption(titleOption);
        addCommand.AddOption(yearOption);
        addCommand.AddOption(seasonOption);

        addCommand.SetHandler(async (magnet, type, title, year, season) =>
        {
            try
            {
                await app.RunAsync(magnet, type, title, year, season, showLogo: true, cts.Token);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[red]Termination requested. Cleaning up...[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
            }
        }, magnetArg, typeOption, titleOption, yearOption, seasonOption);

        rootCommand.AddCommand(addCommand);

        // ── set Command ────────────────────────────────────────────────────
        var setCommand = new Command("set", "Set a configuration value");
        var keyArg = new Argument<string>("key") { Description = "Configuration key (e.g. real_debrid_api_key)" };
        var valueArg = new Argument<string>("value") { Description = "Configuration value" };
        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);

        setCommand.SetHandler((key, value) =>
        {
            app.SetConfigurationValue(key, value);
        }, keyArg, valueArg);

        rootCommand.AddCommand(setCommand);

        // ── list Command ───────────────────────────────────────────────────
        var listCommand = new Command("list", "List all current configurations");
        listCommand.SetHandler(() =>
        {
            app.ListConfiguration();
        });

        rootCommand.AddCommand(listCommand);

        // ── Interactive mode (no args) ─────────────────────────────────────
        if (args.Length == 0)
        {
            await app.RunInteractiveAsync(cts.Token);
            return 0;
        }

        return await rootCommand.InvokeAsync(args);
    }
}
