using System.Text;
using System.Text.RegularExpressions;

namespace GoMidi.Core;

/// <summary>A virtual key plus its modifier bitmask.</summary>
public readonly record struct KeyMapping(int VkCode, int Modifier)
{
    public bool IsValid => VkCode != 0;
}

/// <summary>
/// Note → keystroke mapping, with the two built-in presets from the original
/// build (FF14 and 燕云十六声) and a text-file round-trip format that tolerates
/// full-width punctuation, GBK/UTF-8 encodings and either note names or raw
/// MIDI numbers.
/// </summary>
public sealed class KeyManager
{
    // Modifier bits — identical to Util::KeyManager.
    public const int ModNone = 0;
    public const int ModShift = 1;
    public const int ModCtrl = 2;
    public const int ModAlt = 4;
    public const int ModMouseLeft = 8;
    public const int ModMouseMiddle = 16;
    public const int ModMouseRight = 32;

    /// <summary>Empty scheme id selects the built-in FF14 layout.</summary>
    public const string SchemeFf14 = "";

    /// <summary>Scheme id for the built-in 燕云十六声 layout.</summary>
    public const string SchemeYys = "@builtin_yys";

    public const string Ff14DisplayName = "默认键位";
    public const string YysDisplayName = "燕云十六声";
    public const int Ff14MinPitch = 48;
    public const int Ff14MaxPitch = 84;
    public const int YysMinPitch = 48;
    public const int YysMaxPitch = 83;

    private static readonly string[] NoteNamesSharp =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Letter / digit / punctuation → virtual key. Mirrors <c>get_vk_map()</c>.</summary>
    private static readonly Dictionary<string, int> VkMap = new(StringComparer.Ordinal)
    {
        ["q"] = 0x51, ["w"] = 0x57, ["e"] = 0x45, ["r"] = 0x52, ["t"] = 0x54,
        ["y"] = 0x59, ["u"] = 0x55, ["i"] = 0x49, ["o"] = 0x4F, ["p"] = 0x50,
        ["a"] = 0x41, ["s"] = 0x53, ["d"] = 0x44, ["f"] = 0x46, ["g"] = 0x47,
        ["h"] = 0x48, ["j"] = 0x4A, ["k"] = 0x4B, ["l"] = 0x4C,
        ["z"] = 0x5A, ["x"] = 0x58, ["c"] = 0x43, ["v"] = 0x56, ["b"] = 0x42,
        ["n"] = 0x4E, ["m"] = 0x4D,
        ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34, ["5"] = 0x35,
        ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39, ["0"] = 0x30,
        ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["'"] = 0xDE,
        ["-"] = 0xBD, ["="] = 0xBB, ["+"] = 0xBB, ["/"] = 0xBF,
        [","] = 0xBC, ["."] = 0xBE, [";"] = 0xBA, ["`"] = 0xC0,
    };

    private static readonly Dictionary<int, string> VkReverseMap = BuildReverseMap();

    private static readonly Dictionary<string, int> PitchNameMap = new(StringComparer.Ordinal)
    {
        ["c"] = 0, ["c#"] = 1, ["db"] = 1, ["d"] = 2, ["d#"] = 3, ["eb"] = 3,
        ["e"] = 4, ["f"] = 5, ["f#"] = 6, ["gb"] = 6, ["g"] = 7, ["g#"] = 8,
        ["ab"] = 8, ["a"] = 9, ["a#"] = 10, ["bb"] = 10, ["b"] = 11,
    };

    private static readonly Regex KeymapLineRegex = new(
        @"(?:音符\s+)?([A-G][#bB]?\d+|\d+)(?:\s*\(.*?\))?[\s]*[:=\-\s]+[\s]*([^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PitchNameRegex = new(
        @"^\s*([A-Ga-g])([#bB]?)(-?\d+)\s*$", RegexOptions.Compiled);

    private static bool _encodingProviderRegistered;

