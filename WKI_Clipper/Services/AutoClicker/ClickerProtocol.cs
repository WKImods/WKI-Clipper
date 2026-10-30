using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WKI_Clipper.Models;

namespace WKI_Clipper.Services.AutoClicker;

/// <summary>
/// One message of the clicker pipe (design section 6): a JSON object on one line, the kind in the "t" property.
/// Main to host: hello, config, lock, unlock, start, stop, picker, shutdown, patternData. Host to main: hello, state, runEnded, log,
/// patternRecorded. Both sides use the same records and <see cref="ClickerProtocol"/>; a message type one side does not know is skipped.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ConfigMessage), "config")]
[JsonDerivedType(typeof(LockMessage), "lock")]
[JsonDerivedType(typeof(UnlockMessage), "unlock")]
[JsonDerivedType(typeof(StartMessage), "start")]
[JsonDerivedType(typeof(StopMessage), "stop")]
[JsonDerivedType(typeof(PickerMessage), "picker")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(RunEndedMessage), "runEnded")]
[JsonDerivedType(typeof(LogMessage), "log")]
[JsonDerivedType(typeof(PatternDataMessage), "patternData")]
[JsonDerivedType(typeof(PatternRecordedMessage), "patternRecorded")]
public abstract record ClickerMessage;

/// <summary>First message of each side. <paramref name="V"/> is the protocol version, <paramref name="Pid"/> the sender's process id (host only).</summary>
public sealed record HelloMessage(int V, int Pid = 0) : ClickerMessage;

/// <summary>The settings the host works from. The wire form leaves out the presets and the statistics (the host needs neither).</summary>
public sealed record ConfigMessage(ClickerSettings Settings) : ClickerMessage;

/// <summary>The clipper locks the clicker (a blacklisted game runs): a running clicker stops, starts are refused. <paramref name="Reason"/> is free text for the log.</summary>
public sealed record LockMessage(string Reason = "") : ClickerMessage;

public sealed record UnlockMessage : ClickerMessage;

/// <summary>Start from the UI (same rules as a hotkey start: lock, picker and master switch still apply).</summary>
public sealed record StartMessage : ClickerMessage;

public sealed record StopMessage : ClickerMessage;

/// <summary>A picker window opened or closed in the clipper; hotkeys and zones are ignored while it is open and a running clicker stops.</summary>
public sealed record PickerMessage(bool Active) : ClickerMessage;

public sealed record ShutdownMessage : ClickerMessage;

/// <summary>
/// The host's state, sent on every change and at most 10 times a second while running. <paramref name="Reason"/> is the
/// <see cref="StopReason"/> name of the last run's end (null while running or before the first run); <paramref name="BlockedByElevated"/>
/// is the program (exe file name) whose elevated window swallows the clicks, or null.
/// </summary>
/// <remarks>
/// <paramref name="MasterAllowed"/> came later: a line without it reads as "allowed". The mouse patterns (v2) add
/// <paramref name="Recording"/> (a pattern is being recorded), <paramref name="Playing"/> (the ids of the patterns playing, comma-joined;
/// null when none) and <paramref name="PatternsBlockedBy"/> (exe file name of the elevated window that refused the pattern moves); a
/// line without them reads as idle. <paramref name="Armed"/> is the clicker's own switch ("Clicker bereit") from the last config.
/// </remarks>
public sealed record StateMessage(bool Running, bool Armed, bool Locked, string? Reason, long Clicks, double Cps, long ElapsedMs,
    string? BlockedByElevated, bool MasterAllowed = true, bool Recording = false, string? Playing = null,
    string? PatternsBlockedBy = null) : ClickerMessage;

/// <summary>
/// A run ended. <paramref name="Reason"/> is a <see cref="StopReason"/> name; <paramref name="Detail"/> the number the reason refers to
/// (the click limit, the time limit, the program that is not on the whitelist) for <see cref="StopReasonText.Describe"/>.
/// </summary>
public sealed record RunEndedMessage(DateTime StartUtc, double Seconds, long Clicks, string Reason, string? Detail = null) : ClickerMessage;

/// <summary>A log line of the host (warnings and errors); <paramref name="Level"/> is "info", "warn" or "error".</summary>
public sealed record LogMessage(string Level, string Text) : ClickerMessage;

/// <summary>
/// The samples of one mouse pattern (mouse-patterns design 5.2), main to host: after the config at the handshake and whenever a
/// recording changed, never inside the config. <paramref name="Data"/> is <see cref="PatternCodec"/> text; <paramref name="Revision"/>
/// is the <see cref="MousePattern.Revision"/> the samples belong to (the host plays them only while the config names the same one).
/// </summary>
public sealed record PatternDataMessage(string Id, int Revision, string Data) : ClickerMessage;

