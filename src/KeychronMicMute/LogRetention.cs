using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace KeychronMicMute;

internal static class LogRetention
{
    private const int RetentionDays = 30;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeychronMicMute");
    private static readonly string[] LogPaths =
    [
        Path.Combine(DirectoryPath, "helper.log"),
        Path.Combine(DirectoryPath, "helper.log.1")
    ];

    private static Timer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        PruneSafely();
        _timer = new Timer(
            static _ => PruneSafely(),
            null,
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1));
    }

    private static void PruneSafely()
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
            foreach (var path in LogPaths) PruneFile(path, cutoff);
        }
        catch
        {
            // Logging must never be able to break the helper.
        }
    }

    private static void PruneFile(string path, DateTimeOffset cutoff)
    {
        if (!File.Exists(path)) return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd();
        if (text.Length == 0) return;

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var firstRetainedLine = -1;
        var sawTimestamp = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryReadTimestamp(lines[i], out var timestamp)) continue;
            sawTimestamp = true;
            if (timestamp >= cutoff)
            {
                firstRetainedLine = i;
                break;
            }
        }

        if (!sawTimestamp) return;

        var retained = firstRetainedLine >= 0
            ? string.Join(Environment.NewLine, lines[firstRetainedLine..])
            : string.Empty;

        stream.Position = 0;
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.Write(retained);
        writer.Flush();
    }

    private static bool TryReadTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var separator = line.IndexOf(' ');
        if (separator <= 0) return false;

        return DateTimeOffset.TryParse(
            line.AsSpan(0, separator),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);
    }
}