    private readonly Lock _gate = new();
    private readonly KeyMapping[] _lookup = new KeyMapping[128];
    private readonly bool[] _lookupValid = new bool[128];
    private SortedDictionary<int, KeyMapping> _map = new();

    public KeyManager()
    {
        EnsureEncodingProvider();
        ResetToDefault();
    }

    /// <summary>Current scheme id: <see cref="SchemeFf14"/>, <see cref="SchemeYys"/>, or a saved scheme name.</summary>
    public string CurrentScheme { get; private set; } = SchemeFf14;

    /// <summary>Inclusive note range the current scheme covers.</summary>
    public int MinPitch { get; private set; } = Ff14MinPitch;

    public int MaxPitch { get; private set; } = Ff14MaxPitch;

    public string CurrentSchemeDisplayName =>
        CurrentScheme switch
        {
            SchemeFf14 => Ff14DisplayName,
            SchemeYys => YysDisplayName,
            _ => CurrentScheme,
        };

    /// <summary>Narrows or widens the playable range without touching the bindings.</summary>
    public void SetRange(int minPitch, int maxPitch)
    {
        lock (_gate)
        {
            MinPitch = Math.Clamp(Math.Min(minPitch, maxPitch), 0, 127);
            MaxPitch = Math.Clamp(Math.Max(minPitch, maxPitch), 0, 127);
        }
    }

    /// <summary>O(1) cache lookup, matching the original's <c>m_lookup_cache</c>.</summary>
    public KeyMapping GetMapping(int note)
    {
        lock (_gate)
        {
            return note is >= 0 and < 128 && _lookupValid[note] ? _lookup[note] : default;
        }
    }

    public IReadOnlyDictionary<int, KeyMapping> Snapshot()
    {
        lock (_gate)
        {
            return new SortedDictionary<int, KeyMapping>(_map);
        }
    }

    public void SetMap(IEnumerable<KeyValuePair<int, KeyMapping>> mappings)
    {
        lock (_gate)
        {
            _map = new SortedDictionary<int, KeyMapping>();
            foreach ((int pitch, KeyMapping mapping) in mappings)
            {
                if (pitch is >= 0 and < 128 && mapping.IsValid)
                {
                    _map[pitch] = mapping;
                }
            }

            RebuildLookup();
        }
    }

    /// <summary>Applies one note's binding, leaving the rest untouched.</summary>
    public void SetNote(int pitch, KeyMapping mapping)
    {
        if (pitch is < 0 or > 127)
        {
            return;
        }

        lock (_gate)
        {
            if (mapping.IsValid)
            {
                _map[pitch] = mapping;
            }
            else
            {
                _map.Remove(pitch);
            }

            RebuildLookup();
        }
    }

    public void ResetToDefault()
    {
        lock (_gate)
        {
            _map = BuildDefaultMap();
            CurrentScheme = SchemeFf14;
            MinPitch = Ff14MinPitch;
            MaxPitch = Ff14MaxPitch;
            RebuildLookup();
        }
    }

    public void LoadYysPreset()
    {
        lock (_gate)
        {
            _map = BuildYysMap();
            CurrentScheme = SchemeYys;
            MinPitch = YysMinPitch;
            MaxPitch = YysMaxPitch;
            RebuildLookup();
        }
    }

    /// <summary>Applies a saved custom scheme: its bindings plus its note range.</summary>
    public void LoadScheme(string schemeId, IEnumerable<KeyValuePair<int, KeyMapping>> mappings, int minPitch, int maxPitch)
    {
        SetMap(mappings);
        lock (_gate)
        {
            CurrentScheme = schemeId;
            MinPitch = minPitch;
            MaxPitch = maxPitch;
        }
    }

    // -- text file round-trip ----------------------------------------------

