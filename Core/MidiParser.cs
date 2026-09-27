using System.Buffers.Binary;
using System.Text;

namespace GoMidi.Core;

/// <summary>A single note recovered from a MIDI track, with times already in seconds.</summary>
public sealed class RawNote
{
    public double StartSeconds { get; init; }
    public int Pitch { get; init; }
    public double Duration { get; init; }
    public int TrackIndex { get; init; }
    public int Channel { get; init; }
    public int Velocity { get; init; }
    public int Program { get; init; } = -1;

    public double EndSeconds => StartSeconds + Duration;
}

/// <summary>One MTrk chunk: its display name plus every note it contains.</summary>
public sealed class MidiTrackInfo
{
    public string Name { get; set; } = string.Empty;
    public int Channel { get; set; } = -1;
    public List<RawNote> Notes { get; } = new();
    public int NoteCount => Notes.Count;
}

/// <summary>
/// Standard MIDI File parser for Format 0 and Format 1, with a full tempo map so
/// tick positions convert to wall-clock seconds across tempo changes.
/// Ported from <c>MidiParser.cpp</c>.
/// </summary>
public sealed class MidiFile
{
    private readonly List<long> _tempoTicks = new();
    private readonly List<double> _tempoSeconds = new();
    private readonly List<int> _tempoUsecPerQuarter = new();
    private double _smpteTicksPerSecond;
    private int _lastTempoIndex;

    public List<MidiTrackInfo> Tracks { get; } = new();
    public double Length { get; private set; }
    public int Division { get; private set; } = 480;
    public int Format { get; private set; } = 1;
    public bool IsValid { get; private set; }
    public string ErrorMessage { get; private set; } = string.Empty;
    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public double InitialBpm { get; private set; } = 120.0;
    public int InitialTimeSignatureNumerator { get; private set; } = 4;
    public int InitialTimeSignatureDenominator { get; private set; } = 4;

