using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightSignal.ControlPlane.Configuration;
using NightSignal.ControlPlane.Content;
using NightSignal.ControlPlane.Security;

namespace NightSignal.Services.Tests.Infrastructure;

/// <summary>Injectable clock. Timers still run in real time; only "now" is controlled.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;
    public ManualClock() : this(DateTimeOffset.UtcNow) { }
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

/// <summary>A temporary folder deleted at the end of the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ns-tests-" + Guid.NewGuid().ToString("N"));
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class TestHostEnvironment(string contentRoot, string environment = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environment;
    public string ApplicationName { get; set; } = "NightSignal.ControlPlane";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Dev accounts generated per test run (random passwords, low PBKDF2 cost for speed).</summary>
public sealed record DevAccountFixture(string AccountId, string Email, string Password);

public static class TestData
{
    static readonly Lazy<ContentService> SharedContent = new(() =>
        new ContentService(Options.Create(new ContentOptions()), NullLogger<ContentService>.Instance));

    /// <summary>The real generated catalogue (copied from Assets/Content/Data/generated), validated by Core.</summary>
    public static ContentService Content => SharedContent.Value;

    public static IReadOnlyList<DevAccountFixture> WriteSeed(string path, int count)
    {
        var accounts = Enumerable.Range(1, count).Select(i => new DevAccountFixture(
            $"00000000-0000-4000-8000-{i:000000000000}", $"driver{i}@devauth.localhost",
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12)))).ToList();
        var seed = new
        {
            accounts = accounts.Select(a => new { accountId = a.AccountId, email = a.Email, passwordHash = Pbkdf2Password.Hash(a.Password, 10_000) }),
        };
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(seed));
        return accounts;
    }
}

/// <summary>Captures every log line so tests can assert credentials never reach the logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{level} {category}: {formatter(state, e)} {e}");
    }
}
