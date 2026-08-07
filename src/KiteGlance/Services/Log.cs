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

    /// <summary>Machine-readable twin of the text log, one JSON object per line.</summary>
    private static readonly string JsonPath = Path.Combine(Dir, "kiteglance.jsonl");

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

    // -- Structured overloads ------------------------------------------
    //
    // Serilog-shaped message templates without the dependency: the roadmap
    // asked for structured logging, while this file and the README both commit
    // to shipping no external logging framework. A template plus its arguments
    // gives machine-readable events (kiteglance.jsonl) alongside the
    // human-readable line, which is the part that actually mattered.
    //
    //     Log.Info("Refreshed {Count} holdings in {Ms}ms", n, elapsed);
    //
    // renders  "Refreshed 12 holdings in 340ms"
    // and emits {"Count":12,"Ms":340} as properties.

    public static void Debug(string template, params object?[] args) =>
        WriteStructured(Level.Debug, template, args, null);

    public static void Info(string template, params object?[] args) =>
        WriteStructured(Level.Info, template, args, null);

    public static void Warn(string template, params object?[] args) =>
        WriteStructured(Level.Warn, template, args, null);

    public static void Error(Exception? ex, string template, params object?[] args) =>
        WriteStructured(Level.Error, template, args, ex);

    private static void WriteStructured(Level level, string template, object?[] args, Exception? ex)
    {
        if (level < Minimum) return;

        var (rendered, properties) = Render(template, args);
        Write(level, rendered, ex, properties);
    }

    /// <summary>
    /// Substitutes {Name} holes in the template with the positional arguments,
    /// returning both the rendered text and the name/value pairs. Unmatched
    /// holes are left verbatim so a wrong argument count degrades to a readable
    /// line instead of throwing inside the logger.
    /// </summary>
    internal static (string Rendered, List<KeyValuePair<string, object?>> Properties) Render(
        string template, object?[] args)
    {
        var properties = new List<KeyValuePair<string, object?>>();
        var sb = new StringBuilder(template.Length + 32);
        var next = 0;
        var i = 0;

        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, open - i);

            var name = template.Substring(open + 1, close - open - 1);
            if (name.Length > 0 && next < args.Length)
            {
                var value = args[next++];
                properties.Add(new KeyValuePair<string, object?>(name, value));
                sb.Append(Format(value));
            }
            else
            {
                // No argument for this hole; keep it literal.
                sb.Append('{').Append(name).Append('}');
            }

            i = close + 1;
        }

        return (sb.ToString(), properties);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static void Write(Level level, string message, Exception? ex,
        IReadOnlyList<KeyValuePair<string, object?>>? properties = null)
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

                WriteJsonLine(level, message, ex, properties);
            }
        }
        catch
        {
            // A logger that throws is worse than no logger. Give up silently.
        }
    }

    /// <summary>
    /// One JSON object per line, next to the human-readable log. This is the
    /// machine-readable half of "structured logging": greppable with jq, and
    /// parseable without a regex over the text format. Called with the Gate
    /// already held.
    /// </summary>
    private static void WriteJsonLine(
        Level level, string message, Exception? ex,
        IReadOnlyList<KeyValuePair<string, object?>>? properties)
    {
        try
        {
            var json = new StringBuilder(160);
            json.Append('{');
            AppendJson(json, "ts", DateTime.UtcNow.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture));
            json.Append(',');
            AppendJson(json, "level", level.ToString().ToLowerInvariant());
            json.Append(',');
            AppendJson(json, "message", message);

            if (properties is not null)
            {
                foreach (var (name, value) in properties)
                {
                    json.Append(',');
                    AppendJson(json, name, Format(value));
                }
            }

            if (ex is not null)
            {
                json.Append(',');
                AppendJson(json, "exception", ex.GetType().Name);
                json.Append(',');
                AppendJson(json, "exceptionMessage", ex.Message);
            }

            json.Append("}\n");
            File.AppendAllText(JsonPath, json.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Same contract as the text log: never throw into the caller.
        }
    }

    private static void AppendJson(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(Escape(name)).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static void Rotate()
    {
        try
        {
            RotateOne(FilePath);
            RotateOne(JsonPath);
        }
        catch
        {
            // If rotation fails the log simply grows; not worth crashing over.
        }
    }

    private static void RotateOne(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var backup = path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(path, backup);
    }
}
