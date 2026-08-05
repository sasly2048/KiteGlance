using System.IO;
using System.Text;

namespace KiteGlance.Services;

/// <summary>
/// A minimal, dependency-free logger. Writes timestamped lines to a single
/// rotating file under %APPDATA%\KiteGlance\logs, so an unhandled exception on
/// a user's machine leaves a trail we can ask them to send, without pulling in
/// Serilog/NLog or breaking the project's no-external-dependencies rule.
///
/// Deliberately simple: synchronous, lock-guarded, best-effort. Logging must
/// never throw into the code it is trying to diagnose, so every path swallows
/// its own failures. It is not a high-throughput logger and does not try to
/// be -- a desktop widget writes a handful of lines per refresh.
///
/// No holdings, NAVs, tokens, or personal identifiers are ever logged. Only
/// events and error text. See the redaction note in <see cref="Write"/>.
/// </summary>
public static class Log
{
    public enum Level { Debug, Info, Warn, Error }

    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KiteGlance", "logs");

    private static readonly string FilePath = Path.Combine(Dir, "kiteglance.log");

    // Rotate when the file passes this size, keeping one .1 backup. A widget
    // will take a long time to write 1 MB of terse log lines.
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Minimum level actually written. Debug is enabled only when the same
    /// KITEGLANCE_DEBUG=1 switch that turns on the API dump is set, so normal
    /// runs stay quiet.
    /// </summary>
    public static Level Minimum { get; set; } =
        Environment.GetEnvironmentVariable("KITEGLANCE_DEBUG") == "1"
            ? Level.Debug
            : Level.Info;

    public static void Debug(string message) => Write(Level.Debug, message, null);
    public static void Info(string message) => Write(Level.Info, message, null);
    public static void Warn(string message) => Write(Level.Warn, message, null);

    public static void Error(string message, Exception? ex = null) =>
        Write(Level.Error, message, ex);

    private static void Write(Level level, string message, Exception? ex)
    {
        if (level < Minimum) return;

        // Redaction: callers are responsible for not passing secrets, but as a
        // backstop we never format holding values or tokens here -- this logger
        // only ever receives event strings and exception text by design.
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                Rotate();

                var sb = new StringBuilder();
                // InvariantCulture: under a non-Gregorian system calendar
                // (Thai Buddhist, Hijri) "yyyy" renders the era year, so log
                // lines come out dated 2569 and stop sorting against anything.
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",
                    System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("  [").Append(level.ToString().ToUpperInvariant()).Append("]  ");
                sb.Append(message);

                if (ex is not null)
                {
                    sb.Append("  ::  ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message);

                    if (Minimum == Level.Debug && ex.StackTrace is not null)
                        sb.Append('\n').Append(ex.StackTrace);
                }

                sb.Append('\n');
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // A logger that throws is worse than no logger. Give up silently.
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxBytes) return;

            var backup = FilePath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch
        {
            // If rotation fails the log simply grows; not worth crashing over.
        }
    }
}
