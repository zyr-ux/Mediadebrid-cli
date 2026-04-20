using Spectre.Console;

namespace MediaDebrid_cli.Models;

public interface IPrintableException
{
    void Print();
}

// Robust Polymorphic termination exception handling
public class TerminationException : OperationCanceledException, IPrintableException
{
    private readonly string? _customMessage;
    private bool _wasPrinted;

    public TerminationException(string? customMessage = null) : base()
    {
        _customMessage = customMessage;
    }

    public void Print()
    {
        if (_wasPrinted) return;

        if (_customMessage != null)
        {
            if (!string.IsNullOrEmpty(_customMessage))
            {
                AnsiConsole.MarkupLine(_customMessage);
            }
        }
        else
        {
            AnsiConsole.MarkupLine("\n[red]Operation cancelled. Exiting...[/]");
        }

        _wasPrinted = true;
    }
}

public class RealDebridApiException : HttpRequestException, IPrintableException
{
    public string Error { get; }
    public int ErrorCode { get; }

    public RealDebridApiException(string error, int errorCode, System.Net.HttpStatusCode statusCode) 
        : base($"Real-Debrid API Error: {error} (Code: {errorCode})", null, statusCode)
    {
        Error = error;
        ErrorCode = errorCode;
    }

    public void Print()
    {
        string msg = ErrorCode == 35 
            ? "\n[red]✗[/] Real-Debrid has blocked this magnet as an infringing file (Code 35)."
            : $"\n[red]✗[/] Real-Debrid API Error: [white]{Markup.Escape(Error)}[/] (Code: {ErrorCode})";
        AnsiConsole.MarkupLine(msg);
    }
}

public class ConfigurationException : Exception, IPrintableException
{
    public ConfigurationException(string message) : base(message) { }

    public void Print()
    {
        AnsiConsole.MarkupLine($"\n[red]✗[/] Configuration Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class MagnetException : Exception, IPrintableException
{
    public MagnetException(string message) : base(message) { }

    public void Print()
    {
        AnsiConsole.MarkupLine($"\n[red]✗[/] Magnet Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class DownloadException : Exception, IPrintableException
{
    public DownloadException(string message, Exception? innerException = null) 
        : base(message, innerException) { }

    public void Print()
    {
        AnsiConsole.MarkupLine($"\n[red]✗[/] Download Error: [white]{Markup.Escape(Message)}[/]");
    }
}

public class RealDebridClientException : Exception, IPrintableException
{
    public RealDebridClientException(string message, Exception? innerException = null) 
        : base(message, innerException) { }

    public void Print()
    {
        AnsiConsole.MarkupLine($"\n[red]✗[/] Client Error: [white]{Markup.Escape(Message)}[/]");
    }
}