/// <summary>
/// A pattern recording ended in the host. <paramref name="Outcome"/> is a <see cref="PatternRecordingOutcome"/> name;
/// <paramref name="Data"/> (<see cref="PatternCodec"/> text) is set for Ok and Cut only; <paramref name="Program"/> is the exe file name
/// of the program in front when the recording started (null when unknown); <paramref name="DurationMs"/> the length of the motion.
/// </summary>
public sealed record PatternRecordedMessage(string Outcome, string? Data, string? Program, double DurationMs) : ClickerMessage;

public enum DecodeStatus { Ok, Unknown, Malformed }

/// <summary>What <see cref="ClickerProtocol.Decode"/> made of one line: a message, a type this side does not know (skip it) or garbage.</summary>
public readonly record struct ClickerDecode(DecodeStatus Status, ClickerMessage? Message, string? Detail = null)
{
    public static ClickerDecode Ok(ClickerMessage message) => new(DecodeStatus.Ok, message);
}

/// <summary>The JSON line codec shared by the clipper and the helper process.</summary>
public static class ClickerProtocol
{
    /// <summary>Bumped on an incompatible change; a host that sees another version ends. 2: mouse patterns.</summary>
    public const int Version = 2;

    /// <summary>A line longer than this is a broken or hostile peer, not a message.</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    private static readonly string[] KnownTypes =
        { "hello", "config", "lock", "unlock", "start", "stop", "picker", "shutdown", "state", "runEnded", "log", "patternData", "patternRecorded" };

    private static readonly Regex PipeNamePattern = new(@"\AWKI_Clipper\.Clicker\.[0-9]{1,10}\.[0-9a-f]{16}\z", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ClickerSettings)) return;
            // The host needs neither the saved presets nor the statistics: keeping them off the wire keeps a config push small.
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name is "presets" or "stats") info.Properties.RemoveAt(i);
        });
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false
        };
    }

    /// <summary>The message as one line of JSON (no line ending).</summary>
    public static string Encode(ClickerMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>
    /// Reads one line. An unknown "t" is not an error (newer peers may send more); a line that is no JSON object, has no "t" or
    /// does not fit its message type is <see cref="DecodeStatus.Malformed"/>. A config message comes back as the wire form, so
    /// callers run <see cref="ClickerSettingsRules.Normalize"/> on it.
    /// </summary>
    public static ClickerDecode Decode(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new(DecodeStatus.Malformed, null, "empty line");
        try
        {
            using (var doc = JsonDocument.Parse(line))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
                    return new(DecodeStatus.Malformed, null, "no message type");
                var type = t.GetString();
                if (Array.IndexOf(KnownTypes, type) < 0) return new(DecodeStatus.Unknown, null, type);
            }
            // The discriminator must be the first property (STJ requirement); our own Encode always writes it first.
            var message = JsonSerializer.Deserialize<ClickerMessage>(line, Options);
            return message is null ? new(DecodeStatus.Malformed, null, "null message") : ClickerDecode.Ok(message);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new(DecodeStatus.Malformed, null, ex.Message);
        }
    }

    /// <summary>"WKI_Clipper.Clicker.&lt;parentPid&gt;.&lt;16 hex&gt;": the pipe the clipper opens for one helper process.</summary>
    public static string NewPipeName(int parentPid)
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return $"WKI_Clipper.Clicker.{parentPid}.{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    /// <summary>true for a name <see cref="NewPipeName"/> could have made (the host refuses anything else on its command line).</summary>
    public static bool IsValidPipeName(string? name) => name is not null && PipeNamePattern.IsMatch(name);
}

/// <summary>
/// A <see cref="ClickerMessage"/> stream over a byte stream (the named pipe): one JSON message per line, UTF-8. Reads and writes may
/// run concurrently with each other; writes from several threads are serialized. A broken pipe surfaces as an IOException.
/// </summary>
public sealed class ClickerChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _buffer = new byte[8192];
    private readonly MemoryStream _line = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _pos, _len;

    public ClickerChannel(Stream stream, int maxLineBytes = ClickerProtocol.MaxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>
    /// The next line decoded, or null at the end of the stream. Blank lines are skipped. A line over the size limit throws an
    /// <see cref="InvalidDataException"/> (the connection is not worth keeping).
    /// </summary>
    public async Task<ClickerDecode?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length == 0) continue;
            return ClickerProtocol.Decode(line);
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_pos < _len)
            {
                int nl = Array.IndexOf(_buffer, (byte)'\n', _pos, _len - _pos);
                int end = nl >= 0 ? nl : _len;
                if (_line.Length + (end - _pos) > _maxLineBytes)
                    throw new InvalidDataException("clicker pipe: line longer than " + _maxLineBytes + " bytes");
                _line.Write(_buffer, _pos, end - _pos);
                if (nl >= 0)
                {
                    _pos = nl + 1;
                    var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                    _line.SetLength(0);
                    return text;
                }
                _pos = _len;
            }
            int n = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return null;   // end of stream; a half line is dropped
            _pos = 0;
            _len = n;
        }
    }

    public async Task WriteAsync(ClickerMessage message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(ClickerProtocol.Encode(message) + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _writeLock.Dispose();
    }
}
