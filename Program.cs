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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
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


        // ── unres Command ──────────────────────────────────────────────────
        var unresCommand = new Command("unres", "Generate unrestricted links instead of downloading");
        var unresMagnetArg = new Argument<string?>("magnet", () => null) { Description = "Optional magnet link to process" };
        
        unresCommand.AddArgument(unresMagnetArg);

        unresCommand.SetHandler(async (magnet) =>
        {
            if (string.IsNullOrWhiteSpace(magnet))
            {
                await app.RunInteractiveAsync(cts.Token, generateUnresLinks: true);
            }
            else
            {
                await app.RunAsync(magnet, null, null, showLogo: true, cts.Token, forceResume: false, generateUnresLinks: true);
            }
        }, unresMagnetArg);

        rootCommand.AddCommand(unresCommand);

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
            AnsiConsole.MarkupLine($"[red]Unexpected error ({Markup.Escape(ex.GetType().Name)}):[/] [white]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