    public static MidiFile? Load(string path, out string error)
    {
        try
        {
            var midi = new MidiFile(path);
            error = midi.ErrorMessage;
            return midi.IsValid ? midi : null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public MidiFile(string filepath)
    {
        FilePath = filepath;
        try
        {
            byte[] data = File.ReadAllBytes(filepath);
            Parse(data);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsValid = false;
        }
    }

    // -- parsing ------------------------------------------------------------

    private void Parse(byte[] data)
    {
        if (data.Length < 14 || Encoding.ASCII.GetString(data, 0, 4) != "MThd")
        {
            ErrorMessage = "不是有效的 MIDI 文件（缺少 MThd 头）";
            return;
        }

        int headerLen = (int)ReadU32(data, 4);
        if (headerLen < 6 || 8 + headerLen > data.Length)
        {
            ErrorMessage = "MIDI 头长度无效";
            return;
        }

        Format = ReadU16(data, 8);
        int trackCount = ReadU16(data, 10);
        Division = ReadU16(data, 12);

        InitDivision();
        if (Division == 0)
        {
            ErrorMessage = "MIDI 时间划分无效";
            return;
        }

        // Pass 1: collect tempo / time-signature map plus every note, in ticks.
        var perTrackNotes = new List<PendingNote>[trackCount];
        var perTrackNames = new string[trackCount];
        var perTrackChannels = new int[trackCount];
        var tempoEvents = new List<(int Tick, int Usec)>();
        var timeSigEvents = new List<(int Tick, int Num, int Den)>();
        int globalLastTick = 0;

        int offset = 8 + headerLen;
        for (int t = 0; t < trackCount; t++)
        {
            perTrackNames[t] = string.Empty;
            perTrackChannels[t] = -1;
            perTrackNotes[t] = new List<PendingNote>();

            if (offset + 8 > data.Length || Encoding.ASCII.GetString(data, offset, 4) != "MTrk")
            {
                break;
            }

            int trackLen = (int)ReadU32(data, offset + 4);
            int trackStart = offset + 8;
            int trackEnd = Math.Min(trackStart + trackLen, data.Length);

            ParseTrack(data, trackStart, trackEnd, t, perTrackNotes[t], ref perTrackNames[t],
                ref perTrackChannels[t], tempoEvents, timeSigEvents, ref globalLastTick);

            offset = trackStart + trackLen;
        }

        // Tempo and time-signature events are collected in track order, so they
        // must be sorted before the first entry can be called the initial one —
        // the conductor track is not guaranteed to come first.
        tempoEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        timeSigEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        InitTempoMap(tempoEvents);
        if (tempoEvents.Count > 0)
        {
            InitialBpm = 60_000_000.0 / tempoEvents[0].Usec;
        }

        if (timeSigEvents.Count > 0)
        {
            InitialTimeSignatureNumerator = timeSigEvents[0].Num;
            InitialTimeSignatureDenominator = (int)Math.Pow(2, timeSigEvents[0].Den);
        }

        // Pass 2: convert ticks to seconds now that the tempo map is complete.
        double length = 0;
        for (int t = 0; t < trackCount; t++)
        {
            var info = new MidiTrackInfo
            {
                Name = string.IsNullOrWhiteSpace(perTrackNames[t]) ? $"轨道 {t + 1}" : perTrackNames[t],
                Channel = perTrackChannels[t],
            };

            foreach (PendingNote pending in perTrackNotes[t])
            {
                double start = TickToSeconds(pending.StartTick);
                double end = TickToSeconds(pending.EndTick);
                if (end <= start)
                {
                    end = start + 0.01; // degenerate/zero-length notes still need to sound
                }

                info.Notes.Add(new RawNote
                {
                    StartSeconds = start,
                    Pitch = pending.Pitch,
                    Duration = end - start,
                    TrackIndex = t,
                    Channel = pending.Channel,
                    Velocity = pending.Velocity,
                    Program = pending.Program,
                });

                if (end > length)
                {
                    length = end;
                }
            }

            Tracks.Add(info);
        }

        Length = length;
        IsValid = Tracks.Count > 0;
        if (!IsValid)
        {
            ErrorMessage = "文件中没有可播放的音符";
        }
    }

    private void ParseTrack(
        byte[] data, int start, int end, int trackIndex,
        List<PendingNote> notes, ref string trackName, ref int trackChannel,
        List<(int Tick, int Usec)> tempoEvents,
        List<(int Tick, int Num, int Den)> timeSigEvents,
        ref int globalLastTick)
    {
        int pos = start;
        int tick = 0;
        int runningStatus = 0;
        int program = -1;
        int lastChannel = -1;
        var openNotes = new Dictionary<(int Channel, int Pitch), PendingNote>(16);

        while (pos < end)
        {
            (int delta, int size) = ReadVarLen(data, pos, end);
            pos += size;
            tick += delta;

            if (pos >= end)
            {
                break;
            }

            int status = data[pos];
            if ((status & 0x80) != 0)
            {
                pos++;
                if (status < 0xF0)
                {
                    runningStatus = status;
                }
            }
            else
            {
                status = runningStatus;
                if (status == 0)
                {
                    break; // corrupt stream, nothing to fall back on
                }
            }

            if (status == 0xFF)
            {
                if (pos >= end)
                {
                    break;
                }

                int metaType = data[pos++];
                (int metaLen, int metaSize) = ReadVarLen(data, pos, end);
                pos += metaSize;
                int metaEnd = Math.Min(pos + metaLen, end);

                switch (metaType)
                {
                    case 0x51 when metaLen >= 3 && pos + 3 <= end:
                        int usec = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
                        if (usec > 0)
                        {
                            tempoEvents.Add((tick, usec));
                        }

                        break;

                    case 0x58 when metaLen >= 2 && pos + 2 <= end:
                        timeSigEvents.Add((tick, data[pos], data[pos + 1]));
                        break;

                    case 0x03:
                        trackName = DecodeText(data, pos, metaEnd - pos);
                        break;

                    case 0x2F: // end of track
                        pos = end;
                        metaEnd = end;
                        break;
                }

                pos = metaEnd;
                globalLastTick = Math.Max(globalLastTick, tick);
                continue;
            }

            if (status == 0xF0 || status == 0xF7)
            {
                (int sysLen, int sysSize) = ReadVarLen(data, pos, end);

                // Clamp like the meta branch: a bogus length must not skip the
                // remainder of the track silently.
                pos = Math.Min(pos + sysSize + sysLen, end);
                continue;
            }

            int command = status & 0xF0;
            int channel = status & 0x0F;
            lastChannel = channel;

            switch (command)
            {
                case 0x80:
                case 0x90:
                {
                    if (pos + 2 > end)
                    {
                        pos = end;
                        break;
                    }

                    int pitch = data[pos++];
                    int velocity = data[pos++];
                    bool isNoteOn = command == 0x90 && velocity > 0;

                    if (isNoteOn)
                    {
                        // A repeated note-on for a pitch that is already sounding
                        // would otherwise overwrite the open note and lose it.
                        if (openNotes.Remove((channel, pitch), out PendingNote? previous))
                        {
                            previous.EndTick = Math.Max(tick, previous.StartTick + 1);
                            notes.Add(previous);
                        }

                        openNotes[(channel, pitch)] = new PendingNote
                        {
                            StartTick = tick,
                            EndTick = tick,
                            Pitch = pitch,
                            Channel = channel,
                            Velocity = velocity,
                            Program = program,
                        };
                    }
                    else if (openNotes.Remove((channel, pitch), out PendingNote? pending))
                    {
                        pending.EndTick = tick;
                        notes.Add(pending);
                    }

                    break;
                }

                case 0xC0:
                    if (pos < end)
                    {
                        program = data[pos++];
                    }

                    break;

                case 0xD0:
                    pos += 1;
                    break;

                default: // 0xA0, 0xB0, 0xE0 — two data bytes
                    pos += 2;
                    break;
            }
        }

        // Notes left hanging (missing note-off) are closed at the last tick seen.
        foreach (PendingNote pending in openNotes.Values)
        {
            pending.EndTick = Math.Max(tick, pending.StartTick + 1);
            notes.Add(pending);
        }

        trackChannel = lastChannel;
    }

    private void InitDivision()
    {
        if ((Division & 0x8000) != 0)
        {
            // SMPTE: high byte is a negative frame count, low byte is ticks per frame.
            int frames = 256 - ((Division >> 8) & 0xFF);
            int ticksPerFrame = Division & 0xFF;
            _smpteTicksPerSecond = frames * ticksPerFrame;
        }
        else
        {
            _smpteTicksPerSecond = 0;
        }
    }

    private void InitTempoMap(List<(int Tick, int Usec)> tempoEvents)
    {
        _tempoTicks.Clear();
        _tempoSeconds.Clear();
        _tempoUsecPerQuarter.Clear();
        _lastTempoIndex = 0;

        if (_smpteTicksPerSecond > 0)
        {
            return; // absolute time base — tempo events do not affect the clock
        }

        tempoEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        // Always anchor at tick 0 so lookups before the first tempo event work.
        _tempoTicks.Add(0);
        _tempoSeconds.Add(0);
        _tempoUsecPerQuarter.Add(tempoEvents.Count > 0 ? tempoEvents[0].Usec : 500_000);

        double seconds = 0;
        int previousTick = 0;
        int previousUsec = _tempoUsecPerQuarter[0];
        bool firstTempoApplied = tempoEvents.Count > 0 && tempoEvents[0].Tick <= 0;

        foreach ((int tick, int usec) in tempoEvents)
        {
            if (tick <= 0)
            {
                // Only the first tick-0 tempo wins; a later track restating one
                // must not silently override the established initial tempo.
                if (!firstTempoApplied)
                {
                    firstTempoApplied = true;
                    previousUsec = usec;
                    _tempoUsecPerQuarter[0] = usec;
                }

                continue;
            }

            if (tick <= previousTick)
            {
                continue;
            }

            seconds += (tick - previousTick) * previousUsec / (Division * 1_000_000.0);
            _tempoTicks.Add(tick);
            _tempoSeconds.Add(seconds);
            _tempoUsecPerQuarter.Add(usec);
            previousTick = tick;
            previousUsec = usec;
        }
    }

    private double TickToSeconds(int tick)
    {
        if (_smpteTicksPerSecond > 0)
        {
            return tick / _smpteTicksPerSecond;
        }

        if (_tempoTicks.Count == 0)
        {
            return tick * 500_000.0 / (Division * 1_000_000.0);
        }

        // Tempo lookups during parsing are almost always sequential; start from
        // the cached index and clamp, giving O(1) amortized access.
        int index = Math.Clamp(_lastTempoIndex, 0, _tempoTicks.Count - 1);
        if (_tempoTicks[index] > tick)
        {
            index = 0;
        }

        while (index + 1 < _tempoTicks.Count && _tempoTicks[index + 1] <= tick)
        {
            index++;
        }

        _lastTempoIndex = index;
        return _tempoSeconds[index]
            + (tick - _tempoTicks[index]) * _tempoUsecPerQuarter[index] / (Division * 1_000_000.0);
    }

    // -- primitives ---------------------------------------------------------

    private static (int Value, int Size) ReadVarLen(byte[] data, int offset, int end)
    {
        int value = 0;
        int size = 0;
        while (offset + size < end && size < 4)
        {
            byte b = data[offset + size];
            value = (value << 7) | (b & 0x7F);
            size++;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return (value, Math.Max(size, 1));
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : (ushort)0;

    private static uint ReadU32(byte[] data, int offset) =>
        offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : 0u;

    private static string DecodeText(byte[] data, int start, int len)
    {
        if (len <= 0 || start < 0 || start + len > data.Length)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(data, start, len).Trim('\0', ' ', '\r', '\n');
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class PendingNote
    {
        public int StartTick;
        public int EndTick;
        public int Pitch;
        public int Channel;
        public int Velocity;
        public int Program;
    }
}

/// <summary>Note-name conversion shared by the parser consumers and the keymap editor.</summary>
public static class NoteNames
{
    private static readonly string[] Sharps =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Scientific pitch notation: MIDI 60 → "C4".</summary>
    public static string FromPitch(int pitch)
    {
        if (pitch < 0 || pitch > 127)
        {
            return "?";
        }

        int octave = pitch / 12 - 1;
        return $"{Sharps[pitch % 12]}{octave}";
    }

    /// <summary>Accepts "C4", "C#4", "Db4", "c4" or a bare MIDI number.</summary>
    public static bool TryParse(string text, out int pitch)
    {
        pitch = -1;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();

        if (int.TryParse(value, out int numeric))
        {
            if (numeric is >= 0 and <= 127)
            {
                pitch = numeric;
                return true;
            }

            return false;
        }

        int i = 0;
        char letter = char.ToUpperInvariant(value[i++]);
        int semitone = letter switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };

        if (semitone < 0)
        {
            return false;
        }

        if (i < value.Length && (value[i] == '#' || value[i] == 'b' || value[i] == '♯' || value[i] == '♭'))
        {
            semitone += value[i] == '#' || value[i] == '♯' ? 1 : -1;
            i++;
        }

        if (i >= value.Length || !int.TryParse(value[i..], out int octave))
        {
            return false;
        }

        int result = (octave + 1) * 12 + semitone;
        if (result is < 0 or > 127)
        {
            return false;
        }

        pitch = result;
        return true;
    }
}