    /// <summary>
    /// Reads a <c>.txt</c> keymap. Lines beginning with <c>#</c> or <c>-</c> are
    /// comments; a line looks like <c>音符 60 (C4): q</c> or simply <c>C4 = q</c>.
    /// </summary>
    public bool LoadConfig(string path)
    {
        string content;
        try
        {
            content = ReadFileWithEncodingDetection(path);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var parsed = new SortedDictionary<int, KeyMapping>();
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || line[0] == '-')
            {
                continue;
            }

            line = NormalizeLine(line);
            Match match = KeymapLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string keyPart = match.Groups[1].Value;
            string valuePart = match.Groups[2].Value;

            int pitch;
            if (!int.TryParse(keyPart, out pitch) && !TryParsePitchName(keyPart, out pitch))
            {
                continue;
            }

            if (TryParseKeyString(valuePart, out int vk, out int modifier))
            {
                parsed[pitch] = new KeyMapping(vk, modifier);
            }
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        SetMap(parsed);
        return true;
    }

    /// <summary>Writes the current map back out in the documented format, with a UTF-8 BOM.</summary>
    public bool SaveConfig(string path)
    {
        IReadOnlyDictionary<int, KeyMapping> snapshot = Snapshot();

        var sb = new StringBuilder();
        sb.Append(" ################################################################\n");
        sb.Append(" # MIDI 键位映射配置文件\n");
        sb.Append($" # 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append(" ################################################################\n");
        sb.Append(" #\n");
        sb.Append(" # [编写规则说明]\n");
        sb.Append(" # 1. 每行定义一个音符映射，格式为: 音符(或音名) 分隔符 按键\n");
        sb.Append(" # 2. 音符表示法: 支持 MIDI 编号 (如 60) 或 音名 (如 C4, C#4, Eb4)\n");
        sb.Append(" # 3. 分隔符: 支持 冒号(:)、等号(=)、减号(-)、空格 或 全角符号(：、＝、－)\n");
        sb.Append(" # 4. 修饰符: 在按键后加 '+' 表示 Shift，加 '-' 表示 Ctrl，加 '*' 表示 Alt (可叠加, 如 q+- 表示 Ctrl+Shift+q)\n");
        sb.Append(" # 5. 鼠标键: 用 LMB / MMB / RMB 表示 左/中/右键, 作为修饰符写在按键后 (如 q*LMB 表示 Alt+鼠标左键+q)\n");
        sb.Append(" # 6. 自由度: 所有的符号都不分全角/半角，且不区分大小写\n");
        sb.Append(" #\n");
        sb.Append(" # [示例格式]\n");
        sb.Append(" #   60: z            (半角冒号)\n");
        sb.Append(" #   C4 = x           (音名 + 等号)\n");
        sb.Append(" #   音符 62 (D4)：c  (带备注 + 全角冒号)\n");
        sb.Append(" #   64　v            (全角空格)\n");
        sb.Append(" #\n");
        sb.Append(" ################################################################\n\n");

        foreach ((int pitch, KeyMapping mapping) in snapshot)
        {
            string key = FormatKeyString(mapping.VkCode, mapping.Modifier);
            if (key.Length == 0)
            {
                continue;
            }

            sb.Append($" 音符 {pitch} ({NoteName(pitch)}): {key}\n");
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // -- primitives ---------------------------------------------------------

    public static string NoteName(int pitch) =>
        pitch is < 0 or > 127 ? string.Empty : $"{NoteNamesSharp[pitch % 12]}{pitch / 12 - 1}";

    public static bool TryParsePitchName(string name, out int pitch)
    {
        pitch = -1;
        Match match = PitchNameRegex.Match(name);
        if (!match.Success)
        {
            return false;
        }

        string key = match.Groups[1].Value.ToLowerInvariant();
        string accidental = match.Groups[2].Value;
        if (accidental.Length > 0)
        {
            key += accidental[0] == '#' ? "#" : "b";
        }

        if (!PitchNameMap.TryGetValue(key, out int semitone))
        {
            return false;
        }

        int value = (int.Parse(match.Groups[3].Value) + 1) * 12 + semitone;
        if (value is < 0 or > 127)
        {
            return false;
        }

        pitch = value;
        return true;
    }

    /// <summary>
    /// Parses a binding token such as <c>q</c>, <c>q+</c> (Shift), <c>q-</c> (Ctrl),
    /// <c>q*</c> (Alt) or <c>q*LMB</c> (Alt + left mouse + Q). Suffixes combine and
    /// may appear in any order, but a keyboard key must always be present — the
    /// mouse buttons are modifiers here, not keys in their own right.
    /// </summary>
    public static bool TryParseKeyString(string keyString, out int vk, out int modifier)
    {
        vk = 0;
        modifier = ModNone;

        string s = NormalizeLine(keyString.Trim().ToLowerInvariant());
        if (s.Length == 0)
        {
            return false;
        }

        // Suffixes are peeled from the end; the main key always keeps one character.
        bool popped = true;
        while (popped && s.Length > 1)
        {
            popped = false;

            if (s.Length > 3)
            {
                string tail = s[^3..];
                if (tail == "lmb")
                {
                    modifier |= ModMouseLeft;
                    s = s[..^3];
                    popped = true;
                }
                else if (tail == "mmb")
                {
                    modifier |= ModMouseMiddle;
                    s = s[..^3];
                    popped = true;
                }
                else if (tail == "rmb")
                {
                    modifier |= ModMouseRight;
                    s = s[..^3];
                    popped = true;
                }
            }

            if (!popped && s.Length > 1)
            {
                switch (s[^1])
                {
                    case '+':
                        modifier |= ModShift;
                        s = s[..^1];
                        popped = true;
                        break;
                    case '-':
                        modifier |= ModCtrl;
                        s = s[..^1];
                        popped = true;
                        break;
                    case '*':
                        modifier |= ModAlt;
                        s = s[..^1];
                        popped = true;
                        break;
                }
            }
        }

        s = s.Trim();
        if (!VkMap.TryGetValue(s, out vk))
        {
            return false;
        }

        return true;
    }

    public static string FormatKeyString(int vk, int modifier)
    {
        if (!VkReverseMap.TryGetValue(vk, out string? key))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(key);
        if ((modifier & ModShift) != 0)
        {
            sb.Append('+');
        }

        if ((modifier & ModCtrl) != 0)
        {
            sb.Append('-');
        }

        if ((modifier & ModAlt) != 0)
        {
            sb.Append('*');
        }

        if ((modifier & ModMouseLeft) != 0)
        {
            sb.Append("lmb");
        }

        if ((modifier & ModMouseMiddle) != 0)
        {
            sb.Append("mmb");
        }

        if ((modifier & ModMouseRight) != 0)
        {
            sb.Append("rmb");
        }

        return sb.ToString();
    }

    /// <summary>Human-readable modifier prefix, for the keymap editor grid.</summary>
    public static string DescribeModifier(int modifier)
    {
        var parts = new List<string>(3);
        if ((modifier & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifier & ModCtrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifier & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifier & ModMouseLeft) != 0)
        {
            parts.Add("左键");
        }

        if ((modifier & ModMouseMiddle) != 0)
        {
            parts.Add("中键");
        }

        if ((modifier & ModMouseRight) != 0)
        {
            parts.Add("右键");
        }

        return string.Join('+', parts);
    }

    /// <summary>Friendly name for a virtual key, falling back to the raw code.</summary>
    public static string DescribeKey(int vk)
    {
        string formatted = VkReverseMap.TryGetValue(vk, out string? key) ? key : string.Empty;
        return formatted.Length > 0 ? formatted.ToUpperInvariant() : $"VK 0x{vk:X2}";
    }

    private void RebuildLookup()
    {
        Array.Clear(_lookupValid);
        foreach ((int pitch, KeyMapping mapping) in _map)
        {
            if (pitch is >= 0 and < 128)
            {
                _lookup[pitch] = mapping;
                _lookupValid[pitch] = true;
            }
        }
    }

    private static Dictionary<int, string> BuildReverseMap()
    {
        var reverse = new Dictionary<int, string>();
        foreach ((string key, int vk) in VkMap)
        {
            reverse[vk] = key;
        }

        // Prefer "=" over "+" so Shift suffixes stay unambiguous when re-serialized.
        reverse[0xBB] = "=";
        return reverse;
    }

    /// <summary>FF14 performance layout — the original's <c>init_default_map()</c>, verbatim.</summary>
    private static SortedDictionary<int, KeyMapping> BuildDefaultMap() => new()
    {
        [48] = new KeyMapping(0x49, 0), // I
        [50] = new KeyMapping(0x4F, 0), // O
        [52] = new KeyMapping(0x50, 0), // P
        [53] = new KeyMapping(0xDB, 0), // [
        [55] = new KeyMapping(0xDD, 0), // ]
        [57] = new KeyMapping(0xDC, 0), // \
        [59] = new KeyMapping(0xDE, 0), // '
        [60] = new KeyMapping(0x51, 0), // Q
        [62] = new KeyMapping(0x57, 0), // W
        [64] = new KeyMapping(0x45, 0), // E
        [65] = new KeyMapping(0x52, 0), // R
        [67] = new KeyMapping(0x54, 0), // T
        [69] = new KeyMapping(0x59, 0), // Y
        [71] = new KeyMapping(0x55, 0), // U
        [81] = new KeyMapping(0x4E, 0), // N
        [83] = new KeyMapping(0x4D, 0), // M
        [49] = new KeyMapping(0x38, 0), // 8
        [51] = new KeyMapping(0x39, 0), // 9
        [54] = new KeyMapping(0x30, 0), // 0
        [56] = new KeyMapping(0xBD, 0), // -
        [58] = new KeyMapping(0xBB, 0), // =
        [61] = new KeyMapping(0x32, 0), // 2
        [63] = new KeyMapping(0x33, 0), // 3
        [66] = new KeyMapping(0x35, 0), // 5
        [68] = new KeyMapping(0x36, 0), // 6
        [70] = new KeyMapping(0x37, 0), // 7
        [80] = new KeyMapping(0x48, 0), // H
        [82] = new KeyMapping(0x4A, 0), // J
        [72] = new KeyMapping(0x5A, 0), // Z
        [73] = new KeyMapping(0x53, 0), // S
        [74] = new KeyMapping(0x58, 0), // X
        [75] = new KeyMapping(0x44, 0), // D
        [76] = new KeyMapping(0x43, 0), // C
        [77] = new KeyMapping(0x56, 0), // V
        [78] = new KeyMapping(0x47, 0), // G
        [79] = new KeyMapping(0x42, 0), // B
        [84] = new KeyMapping(0xBF, 0), // /
    };

    /// <summary>燕云十六声 layout — the original's <c>init_yysls_map()</c>, verbatim.</summary>
    private static SortedDictionary<int, KeyMapping> BuildYysMap() => new()
    {
        // 下行左手区
        [48] = new KeyMapping(0x5A, 0),           // C3  → Z
        [49] = new KeyMapping(0x5A, ModShift),    // C#3 → Shift+Z
        [50] = new KeyMapping(0x58, 0),           // D3  → X
        [51] = new KeyMapping(0x43, ModCtrl),     // D#3 → Ctrl+C
        [52] = new KeyMapping(0x43, 0),           // E3  → C
        [53] = new KeyMapping(0x56, 0),           // F3  → V
        [54] = new KeyMapping(0x56, ModShift),    // F#3 → Shift+V
        [55] = new KeyMapping(0x42, 0),           // G3  → B
        [56] = new KeyMapping(0x42, ModShift),    // G#3 → Shift+B
        [57] = new KeyMapping(0x4E, 0),           // A3  → N
        [58] = new KeyMapping(0x4D, ModCtrl),     // A#3 → Ctrl+M
        [59] = new KeyMapping(0x4D, 0),           // B3  → M

        // 中行主区
        [60] = new KeyMapping(0x41, 0),           // C4  → A
        [61] = new KeyMapping(0x41, ModShift),    // C#4 → Shift+A
        [62] = new KeyMapping(0x53, 0),           // D4  → S
        [63] = new KeyMapping(0x44, ModCtrl),     // D#4 → Ctrl+D
        [64] = new KeyMapping(0x44, 0),           // E4  → D
        [65] = new KeyMapping(0x46, 0),           // F4  → F
        [66] = new KeyMapping(0x46, ModShift),    // F#4 → Shift+F
        [67] = new KeyMapping(0x47, 0),           // G4  → G
        [68] = new KeyMapping(0x47, ModShift),    // G#4 → Shift+G
        [69] = new KeyMapping(0x48, 0),           // A4  → H
        [70] = new KeyMapping(0x4A, ModCtrl),     // A#4 → Ctrl+J
        [71] = new KeyMapping(0x4A, 0),           // B4  → J

        // 上行主区
        [72] = new KeyMapping(0x51, 0),           // C5  → Q
        [73] = new KeyMapping(0x51, ModShift),    // C#5 → Shift+Q
        [74] = new KeyMapping(0x57, 0),           // D5  → W
        [75] = new KeyMapping(0x45, ModCtrl),     // D#5 → Ctrl+E
        [76] = new KeyMapping(0x45, 0),           // E5  → E
        [77] = new KeyMapping(0x52, 0),           // F5  → R
        [78] = new KeyMapping(0x52, ModShift),    // F#5 → Shift+R
        [79] = new KeyMapping(0x54, 0),           // G5  → T
        [80] = new KeyMapping(0x54, ModShift),    // G#5 → Shift+T
        [81] = new KeyMapping(0x59, 0),           // A5  → Y
        [82] = new KeyMapping(0x55, ModCtrl),     // A#5 → Ctrl+U
        [83] = new KeyMapping(0x55, 0),           // B5  → U
    };

    private static string NormalizeLine(string s) => s
        .Replace('：', ':')
        .Replace('＝', '=')
        .Replace('－', '-')
        .Replace('＋', '+')
        .Replace('\u3000', ' ')
        .Replace('（', '(')
        .Replace('）', ')');

    private static void EnsureEncodingProvider()
    {
        if (_encodingProviderRegistered)
        {
            return;
        }

        _encodingProviderRegistered = true;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Without the provider only UTF-8/UTF-16 files load; that is a
            // degradation, not a failure.
        }
    }

    /// <summary>
    /// Reads a keymap file, honouring a UTF-8 BOM, strict UTF-8 validation, then
    /// falling back to the common Chinese/Japanese code pages before the system
    /// default — the same order the original used.
    /// </summary>
    internal static string ReadFileWithEncodingDetection(string path)
    {
        EnsureEncodingProvider();
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (IsValidUtf8(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        foreach (int codepage in new[] { 936, 950, 932, 1252, 28591 })
        {
            try
            {
                string candidate = Encoding.GetEncoding(codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    .GetString(bytes);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Try the next code page.
            }
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte c = bytes[i];
            if (c < 0x80)
            {
                i++;
            }
            else if (c < 0xC0)
            {
                return false;
            }
            else if (c < 0xE0)
            {
                if (i + 1 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80)
                {
                    return false;
                }

                i += 2;
            }
            else if (c < 0xF0)
            {
                if (i + 2 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80)
                {
                    return false;
                }

                if (c == 0xED && bytes[i + 1] > 0x9F)
                {
                    return false;
                }

                i += 3;
            }
            else if (c < 0xF8)
            {
                if (i + 3 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 ||
                    (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80)
                {
                    return false;
                }

                if (c == 0xF4 && bytes[i + 1] > 0x8F)
                {
                    return false;
                }

                i += 4;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
