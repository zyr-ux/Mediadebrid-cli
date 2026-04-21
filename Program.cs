using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.CommandLine.Help;
using MediaDebrid_cli.Services;
using MediaDebrid_cli.Tui;
using MediaDebrid_cli.Models;
using Spectre.Console;

namespace MediaDebrid_cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Settings.Load();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };



        var app = new TuiApp();

        var rootCommand = new RootCommand(Utils.GetRootHelpDescription())
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
            await app.RunAsync(magnet, type, title, year, season, null, showLogo: true, cts.Token);
        }, magnetArg, typeOption, titleOption, yearOption, seasonOption);

        rootCommand.AddCommand(addCommand);

        // ── set Command ────────────────────────────────────────────────────
        var setCommand = new Command("set", "Set a configuration value");
        var keyArg = new Argument<string>("key") { Description = "Configuration key" };
        var valueArg = new Argument<string>("value") { Description = "Configuration value" };
        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);

        setCommand.SetHandler(app.SetConfigurationValue, keyArg, valueArg);

        rootCommand.AddCommand(setCommand);

        // ── list Command ───────────────────────────────────────────────────
        var listCommand = new Command("list", "List all current configurations");
        listCommand.SetHandler(app.ListConfiguration);

        rootCommand.AddCommand(listCommand);
        
        // ── resume Command ──────────────────────────────────────────────────
        var resumeCommand = new Command("resume", "Resume a download from a .mdebrid file");
        var pathArg = new Argument<string>("path") { Description = "Path to the .mdebrid file" };
        resumeCommand.AddArgument(pathArg);
        resumeCommand.SetHandler(async (path) =>
        {
            await app.RunResumeAsync(path, cts.Token);
        }, pathArg);
        
        rootCommand.AddCommand(resumeCommand);

        var parser = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseHelp(ctx =>
            {
                ctx.HelpBuilder.CustomizeLayout(helpContext =>
                {
                    if (helpContext.Command is RootCommand)
                    {
                        return new HelpSectionDelegate[]
                        {
                            ctx => 
                            {
                                TuiApp.ShowLogo();
                                ctx.Output.Write(Utils.GetRootHelpDescription());
                            }
                        };
                    }
                    return HelpBuilder.Default.GetLayout();
                });
            })
            .Build();

        // ── Interactive mode (no args) ─────────────────────────────────────
        // ── Execute ────────────────────────────────────────────────────────
        try
        {
            if (args.Length != 0) return await parser.InvokeAsync(args);
            await app.RunInteractiveAsync(cts.Token);
            return 0;

        }
        catch (RealDebridApiException ex)
        {
            ex.Print();
            return 1;
        }
        catch (MagnetException ex)
        {
            ex.Print();
            return 1;
        }
        catch (ConfigurationException ex)
        {
            ex.Print();
            return 1;
        }
        catch (DownloadException ex)
        {
            ex.Print();
            return 1;
        }
        catch (RealDebridClientException ex)
        {
            ex.Print();
            return 1;
        }
        catch (OperationCanceledException ex)
        {
            var tex = ex as TerminationException ?? new TerminationException();
            tex.Print();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
