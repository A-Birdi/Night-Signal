using NightSignal.ControlPlane;
using NightSignal.ControlPlane.Security;

// Utility mode for creating DevAuth seed entries without putting plaintext in a command line:
//   echo <password> | dotnet run --project Services/ControlPlane --no-launch-profile -- hash-password
if (args.Length > 0 && args[0] == "hash-password")
{
    string? password = Console.In.ReadLine();
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Pipe the password on standard input.");
        return 1;
    }
    Console.WriteLine(Pbkdf2Password.Hash(password));
    return 0;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = RedactingJsonFormatter.Name)
    .AddConsoleFormatter<RedactingJsonFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();
builder.Services.AddControlPlane();

WebApplication app = builder.Build();
app.UseControlPlane();
app.Run();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
