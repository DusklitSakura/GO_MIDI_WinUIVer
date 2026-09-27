# GO_Midi → C#/WinUI 3 Reimplementation Specification

Research-only reverse specification of **https://github.com/qr-rp/GO_Midi** (branch `main`,
tree SHA `c621c1906331da504a6e1692a729d9f2d0efd0a8`), derived exclusively from the C++/wxWidgets
sources. Nothing here is invented; where the source is silent or self-contradictory it is marked
**[ABSENT]**, **[QUIRK]** or **[VERIFY]**.

## 0. Source inventory (all files read in full)

| File | Bytes (GitHub API) | Used for |
|---|---|---|
| `src/App.cpp` | 2 403 | app entry, log config bootstrap |
| `src/ui/MainFrame.cpp` | 102 883 | §1,2,8,9,11,12,13 |
| `src/ui/MainFrame.h` | 10 634 | control IDs, member state |
| `src/ui/UIHelpers.h` / `.cpp` | 2 365 / 2 098 | `UIConstants` strings, choice-window updater |
| `src/ui/Widgets.h` / `.cpp` | 4 773 / 19 666 | `ModernSlider`, `ScrollingText`, AB logic |
| `src/ui/PlaybackState.h` / `.cpp` | 2 741 / 3 601 | state machine, status/button text |
| `src/ui/KeymapEditorDialog.h` / `.cpp` | 3 494 / 19 637 | keymap editor dialog, scheme persistence |
| `src/ui/PianoRollCtrl.h` / `.cpp` | 3 719 / 18 562 | piano-roll binding UI |
| `src/core/PlaybackEngine.h` / `.cpp` | 5 469 / 43 655 | §4,5 |
| `src/core/KeyboardSimulator.h` / `.cpp` | 2 168 / 26 552 | §7 |
| `src/midi/MidiParser.h` / `.cpp` | 2 614 / 25 092 | §3 |
| `src/util/KeyManager.h` / `.cpp` | 2 316 / 31 253 | §6 |
| `src/util/PlaylistManager.h` / `.cpp` | 2 787 / 6 791 | §8 |
| `src/util/NtpClient.h` / `.cpp` | 1 630 / 17 094 | §10 |
| `src/resource.rc`, `README.md`, `AGENTS.md` | — | naming, packaging, intent |

There is **no** `keymaps/` directory in the repository even though `KeyManager.cpp` comments cite
`keymaps/燕云十六声默认键位.txt`; the Yys table exists **only** as the hard-coded `init_yysls_map()`.
**[ABSENT]** There are no tests, no menus, no accelerator table, no settings dialog.

---

## 1. Main window UI layout

### 1.1 Frame

| Property | Value |
|---|---|
| Class | `MainFrame : wxFrame` |
| Title | `"GO_Midi!"` — hard-coded, never changed (`SetTitle` never called) |
| Icon | resource `APP_ICON` (`src/assets/icon.ico`), only if resource loads |
| Client size | `FromDIP(500, 700)`; also `SetMinClientSize` to the same |
| Frame font | `wxSYS_DEFAULT_GUI_FONT`, `SetPointSize(9)` |
| Geometry restore | On start, if `WinW >= 400 && WinH >= 300` **and** the saved rect intersects any display geometry → `SetSize(rect)` |
| Root layout | one `wxPanel` (mainPanel) + vertical `wxBoxSizer`, 4 child panels + status bar |
| Drop target | whole mainPanel accepts `.mid`/`.midi` file drops |
| Executable name | `GO_MIDI!.exe` (CI artifacts `GO_MIDI!_<version>.exe`); requests admin via `Win32.manifest` (`requireAdministrator`) |

Panel proportions in the root vertical sizer, in order:

1. **Playlist panel** — proportion `1`
2. **Control panel** — proportion `0`
3. **Channel panel** — proportion `1`
4. **Bottom (keymap/latency/NTP/schedule) panel** — proportion `0`

### 1.2 Panel 1 — playlist panel (`InitPlaylistPanel`)

Vertical sizer, three rows. All `wxALL` borders are 2 px DIP unless stated.

**Row A — playlist/keymap selector row** (horizontal sizer)

| # | Control | Type | Label / text (verbatim) | Min size (DIP) | Action |
|---|---|---|---|---|---|
| 1 | `m_playlistChoice` | `wxChoice` | items = playlist names | 80 × – | switch playlist |
| 2 | `m_addPlaylistBtn` | `wxButton` | `新建` | 45 × – | create playlist |
| 3 | `m_deletePlaylistBtn` | `wxButton` | `删除` | 45 × – | delete playlist |
| 4 | `m_renamePlaylistBtn` | `wxButton` | `重命名` | 55 × – | rename playlist |
| 5 | separator | `wxStaticLine` vertical | – | 2 × 20, borders 4 px L/R | – |
| 6 | `m_keymapChoice` | `wxChoice` | `默认键位`, `燕云十六声`, then custom scheme names | 110 × – | switch keymap scheme |
| 7 | `m_keymapEditorBtn` | `wxButton` | `键位设置` | 70 × – | open keymap editor dialog |

**Row B — toolbar** (horizontal sizer)

| # | Control | Type | Label / text | Notes |
|---|---|---|---|---|
| 1 | `m_importBtn` | `wxButton` | `导入文件` | opens file dialog (§13) |
| 2 | `m_removeBtn` | `wxButton` | `移除选中` | removes selected playlist entry |
| 3 | `m_clearBtn` | `wxButton` | `清空列表` | clears current playlist + deletes `/Files` config group |
| 4 | `m_searchCtrl` | `wxTextCtrl` (`wxTE_PROCESS_ENTER`) | placeholder `搜索...` (`SetHint`) | proportion `1`, min height 26 DIP; live filter on every keystroke |

**Row C — list**

| Property | Value |
|---|---|
| `m_playlistCtrl` | `wxListView`, style `wxLC_REPORT \| wxLC_SINGLE_SEL \| wxLC_NO_HEADER`, proportion `1` |
| Column 0 | title `文件名`, `wxLIST_FORMAT_LEFT`, width always forced to the client width on `wxEVT_SIZE` |
| Horizontal scrollbar | force-hidden on MSW (`ShowScrollBar(SB_HORZ, FALSE)`), re-hidden after each resize |
| Font | 10 pt |
| Min size | – × 145 DIP |
| Row text | file **name only** (`path.AfterLast('\\')`); full path only in tooltip |
| Tooltip | on `wxEVT_MOTION`, hit-tested item's full path; only re-set when it changes |

### 1.3 Panel 2 — control panel (`InitControlPanel`)

**Row A — transport buttons** (horizontal, centred)

| Control | Type | Label | Notes |
|---|---|---|---|
| `m_prevBtn` | `wxButton` | `上一曲` | |
| `m_playBtn` | `wxButton` | `播放` (dynamic: `暂停` / `继续` / `播放`) | |
| `m_stopBtn` | `wxButton` | `停止` | |
| `m_nextBtn` | `wxButton` | `下一曲` | |
| `m_modeBtn` | `wxButton` | `单曲播放` initially; label = current mode string | cycles 5 modes |
| `m_decomposeBtn` | `wxButton` | `普通模式` ⟷ `和弦分解` | min size 80 × 25 DIP |

**Row B — progress**

| Order | Control | Type | Initial text | Notes |
|---|---|---|---|---|
| 1 | `m_currentTimeLabel` | `wxStaticText` | `00:00` | |
| 2 | `m_progressSlider` | **`ModernSlider` (custom-drawn)** | value 0, range 0…1000 | proportion 1; on file load range becomes `0 … (int)(length_s * 1000)`; value unit = **milliseconds** |
| 3 | `m_totalTimeLabel` | `wxStaticText` | `00:00` | |

**Row C — speed + current file**

| Order | Control | Type | Label | Notes |
|---|---|---|---|---|
| 1 | speed panel: `wxStaticText` | | `倍速:` | |
| 2 | `m_speedCtrl` | `wxSpinCtrlDouble` | `"1.0"`, `wxSP_ARROW_KEYS`, min `0.1`, max `100.0`, initial `1.0`, inc `0.1`, `SetDigits(2)` | min size 60 × 22 DIP |
| 3 | `m_currentFileLabel` | **`ScrollingText` (custom-drawn)** | `未选择文件` | proportion 1 |

Note: the target pitch range (音域) is **not** on the main window any more (comment: "目标音域已挪入键位设置弹窗, 主界面不再显示").

### 1.4 Panel 3 — channel configuration (`InitChannelPanel`, `CreateChannelConfig`)

`wxGridSizer(4, 2, 2, 2)` → **4 rows × 2 columns**, 8 identical cards (channel index 0…7),
filled row-major (row 0 = ch 1,2; row 1 = ch 3,4; row 2 = ch 5,6; row 3 = ch 7,8).

Each card is a `wxPanel` with a vertical sizer of two horizontal rows:

| Row | Control | Type | Content | Min size |
|---|---|---|---|---|
| 1 | enable toggle | `wxToggleButton` | `通道 %d` with `index + 1` → `通道 1` … `通道 8` | 50 × – |
| 1 | window choice | `wxChoice` | item 0 = `未选择` (`UIConstants::DEFAULT_WINDOW`, client data `nullptr`), then one item per enumerated window labelled `title(pid)` | proportion 1 |
| 2 | transpose | `wxSpinCtrl` | `"0"`, `wxSP_ARROW_KEYS \| wxTE_CENTRE`, range **−24 … +24**, initial 0 | 50 × – |
| 2 | track choice | `wxChoice` | item 0 = `全部音轨` (client data `-1`), then `"%d: %s"` per non-empty track | proportion 1 |

**There are no static labels** on row 2 (transpose/track are unlabelled, distinguishable only by position/type).

Semantics:
* Default enabled state: channel 0 = on, channels 1…7 = off (`bool initialEnable = (index == 0)`).
* Disabled channels remain fully configurable (deliberate: "未启用通道也允许配置… 先配好再启用 = 一次原子提交").
  `UpdateChannelUI()` is an intentionally empty function.
* Changing toggle → `engine.set_channel_enable(index, checked)` + debounced config save.
* Changing window → `engine.set_channel_window(index, hwnd_or_nullptr)` + debounced save.
  Index 0 (or client data null) ⇒ `nullptr` ⇒ keyboard goes through global `SendInput`.
* `EVT_LEFT_DOWN` on a window choice calls `UpdateWindowList()` first, then `Skip()` so the
  popup still opens — this is how users recover after the game restarts.
* Transpose `EVT_SPINCTRL` / `EVT_TEXT_ENTER` → `engine.set_channel_transpose`; if value > 0 the
  text is rewritten as `"+%d"`.
* Track choice → `engine.set_channel_track(index, clientData_as_int)` (`-1` when null).

### 1.5 Panel 4 — bottom bar (`InitKeymapPanel`)

Horizontal sizer with a stretch spacer on both ends (content centred), single `contentSizer`:

| Order | Control | Type | Content | Notes |
|---|---|---|---|---|
| 1 | label | `wxStaticText` | `网络延迟补偿:` | |
| 2 | `m_latencyCompCtrl` | `wxSpinCtrl` | `"0"`, `wxSP_ARROW_KEYS \| wxTE_CENTRE`, range `0 … INT_MAX`, initial 0 | 55 × – DIP; tooltip `请输入游戏内ping值，单人演奏可以忽略`; both `EVT_SPINCTRL` and `EVT_TEXT` store `value * 1000` into `m_latency_comp_us` |
| 3 | label | `wxStaticText` | `ms` | |
| 4 | separator | `wxStaticLine` vertical | – | 2 × 20, 4 px L/R |
| 5 | `m_ntpLabel` | `wxStaticText` | `--:--` | 45 × –, `wxALIGN_CENTER`; shows NTP **minute:second** |
| 6 | separator | `wxStaticLine` vertical | – | 2 × 20, 4 px L/R |
| 7 | `m_schedMin` | `wxSpinCtrl` | `"0"`, range 0…59 | 45 × – |
| 8 | label | `wxStaticText` | `:` | |
| 9 | `m_schedSec` | `wxSpinCtrl` | `"0"`, range 0…59 | 45 × – |
| 10 | `m_scheduleBtn` | `wxButton` | `定时` ⟷ `取消` | toggles scheduled playback |

### 1.6 Status bar

* `CreateStatusBar(2)`; font = system GUI font at 9 pt.
* Field widths: `{ -1, FromDIP(75) }` (field 0 stretch, field 1 fixed 75 DIP for BPM).
* Field 0 initially `UIConstants::AUTHOR_SIGNATURE`; field 1 initially `BPM: --`.
* Field 1 after a file loads: `"BPM: %.0f"` (or `"BPM: --"` when BPM ≤ 0) plus
  `" | %d/%d"` from the first time signature, or `" | 4/4"` when none.
* Field 0 is driven by `UpdateStatusText()` (transient messages, 3 s) and by the *help marquee*
  (see §2.4) and by the state-machine status text (see §1.7 and §2.2).
* `EVT_DPI_CHANGED` re-applies the 75 DIP width and calls `m_progressSlider->RefreshDPIMetrics()`, then `Layout()`.

### 1.7 Status / play-button text per playback state (`PlaybackState.cpp`)

| State | Status bar field 0 | Play button |
|---|---|---|
| `Idle`, `Playing`, `Stopped` | `By:最终幻想14水晶世界_黄金谷_吸溜` | `播放` (except `Playing` → `暂停`) |
| `Paused` | `已暂停` | `继续` |
| `Scheduled` | `定时: ` + context info (e.g. `定时: 目标: 03:15`) | `播放` |
| `Error` | `错误: ` + context info | `播放` |

**[VERIFY]** `AUTHOR_SIGNATURE` appears to contain a zero-width space (U+200B) between `By:` and
`最` — i.e. bytes `42 79 3A E2 80 8B E6 9C 80 …`. Confirm against raw bytes before copying.

### 1.8 Custom-drawn controls

Only three controls are owner-drawn; **everything else is a native wx/Win32 control**:

1. **`ModernSlider`** (`Widgets.cpp`) — the progress bar. Draws: rounded track, progress fill, AB
   range band, A/B triangle markers with `A`/`B` labels, thumb with shadow.
   Geometry: track height 4 DIP, thumb radius 6 DIP (8 DIP on hover/drag), padding =
   `thumbRadiusHover + 2` DIP, `SetMinSize(100 × 24)` DIP.
   Colours: track `rgb(200,200,200)`, progress `rgb(0,120,215)`, thumb `rgb(255,255,255)`,
   thumb border `rgb(180,180,180)`, A point `rgb(255,100,100)`, B point `rgb(100,255,100)`,
   AB band `rgba(255,200,100,80)`. Marker radius 5 DIP, 1 px white outline, bold 8 pt `A`/`B`.
2. **`ScrollingText`** (`Widgets.cpp`) — the current-file label. Marquee: speed `0.5` px/frame,
   fps `60` (timer `1000/60` ms), 1 000 ms delay before start, spacing = width of 4 spaces,
   min height 26 DIP, min height recomputed as `textHeight + 4`.
3. **`PianoRollCtrl`** (`PianoRollCtrl.cpp`) — the piano roll inside the keymap editor dialog only
   (never on the main window). White key 30 × 92 DIP, black key 20 × 56 DIP, internal horizontal
   scrollbar height 6 DIP, view width `min(content, 700)` DIP (floor 100), wheel step 40 px,
   thumb min width 20 px. Background `rgb(0x10,0x14,0x1a)`.

---

## 2. Default settings and constants

### 2.1 Every `UIConstants` string, verbatim

| Constant | Literal (Chinese as in source) |
|---|---|
| `DEFAULT_WINDOW` | `未选择` |
| `DEFAULT_TRACK` | `全部音轨` |
| `DEFAULT_KEYMAP` | `默认键位` |
| `KEYMAP_YYSLS` | `燕云十六声` |
| `MODE_SINGLE` | `单曲播放` |
| `MODE_SINGLE_LOOP` | `单曲循环` |
| `MODE_LIST` | `列表播放` |
| `MODE_LIST_LOOP` | `列表循环` |
| `MODE_RANDOM` | `随机播放` |
| `AUTHOR_SIGNATURE` | `By:最终幻想14水晶世界_黄金谷_吸溜` (**[VERIFY]** U+200B after `:`) |
| `STATUS_SYNCING` | `时间同步中...` |
| `STATUS_LOADED` | `已加载` |
| `STATUS_PAUSED` | `已暂停` |
| `BPM_PLACEHOLDER` | `BPM: --` |

All are `wxString::FromUTF8(...)` except `BPM_PLACEHOLDER` which is a plain ASCII literal.

### 2.2 Other UI strings actually used

| Context | Literal |
|---|---|
| Channel card | `通道 %d` (1-based) |
| Speed | `倍速:` |
| Current file placeholder | `未选择文件` |
| Latency | `网络延迟补偿:`, `ms`, tooltip `请输入游戏内ping值，单人演奏可以忽略` |
| NTP placeholder | `--:--` |
| Schedule button | `定时`, `取消` |
| Decompose button | `普通模式`, `和弦分解` |
| Play button | `播放`, `暂停`, `继续` |
| Transport | `上一曲`, `播放`, `停止`, `下一曲` |
| Import dialog | title `选择MIDI文件`; filter `MIDI文件 (*.mid;*.midi)|*.mid;*.midi|所有文件 (*.*)|*.*` |
| Error dialog | title `错误`; `加载失败: ` + `error_msg`; `播放列表中的文件均无法加载` |
| AB status | `A点: %02d:%02d`, `AB点循环: %02d:%02d - %02d:%02d`, `已清除AB点` |
| Schedule status | `目标: %02d:%02d`, `定时已启动 (目标: %02d:%02d)`, `定时已取消`, `定时任务触发`, ` - 时间已同步`, ` - 时间同步中...`, `时间已同步`, `时间同步中...` |
| Playlist dialogs | `请输入新播放列表的名称:`, `新建播放列表`, `新列表`, `已创建播放列表: `, `至少需要保留一个播放列表`, `提示`, `确定要删除播放列表 "%s" 吗？\n该操作不可撤销。`, `删除播放列表`, `已删除播放列表: `, `请输入新的播放列表名称:`, `重命名播放列表`, `已重命名为: `, `名称已存在或无效`, `重命名失败`, `已切换到: `, `当前列表`, `播放列表` |
| Keymap | `已加载键位方案: `, `键位方案加载失败: `, `已切换到默认键位`, `已切换到燕云十六声键位` |
| Track fallback | `空音轨` |
| BPM field | `BPM: %.0f`, `BPM: --`, ` | %d/%d`, ` | 4/4` |
| Time format | `%02d:%02d` (all time labels are `mm:ss`) |

### 2.3 Numeric defaults

| Setting | Default | Range / step |
|---|---|---|
| Target pitch range (音域) — built-in FF14 preset (`@builtin_0` / empty id) | **48 … 84** (C3 … C6) | 0…127 in the editor |
| Target pitch range — built-in Yys preset (`@builtin_1`) | **48 … 83** (C3 … B5) | 0…127 |
| Per-channel transpose | 0 | −24 … +24, step 1 |
| Playback speed (倍速) | 1.0 | 0.1 … 100.0, step 0.1, 2 decimals displayed |
| Play mode | `单曲播放` | 5 modes |
| Chord decompose | off (`false`) | – |
| Network latency compensation | 0 ms | 0 … INT_MAX ms |
| Schedule minute/second | 0 / 0 | 0…59 each |
| Channels enabled | only channel index 0 | – |
| Channel window | `未选择` (= `nullptr` → global SendInput) | – |
| Channel track | `全部音轨` (= `-1`) | – |
| Engine pitch range defaults (`PlaybackEngine.h`) | `m_min_pitch{48}`, `m_max_pitch{84}` | – |
| Engine speed default | 1.0 | – |
| AB points | A = −1.0 ms, B = −1.0 ms, loop disabled | – |
| Default playlist name | `默认列表` | – |
| New-playlist dialog default text | `新列表` | – |
| New-keymap-scheme default text | `新键位` | – |
| Keymap editor dialog size | 740 DIP wide; height ≥ 280 DIP | – |
| `MidiFile` header defaults | `division = 480`, `format = 1`, `length = 0.0f` | overridden by file |

### 2.4 Timer intervals (exact)

| Timer | ID | Interval | Mode | Purpose |
|---|---|---|---|---|
| `m_timer` | `ID_PLAYBACK_TIMER` = 2001 | **100 ms** | repeating, started in ctor | UI polling: playhead, AB loop enforcement, end-of-track, NTP label, window recovery, hook health |
| `m_statusTimer` | `ID_STATUS_TIMER` = 2002 | **3000 ms** | one-shot, armed by `UpdateStatusText` | after 3 s: if state is `Idle` start the help marquee, else restore the state status text |
| `m_helpScrollTimer` | `ID_HELP_SCROLL_TIMER` = 2005 | **50 ms** then **5000 ms** | one-shot chain | help text marquee in status field 0 |
| `m_configSaveTimer` | `ID_CONFIG_SAVE_TIMER` = 2006 | **300 ms** | one-shot, trailing-edge debounce | flush pending channel config write |
| Playlist font/scroll | – | `1000/60` ms | repeating while scrolling | `ScrollingText` |
| Keymap dialog pitch debounce | `ID_KM_PITCH_SAVE_TIMER` | **300 ms** | one-shot | flush pitch-range write |

Derived poll counters inside `OnTimer` (each tick = 100 ms):

* Window-recovery scan: every **50** ticks = **5 s**, only while `!engine.is_playing()`.
* Global-hook health check: every **300** ticks = **30 s**; reinstall if `g_hKeyboardHook == nullptr`.
* Progress slider refresh: only when `updateCounter % 3 == 0` ⇒ **≈50 ms**, and only if
  `|sliderShown − (int)(t*1000)| > 100` ms.
* Time labels: only when `|t − lastUpdateTime| >= 1.0` s.

### 2.5 Help marquee messages (exact list, in order)

`InitHelpMessages()`:

```
1. UIConstants::AUTHOR_SIGNATURE            -> By:最终幻想14水晶世界_黄金谷_吸溜
2. F12: 全局播放/暂停
3. 右键进度条: 设置 AB 循环点
4. Shift+右键 AB 点: 拖动调整位置
5. 第三次右键: 清除 AB 循环点
6. 点击模式按钮: 切换播放方式
7. 点击「和弦分解」: 切换普通/分解模式
```

Start index = last element. `StartHelpScroll()` arms the timer at 50 ms; each fire advances
`(index + 1) % 7`, writes it to status field 0, and re-arms at 5 000 ms. Marquee only runs while
the state machine is `Idle`; any `UpdateStatusText()` stops it and any non-Idle state stops it.

---

## 3. MIDI parsing algorithm (`MidiParser.cpp`)

### 3.1 Data model

```cpp
struct RawNote {
    float start_s;      // seconds
    int   pitch;        // 0..127
    float duration;     // seconds, clamped >= 0
    int   track_index;  // 0-based track ordinal
    int   channel;      // 1..16  (status low nibble + 1)
    int   velocity;     // 0..127
    int   program;      // 0..127 GM program in effect at Note On
};

class MidiTrack { std::string name; int note_count{0}; };

class MidiFile {
    std::vector<MidiTrack> tracks;
    float length{0.0f};
    int   division{480};
    int   format{1};
    std::vector<std::vector<RawNote>> raw_notes_by_track;
    // private:
    std::vector<std::pair<int,int>> m_tempo_events;              // (tick, tempo_us)
    std::vector<std::pair<int,std::pair<int,int>>> m_time_sig_events; // (tick, (nn,dd))
    std::vector<int>    m_tempo_ticks;
    std::vector<double> m_tempo_seconds;
    std::vector<int>    m_tempo_values;   // µs per quarter note
    double m_smpte_ticks_per_second{0.0};
    mutable size_t m_last_tempo_idx{0};
};
```

### 3.2 File/header parsing

1. Read whole file binary via `std::ifstream(std::filesystem::path(wstring), binary|ate)`.
   Failure strings: `无法打开文件 (宽字符路径)`, `无法读取文件 (宽字符路径)`.
2. `size < 14` → `无效的 MIDI 文件: 文件太小`.
3. Bytes 0…3 must be ASCII `MThd`, else `无效的 MIDI 文件: 缺少 MThd 头`.
4. `header_len = u32be(4)`; `< 6` → `无效的 MIDI 头长度`.
5. `format = u16be(8)`, `track_count = u16be(10)`, `division = u16be(12)`.
6. First chunk starts at `pos = 8 + header_len`.
7. SMPTE detection: if `division & 0x8000`:
   `fps = -int8_t((division >> 8) & 0xFF)`, `ticks_per_frame = division & 0xFF`,
   `fps_val = 29.97` when `fps == 29`, else `fps_val = fps`;
   `m_smpte_ticks_per_second = fps_val * ticks_per_frame`.
8. For each of `track_count` tracks: require `pos + 8 <= size`, ASCII `MTrk`
   (else `无效的音轨块头`), `chunk_len = u32be(pos+4)`, `chunk_end = pos+8+chunk_len`
   (must be `<= size`, else `音轨块长度无效`), parse, `pos = chunk_end`.
   If a track header is truncated: log `音轨数据不完整，已解析 i/N 个音轨` and **break** (not an error).

All 16-bit/32-bit reads are big-endian; an out-of-range offset sets
`m_valid = false`, `m_error_msg = 无效的 MIDI 数据偏移量`.

### 3.3 Variable-length quantity (`readVarLen`)

* Single byte `< 0x80` ⇒ that value.
* Otherwise accumulate up to **4** bytes of 7 bits; termination on a byte with MSB clear.
* Truncated read ⇒ `MIDI 数据意外结束`; more than 4 bytes ⇒ `变长数值过长`.
* No bounds guard on the value beyond the byte count.

### 3.4 Track parsing (`parse_track`) — per-event semantics

State: `abs_tick`, `running_status`, `channel_program[16]` (all initialised to 0 = Acoustic Grand),
and three flat arrays of size **2048** indexed by `key_idx = channel0 * 128 + pitch`:
`note_start_tick[2048]`, `note_start_depth[2048]` (int8), `note_velocity[2048]`,
`note_program[2048]`, plus overflow maps `std::unordered_map<int, std::vector<int>>` for the rare
nested same-key case (start tick, velocity, program stacks).

| Status | Handling |
|---|---|
| `< 0x80` | running status; break if `running_status == 0` |
| `0xFF` meta | `type = byte`, `len = varlen`; `0x2F` End-of-track → stop; `0x03` → `track.name = decodeText(pos,len)`; `0x51 && len==3` → tempo µs = 3 bytes big-endian, push `(abs_tick, tempo_us)`; `0x58 && len>=4` → `nn = byte0`, `dd = 1 << byte1`, push `(abs_tick,(nn,dd))`; anything else skipped |
| `0xF0`/`0xF7` | SysEx: `len = varlen`, skip, **clear running status** |
| `0x90` Note On | `pitch`, `vel`. If `vel == 0` → treated as Note Off. Otherwise: if depth > 0 push current start/vel/prog onto the overflow stacks; set start tick/vel/prog = `channel_program[channel0]`; `depth++`; `track.note_count++` |
| `0x80` Note Off | pops one level, emits `(start_tick, abs_tick, pitch, channel, start_vel, start_prog)`; if depth remains > 0, restores the top of the overflow stack |
| `0xA0`, `0xB0`, `0xE0` | skip 2 data bytes |
| `0xC0` Program Change | `channel_program[channel0] = byte` |
| `0xD0` Channel Pressure | skip 1 byte |
| anything else | skip 1 byte and continue |

After the chunk: `note_start_depth` is **not** reset, so any still-open notes are closed at
`abs_tick` (end of track) with velocity `velocity > 0 ? velocity : 64`; overflow entries are closed
too. Track name defaults to `"Track " + std::to_string(track_index)`.
`res.last_tick = abs_tick`.

### 3.5 Tempo map construction (`init_tempo_map`)

1. If SMPTE division: **overwrite** `m_smpte_ticks_per_second = (256 - ((division>>8)&0xFF)) * (division & 0xFF)`
   and set a single-entry map `ticks={0}, seconds={0.0}, values={500000}`; return.
   **[QUIRK]** this recomputes fps as `256 − high byte` and therefore **discards the 29.97
   drop-frame special case** set during header parsing, which is the value actually used by
   `tick_to_seconds`. A faithful reimplementation should reproduce this (i.e. 29 for `0xE3`).
2. Otherwise: sort the merged tempo events by tick (events are merged from **all** tracks).
3. If empty **or** the first tick ≠ 0, insert `(0, 500000)` (= 120 BPM).
4. De-duplicate by tick: for consecutive events with the same tick the **last one wins**.
5. Build the tables: `ticks[0]=e0.tick`, `values[0]=e0.tempo`, `seconds[0]=0.0`;
   for i ≥ 1:
   `dt_seconds = (ticks[i] − ticks[i−1]) * values[i−1] / division / 1e6`,
   `seconds[i] = seconds[i−1] + dt_seconds`.

### 3.6 tick → seconds

```
tick_to_seconds(tick):
  if SMPTE: return tick / m_smpte_ticks_per_second
  if map empty: return 0.0
  idx = m_last_tempo_idx                      // O(1) amortised sequential access
  if idx >= ticks.size() or ticks[idx] > tick: idx = 0
  while idx+1 < ticks.size() and ticks[idx+1] <= tick: idx++
  m_last_tempo_idx = idx
  return seconds[idx] + (tick − ticks[idx]) * values[idx] / division / 1e6
```
The cached index makes forward sequential conversion amortised O(1); backward conversion falls back
to scanning from 0.

### 3.7 Format 0 vs format 1

`format` is **stored and logged but never branched on**. All tracks are parsed by the same loop and:
* tempo events from **every** track are merged into one global tempo map,
* time-signature events from every track are merged and sorted,
* `track_count` comes from the header (format 0 files normally declare 1).

Therefore a format-0 file (single track containing all 16 channels) and a format-1 file behave
identically, except that format 0 yields `raw_notes_by_track.size() == 1`. Notes are attributed to
the declared track ordinal, not to a MIDI channel split — the **channel** is preserved per note, and
only the "all tracks" playback path uses it (to drop channel 10).

### 3.8 Length, notes, edge cases

* For each note: `start_s = tick_to_seconds(start_tick)`, `end_s = tick_to_seconds(end_tick)`,
  `dur = max(0, end_s − start_s)` (negative durations clamped to 0), `pitch/channel/velocity/program`
  copied; `max_end = max(max_end, end_s)`.
* `length = max_end` when `max_end > 0`; otherwise `length = tick_to_seconds(last_tick_global)` where
  `last_tick_global` is the maximum `abs_tick` over all tracks; if both are 0 then `length = 0`.
* After parsing, `m_data` is cleared and `shrink_to_fit()`-ed.
* `get_initial_bpm()` = `60 000 000 / tempo_us` using the tempo event **at tick 0** (last one at tick 0
  wins), default 500 000 ⇒ 120 BPM; returns 0.0 when `tempo_us <= 0`.
* `get_initial_time_signature()` = first event at tick 0, default `{4,4}`.

### 3.9 Note-name calculation

Note names are **not** produced by the parser. They come from `KeyManager::get_note_name`
(§6.5) and from MIDI meta 0x03 track names. Track display strings are built in `UpdateTrackList`:

* item 0: `全部音轨`, client data `-1`;
* then for each track with `note_count > 0`, in track order, label `"%d: %s"` with a 1-based
  display counter, client data = the real 0-based track index;
* the name is `wxString::FromUTF8(track.name)`, falling back to `wxString(track.name.c_str(), wxConvLocal)`,
  then to `"Track %d"`;
* if no track has notes, a single `空音轨` item with client data `-1` is appended.

---

## 4. Smart transpose (智能移调) algorithm

Triggered per channel when that channel's UI transpose is **exactly 0**
(`is_smart_transpose = (transpose == 0)`). Manual non-zero transpose disables smart shifting on that
channel and is **not** clamped (out-of-range pitches simply fail the key lookup and are dropped).

### 4.1 Histograms (built once per loaded file, in `load_midi`)

For every raw note with `0 <= pitch < 128`:

```
weight = sqrt(duration_seconds) * velocity
m_track_pitch_histograms[track_index][pitch] += weight        // per-track, 128 floats
m_global_histogram[pitch] += weight                            // only if NOT excluded
```

Global histogram exclusion rule (bass/percussion masking):

```cpp
if (raw.channel != 10 && raw.program != 43 && (raw.program < 33 || raw.program > 40))
```

i.e. drop MIDI channel 10 (percussion), GM program 43 (Contrabass) and GM programs 33…40 (Bass
family). `program` is the program in effect at that note's Note On (may be 0 if no Program Change).

### 4.2 Highest-note protection ("加固") — applied to every track histogram **and** the global one

Weights are multiplied in place.

```
highest_pitch = highest p with hist[p] > 0, else 60 (middle C)
lowest_pitch  = lowest  p < highest_pitch with hist[p] > 0, else highest_pitch
pitch_range   = highest_pitch - lowest_pitch

// weighted skewness over [lowest_pitch, highest_pitch], ignoring zero bins
sum_w  = Σ hist[p]            (only hist[p] > 0)
mean   = Σ p*hist[p] / sum_w
var    = Σ (p-mean)^2 * hist[p] / sum_w
skew3  = Σ (p-mean)^3 * hist[p] / sum_w
skewness = skew3 / sigma^3     where sigma = sqrt(var)
           (returns 0.0 if sum_w < 1e-10 or var < 1e-10)

// adaptivity: negative skew (sparse highs) -> stronger protection
skew_adjust = 1.0 + clamp(-skewness * 0.12, -0.3, +0.5)

boost_depth = max(4, (int)(pitch_range * 15 / 100 * skew_adjust))
base_boost  = 1.0 + (1.2 - 1.0) * skew_adjust
start_pitch = max(0, highest_pitch - boost_depth + 1)

for p in [start_pitch, highest_pitch]:
    if hist[p] > 0:
        distance = highest_pitch - p
        boost    = base_boost - 0.1 * distance
        if boost > 1.0: hist[p] *= boost
```

Named constants (declared `constexpr` inside `load_midi`):

| Constant | Value | Meaning |
|---|---|---|
| `kHighNoteBaseBoost` | `1.2f` | base boost at the highest note |
| `kHighNoteStepDecay` | `0.1f` | boost lost per semitone below the top |
| `kMinBoostDepth` | `4` | minimum number of semitones covered |
| `kBoostDepthPercent` | `15` | boost depth as % of the track's own pitch span |
| skew multiplier | `0.12f` | `skew_adjust` slope |
| skew clamp | `[-0.3f, +0.5f]` | adaptation range |

### 4.3 Octave-shift selection (`compute_best_shift`) — "Gaussian weighting"

For a histogram and the channel's target range `[m_min_pitch, m_max_pitch]`:

```
center     = (m_min_pitch + m_max_pitch) / 2.0f
half_range = max((m_max_pitch - m_min_pitch) / 2.0f, 1.0f)
sigma      = half_range * 0.4f

for oct in -4 … +4:                    // 9 candidates, shift = oct*12
    shift = oct * 12
    low   = max(0,   m_min_pitch - shift)
    high  = min(127, m_max_pitch - shift)
    if low > high: score[oct+4] = 0
    else:
        score = Σ over p in [low, high] with hist[p] > 0:
                    mapped = p + shift
                    dist   = |mapped - center|
                    score += hist[p] * exp(-0.5 * (dist/sigma)^2)

best = argmax(score); ties are broken toward the smaller |oct|;
default best_oct_idx = 4 (no transpose) so an all-zero histogram yields shift 0
return (best_oct_idx - 4) * 12      // ∈ {…, -48, -36, -24, -12, 0, +12, +24, +36, +48}
```

Rationale from source comments: the Gaussian keeps notes near the centre of the playable range and
avoids pushing them onto the extreme keyboard edges; shifts are always whole octaves so chord
quality is preserved.

### 4.4 When shifts are computed / applied

```
is_full_range = (m_min_pitch == 0 && m_max_pitch == 127)   // disables smart transpose entirely

if has_specific_track_config && !is_full_range:
    track_best_shifts[t] = compute_best_shift(track_hist[t])   for every track t
if has_global_config && !is_full_range:
    global_shift = compute_best_shift(global_hist)

per note, per active channel config vc:
    transpose = vc.settings->transpose                       // UI value (0 here)
    if vc.is_smart_transpose:
        if vc.is_specific_track: transpose += track_best_shifts[raw.track_index]
        else:                    transpose += global_shift
    raw_pitch = raw.pitch + transpose
    current_pitch = clamp_pitch(raw_pitch, m_min_pitch, m_max_pitch, vc.is_smart_transpose)
```

`clamp_pitch` (smart case) = octave folding then hard clamp:

```
while current < min: current += 12
while current > max: current -= 12
if current < min: current = min
if current > max: current = max
```

(Manual-transpose case returns the pitch unchanged — deliberately unclamped.)

### 4.5 Recompute triggers

The histograms are rebuilt only in `load_midi`. `rebuild_events` runs whenever
`m_config_version != m_built_version`, i.e. after any of: channel enable/disable, channel window or
track change **while that channel is enabled**, transpose change while enabled, pitch-range change,
decompose toggle, keymap change. Speed changes do **not** rebuild.

---

## 5. Playback engine

### 5.1 Threading model

| Thread | Responsibility |
|---|---|
| wx main/UI thread | all controls, `MainFrame::OnTimer` 100 ms polling, config writes, `PlaybackEngine::set_*` calls, `release_all_keys` triggers |
| engine thread (`m_thread`, 1 per engine, started in ctor) | timeline advance + event dispatch |
| NTP auto-sync thread | `NtpClient::AutoSyncThread` |
| scheduled-playback task | one `std::async` per schedule (`StartBackgroundTask`), tracked in `m_backgroundThreads` |
| MIDI parsing | runs synchronously on the UI thread inside `PlayIndex` |

Communication: `std::mutex m_mutex` + `std::condition_variable m_cv`; shared scalars are
`std::atomic`; config changes bump `std::atomic<int> m_config_version` and `m_cv.notify_all()`.
Rebuild happens **outside** the lock (`try_rebuild_events` snapshots notes + histograms, unlocks,
rebuilds, re-locks) and is validated against `std::atomic<int> m_all_notes_generation`.

Engine thread start-up:

```cpp
timeBeginPeriod(1);                                        // 1 ms timer resolution
SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
// affinity = last logical processor
cpuIdx = min(numProcessors - 1, sizeof(DWORD_PTR)*8 - 1);
SetThreadAffinityMask(GetCurrentThread(), (DWORD_PTR)1 << cpuIdx);
```
`timeEndPeriod(1)` on exit. Shutdown: set `m_running=false`, `m_cv.notify_all()`, `join()`.
`shutdown()` is idempotent (warns `PlaybackEngine::shutdown() 被多次调用，跳过`).

### 5.2 Timeline & event scheduling

`ProcessedEvent { double time; bool is_note_on; int vk_code; int modifier; void* window_handle; }`
with the ordering:

```
if |time - other.time| > 1e-6:  time < other.time
else:                           is_note_on < other.is_note_on   // Note Off BEFORE Note On
```

This guarantees Note Off at `t` is delivered before Note On at `t` (perfect legato).

Main loop per iteration (all inside `m_mutex` except sending):

1. If `m_config_version != m_built_version` → rebuild; then reposition `next_event_idx` with
   `std::lower_bound(m_events, m_current_time)`.
2. While `!m_running || (!m_playing || m_paused)`: `m_cv.wait(lock)`; on wake re-check rebuild and
   re-position `next_event_idx` by binary search; reset `last_loop_time`.
3. If `m_seek_triggered`: clear flag, lower_bound again, reset `last_loop_time` so the seek does not
   add an extra `dt`.
4. `dt = now − last_loop_time; last_loop_time = now;`
   **`m_current_time += dt_seconds * m_playback_speed`** (evaluated *before* dispatching).
5. **Batch window**: pop **every** event with `evt.time <= m_current_time` (catch-up after a stall)
   into `m_key_event_buffer` and update `m_active_keys` refcounts.
6. Unlock, then re-check under a short lock: if `!m_playing || m_paused || m_seek_triggered` the
   buffer is **discarded** (it was superseded by `release_all_keys()`), otherwise
   `m_simulator.send_key_events(buffer)` → one batched send per frame.
7. Compute the wall-clock wait to the next event:
   `wall_ms = min(15.0, (next.time − m_current_time) / speed * 1000)`, or **0** if the next event is
   already due. The `15 ms` cap exists so the UI poll keeps seeing a fresh time.
8. Hybrid sleep: `kSpinMarginMs = 1.5`.
   * if `wall_ms > 1.5`: interruptible `m_cv.wait_for(sleep_ms = wall_ms − 1.5)`; on wake, if
     `!m_playing || m_paused || m_seek_triggered` → `continue` (no spin).
   * then busy-wait `while (steady_clock::now() < deadline) YieldProcessor();` where
     `deadline = steady_clock::now() + wall_ms` (absolute), suppressing wake-up jitter to µs level.

There is **no look-ahead** beyond `m_current_time`; "look-ahead" in this design is only the batching
of already-due events plus the ≤15 ms sleep cap.

### 5.3 Active-key tracking & stuck-key avoidance

```cpp
using ActiveKeySet = std::unordered_map<std::pair<int /*vk*/, void* /*hwnd*/>, int /*refcount*/, ActiveKeyHash>;
```
* Note On → `try_emplace(key, 1)`; if already present `++refcount`.
* Note Off → `--refcount`; erase when it reaches 0.
* `release_all_keys()` snapshots the key set, clears it under the lock, then calls
  `KeyboardSimulator::release_keys(snapshot)` (grouped by window: PostMessage per windowed group,
  one batched `SendInput` for the `nullptr` group).
* `release_all_keys()` + `m_simulator.release_all_mods()` are called by **pause**, **stop** and **seek**.
* `stop()` additionally sends a hard safety burst straight to `SendInput` (guarded only by
  `GetForegroundWindow() != nullptr`): key-ups for `VK_SHIFT`, `VK_CONTROL`, `VK_MENU`, `VK_LWIN`
  in one 4-element `SendInput`.
* `play()` only sets `m_playing = true; m_paused = false;` and notifies — so play-after-pause resumes.
* `pause()` sets `m_paused = true` and **notifies specifically so that the A1 interruptible sleep
  returns immediately**; it leaves `m_playing` true.
* `stop()` sets `m_playing = false`, `m_paused = false`, `m_current_time = 0.0`.
* `seek(t)` clamps to `[0, m_total_duration]`, sets `m_seek_triggered = true`, notifies, releases keys/mods.

### 5.4 Channel settings and routing

```cpp
struct ChannelSettings {
    std::atomic<int>   transpose{0};
    std::atomic<bool>  enabled{true};     // ctor default; UI sets only ch0 enabled
    std::atomic<void*> window_handle{nullptr};
    std::atomic<int>   track_index{-1};   // -1 = all tracks
};
```
Exactly **8** channels are created in the ctor (aligned with the 8 UI cards).

`rebuild_events` builds `ValidConfig` entries for every **enabled** channel:
`is_specific_track = (track_index != -1)`, `is_smart_transpose = (transpose == 0)`.
If **no** channel is enabled a synthetic `default_global` config is used
(`enabled=true, track_index=-1, transpose=0, window=nullptr`) — the "match Python behaviour"
fallback. A channel with **no window is not skipped**: "没选窗口=全局发送".

Per note × per config:
* specific track → skip notes whose `track_index` differs;
* all-tracks → **skip `raw.channel == 10`** (percussion) and use the global smart shift;
* then transpose/smart-shift, `clamp_pitch`, and push a `TempNote{start,end,vk,modifier,hwnd,pitch,track}`.

### 5.5 Key mapping and conflict resolution (order matters)

1. **Key mapping first**: `m_key_manager.get_mapping(pitch)`; `vk_code == 0` ⇒ drop the note
   (`note.end = note.start` marks it invalid); a warning aggregates `键位映射丢弃统计: 丢弃数量=N`.
   This means manual transpose leaving the range silently drops notes rather than clamping them.
2. **Conflict resolution grouped by `(hwnd, vk)`** — one physical key can only sound one note at a
   time; this covers both same-pitch legato and different pitches mapped to the same physical key.
   `resolve(prev, curr)`:
   * identical start (|Δt| < 1e-5) **and** identical length (< 1e-5) ⇒ `curr` invalidated
     (`curr.end = curr.start − 1.0`);
   * if `curr.start < prev.start` ⇒ `curr.start = prev.start`;
   * containment: if `prev.end > curr.end` ⇒ `curr.end = prev.end`;
   * overlap: if `prev.end > curr.start` ⇒ `prev.end = curr.start` (truncate previous to legato).
   There is **no minimum gap** — perfect legato (Off at `t`, On at `t`) is intentional.
   A single `std::unordered_map<std::pair<void*,int>, TempNote*, PairHash>` tracks the active note
   per key while iterating the globally time-sorted vector.
3. **Decompose (和弦分解), only if `m_decompose`**, run last:
   * group notes by `hwnd`;
   * sort each group by start; `CHORD_THRESHOLD = 0.03 s`, `STAGGER = 0.05 s`;
   * a chord = consecutive notes starting within 30 ms of the group leader **with different vk**
     (`g[j].vk != g[i].vk`) — same-key notes are excluded so decomposition cannot open a gap where
     conflict resolution already produced legato;
   * sort the chord by ascending `pitch`, then for k = 1…n−1 shift `start` and `end` by `k * 0.05 s`;
   * re-sort; enforce monophonic ordering by truncating each note's end to the next note's start
     (no extra gap);
   * drop notes with `end <= start`.
4. **Event generation**: for each valid note push `{start, true, vk, mod, hwnd}` and
   `{end, false, vk, mod, hwnd}`; final `std::sort(m_events)`. `m_events.reserve(notes.size()*2)`.

Memory management: `m_all_notes.reserve(totalNotes)` (with `shrink_to_fit()` when
`capacity > totalNotes*4`), and the same 4× heuristic for `m_events`.

### 5.6 End-of-track, AB loop, speed, pause/stop/seek summary

| Action | Engine | UI (`OnTimer`, 100 ms) |
|---|---|---|
| natural end | `m_current_time` keeps growing; no engine-side stop | `t >= length && length > 0` → mode dispatch (§8.4) |
| AB loop | engine unaware | if `abLoopEnabled && A >= 0 && B > A && t*1000 >= B` → `engine.seek(A / 1000.0)` |
| speed | `m_current_time += dt * speed`; wait uses `/speed` | `OnSpeedChange` → `engine.set_speed(spin.GetValue())`; **no clamp in engine** |
| pause | `m_paused = true` + notify + release keys/mods | Play button → `暂停`/`继续` |
| stop | current time 0, keys released, safety modifier key-ups | slider → 0, current label → `00:00`, AB points preserved |
| seek | clamp, `m_seek_triggered = true`, release keys/mods | slider release → seek to `value/1000` s and auto-play if not playing |

---

## 6. Keyboard mapping (`KeyManager`)

### 6.1 Modifier bitmask (`KeyManager.h`)

| Name | Value | Meaning |
|---|---|---|
| `kModNone` | 0 | – |
| `kModShift` | 1 | Shift |
| `kModCtrl` | 2 | Ctrl |
| `kModAlt` | 4 | Alt |
| `kModMouseL` | 8 | mouse left |
| `kModMouseM` | 16 | mouse middle |
| `kModMouseR` | 32 | mouse right |

`KeyMapping { int vk_code; int modifier; }`. An `inline bool has_mouse_mod(int)` helper exists in the
header but is **not referenced** anywhere in the fetched sources (dead helper).

Lookup is O(1) through `KeyMapping m_lookup_cache[128]` + `bool m_lookup_valid[128]`, rebuilt by
`rebuild_lookup_cache()` on every map change, protected by `mutable std::mutex m_mutex`
(the engine thread reads while the UI thread writes).

### 6.2 Built-in default mapping — FF14 (`init_default_map`), complete

37 entries, `pitch = key (modifier 0)`. Keys shown with their Windows VK in hex/decimal.

| Pitch | Note | Key | VK hex | VK dec | mod |
|---|---|---|---|---|---|
| 48 | C3 | `I` | 0x49 | 73 | 0 |
| 49 | C#3 | `8` | 0x38 | 56 | 0 |
| 50 | D3 | `O` | 0x4F | 79 | 0 |
| 51 | D#3 | `9` | 0x39 | 57 | 0 |
| 52 | E3 | `P` | 0x50 | 80 | 0 |
| 53 | F3 | `[` | 0xDB (VK_OEM_4) | 219 | 0 |
| 54 | F#3 | `0` | 0x30 | 48 | 0 |
| 55 | G3 | `]` | 0xDD (VK_OEM_6) | 221 | 0 |
| 56 | G#3 | `-` | 0xBD (VK_OEM_MINUS) | 189 | 0 |
| 57 | A3 | `\` | 0xDC (VK_OEM_5) | 220 | 0 |
| 58 | A#3 | `=` | 0xBB (VK_OEM_PLUS) | 187 | 0 |
| 59 | B3 | `'` | 0xDE (VK_OEM_7) | 222 | 0 |
| 60 | C4 | `Q` | 0x51 | 81 | 0 |
| 61 | C#4 | `2` | 0x32 | 50 | 0 |
| 62 | D4 | `W` | 0x57 | 87 | 0 |
| 63 | D#4 | `3` | 0x33 | 51 | 0 |
| 64 | E4 | `E` | 0x45 | 69 | 0 |
| 65 | F4 | `R` | 0x52 | 82 | 0 |
| 66 | F#4 | `5` | 0x35 | 53 | 0 |
| 67 | G4 | `T` | 0x54 | 84 | 0 |
| 68 | G#4 | `6` | 0x36 | 54 | 0 |
| 69 | A4 | `Y` | 0x59 | 89 | 0 |
| 70 | A#4 | `7` | 0x37 | 55 | 0 |
| 71 | B4 | `U` | 0x55 | 85 | 0 |
| 72 | C5 | `Z` | 0x5A | 90 | 0 |
| 73 | C#5 | `S` | 0x53 | 83 | 0 |
| 74 | D5 | `X` | 0x58 | 88 | 0 |
| 75 | D#5 | `D` | 0x44 | 68 | 0 |
| 76 | E5 | `C` | 0x43 | 67 | 0 |
| 77 | F5 | `V` | 0x56 | 86 | 0 |
| 78 | F#5 | `G` | 0x47 | 71 | 0 |
| 79 | G5 | `B` | 0x42 | 66 | 0 |
| 80 | G#5 | `H` | 0x48 | 72 | 0 |
| 81 | A5 | `N` | 0x4E | 78 | 0 |
| 82 | A#5 | `J` | 0x4A | 74 | 0 |
| 83 | B5 | `M` | 0x4D | 77 | 0 |
| 84 | C6 | `/` | 0xBF (VK_OEM_2) | 191 | 0 |

The default map covers **every** semitone 48…84 inclusive; **no modifiers** are used
("Default mapping does not use modifiers"). Pitches outside 48…84 have no mapping by default.

### 6.3 Built-in preset — 燕云十六声 / Yys (`init_yysls_map`), complete

36 entries, pitches 48…83 (C3…B5). Modifier 1 = Shift (format suffix `+`), 2 = Ctrl (suffix `-`).
The source groups them as "下行左手区 (48-59)", "中行主区 (60-71)", "上行主区 (72-83)".

| Pitch | Note | Key | VK dec | mod | Renders as |
|---|---|---|---|---|---|
| 48 | C3 | Z | 90 | 0 | `z` |
| 49 | C#3 | Z | 90 | 1 | `z+` |
| 50 | D3 | X | 88 | 0 | `x` |
| 51 | D#3 | C | 67 | 2 | `c-` |
| 52 | E3 | C | 67 | 0 | `c` |
| 53 | F3 | V | 86 | 0 | `v` |
| 54 | F#3 | V | 86 | 1 | `v+` |
| 55 | G3 | B | 66 | 0 | `b` |
| 56 | G#3 | B | 66 | 1 | `b+` |
| 57 | A3 | N | 78 | 0 | `n` |
| 58 | A#3 | M | 77 | 2 | `m-` |
| 59 | B3 | M | 77 | 0 | `m` |
| 60 | C4 | A | 65 | 0 | `a` |
| 61 | C#4 | A | 65 | 1 | `a+` |
| 62 | D4 | S | 83 | 0 | `s` |
| 63 | D#4 | D | 68 | 2 | `d-` |
| 64 | E4 | D | 68 | 0 | `d` |
| 65 | F4 | F | 70 | 0 | `f` |
| 66 | F#4 | F | 70 | 1 | `f+` |
| 67 | G4 | G | 71 | 0 | `g` |
| 68 | G#4 | G | 71 | 1 | `g+` |
| 69 | A4 | H | 72 | 0 | `h` |
| 70 | A#4 | J | 74 | 2 | `j-` |
| 71 | B4 | J | 74 | 0 | `j` |
| 72 | C5 | Q | 81 | 0 | `q` |
| 73 | C#5 | Q | 81 | 1 | `q+` |
| 74 | D5 | W | 87 | 0 | `w` |
| 75 | D#5 | E | 69 | 2 | `e-` |
| 76 | E5 | E | 69 | 0 | `e` |
| 77 | F5 | R | 82 | 0 | `r` |
| 78 | F#5 | R | 82 | 1 | `r+` |
| 79 | G5 | T | 84 | 0 | `t` |
| 80 | G#5 | T | 84 | 1 | `t+` |
| 81 | A5 | Y | 89 | 0 | `y` |
| 82 | A#5 | U | 85 | 2 | `u-` |
| 83 | B5 | U | 85 | 0 | `u` |

Yys preset pairs a key with a modifier for each sharp (Shift for most, Ctrl for D#, A#):
`Z/X/C/V/B/N/M`, `A/S/D/F/G/H/J`, `Q/W/E/R/T/Y/U` with `+`/`-`.

### 6.4 Key-string parsing (`parse_key_string`)

The text→VK table (`get_vk_map`) contains **exactly these 48 names** (lower-case):

| | | | | | |
|---|---|---|---|---|---|
| `q` 0x51 | `w` 0x57 | `e` 0x45 | `r` 0x52 | `t` 0x54 | `y` 0x59 |
| `u` 0x55 | `i` 0x49 | `o` 0x4F | `p` 0x50 | `a` 0x41 | `s` 0x53 |
| `d` 0x44 | `f` 0x46 | `g` 0x47 | `h` 0x48 | `j` 0x4A | `k` 0x4B |
| `l` 0x4C | `z` 0x5A | `x` 0x58 | `c` 0x43 | `v` 0x56 | `b` 0x42 |
| `n` 0x4E | `m` 0x4D | `1` 0x31 | `2` 0x32 | `3` 0x33 | `4` 0x34 |
| `5` 0x35 | `6` 0x36 | `7` 0x37 | `8` 0x38 | `9` 0x39 | `0` 0x30 |
| `[` 0xDB | `]` 0xDD | `\` 0xDC | `'` 0xDE | `-` 0xBD | `=` 0xBB |
| `+` 0xBB | `/` 0xBF | `,` 0xBC | `.` 0xBE | `;` 0xBA | `` ` `` 0xC0 |

**[ABSENT]** No space, no function keys, no arrow/navigation keys, no numpad in the **text** parser.
(The piano-roll editor can bind far more keys — see §6.6 — but such bindings cannot be expressed in
the exported `.txt` because `format_key_string` returns "" for VKs outside the reverse map, and the
exporter skips empty strings.)

Algorithm:
1. lower-case, trim, normalize full-width punctuation (see below).
2. Strip modifier suffixes in a loop while `size > 1`:
   * first, if `size > 3`, check the 3-char tails `lmb`, `mmb`, `rmb` → set mouse bits;
   * then check the last char: `+` → Shift, `-` → Ctrl, `*` → Alt;
   * repeat until nothing pops (order-independent; `q+-` = Ctrl+Shift+q, `LMB*` = Alt+LMB).
   * the `size > 1` guards preserve a lone `-` or `+` as the main key.
3. trim, look up the remainder in `get_vk_map`; not found ⇒ parse failure (warning
   `无法解析按键: ... (行 N)`).

Formatting (`format_key_string`) is the inverse and appends suffixes in this fixed order:
`+` (Shift), `-` (Ctrl), `*` (Alt), `lmb`, `mmb`, `rmb`.
The reverse map is rebuilt from the forward map in `std::map<std::string,int>` key order, so for the
**only** duplicated VK (`0xBB` = `=` and `+`) the later key wins: **format always emits `=`**, never `+`.

### 6.5 Note-name parsing and printing

`get_pitch_from_name` — regex `^\s*([A-Ga-g])([#bB]?)(-?\d+)\s*$`, case-insensitive; accidentals
`#` = sharp, `b`/`B` = flat. Pitch-class table:
`c 0, c# 1, db 1, d 2, d# 3, eb 3, e 4, f 5, f# 6, gb 6, g 7, g# 8, ab 8, a 9, a# 10, bb 10, b 11`.
Result `pitch = (octave + 1) * 12 + pitch_class`; rejected when outside 0…127.
Therefore **C4 = 60** (MIDI convention), C3 = 48, C6 = 84.

`get_note_name` — `names[] = {C, C#, D, D#, E, F, F#, G, G#, A, A#, B}`,
`octave = pitch/12 - 1`, returns e.g. `"C4"` for 60, `""` outside 0…127.

### 6.6 The `.txt` keymap file format

**Loading** (`load_config`):
* File read as raw bytes, then encoding-detected and converted to UTF-8:
  1. UTF-8 BOM (`EF BB BF`) → strip.
  2. strict UTF-8 validation (full multi-byte sequence validation incl. surrogate range and
     U+10FFFF bound) → accept as UTF-8.
  3. otherwise byte-frequency heuristics pick a first candidate, then a priority list is tried:
     `GBK(936)`, `GB2312(936)`, `Big5(950)`, `Shift-JIS(932)`, `Windows-1252(1252)`,
     `ISO-8859-1(28591)`, system ACP. Conversion goes through `MultiByteToWideChar` →
     `WideCharToMultiByte(CP_UTF8)`; the result is rejected if fewer than 50 % of the produced bytes
     are printable (`>= 0x20` or TAB/LF/CR).
* Line handling: trim; skip empty; skip lines whose **first character is `#` or `-`** (comment);
  then full-width normalization (`：`→`:` `＝`=`=` `－`→`-` `＋`→`+` `　`→space `（`→`(` `）`→`)`);
  then this regex (`icase | optimize`):

  ```
  (?:音符\s+)?([A-G][#bB]?\d+|\d+)(?:\s*\(.*?\))?[\s]*[:=\-\s]+[\s]*([^\s]+)
  ```

  Group 1 = note (MIDI number or note name); group 2 = key string. Separators allowed: `:`, `=`,
  `-`, whitespace, in any repetition. Optional `音符 ` prefix and optional parenthesised comment are
  accepted. The parsed map **replaces** the whole current map (it does not merge); an empty result is
  a failure (`键位配置文件无有效映射`).

**Saving** (`save_config`) writes UTF-8 **with BOM**; each line has one leading space:

```
 ################################################################
 # MIDI 键位映射配置文件
 # 导出时间: YYYY-MM-DD HH:MM:SS
 ################################################################
 #
 # [编写规则说明]
 # 1. 每行定义一个音符映射，格式为: 音符(或音名) 分隔符 按键
 # 2. 音符表示法: 支持 MIDI 编号 (如 60) 或 音名 (如 C4, C#4, Eb4)
 # 3. 分隔符: 支持 冒号(:)、等号(=)、减号(-)、空格 或 全角符号(：、＝、－)
 # 4. 修饰符: 在按键后加 '+' 表示 Shift，加 '-' 表示 Ctrl，加 '*' 表示 Alt (可叠加, 如 q+- 表示 Ctrl+Shift+q)
 # 5. 鼠标键: 用 LMB / MMB / RMB 表示 左/中/右键, 同样支持修饰符 (如 LMB* 表示 Alt+左键)
 # 6. 自由度: 所有的符号都不分全角/半角，且不区分大小写
 #
 # [示例格式]
 #   60: z            (半角冒号)
 #   C4 = x           (音名 + 等号)
 #   音符 62 (D4)：c  (带备注 + 全角冒号)
 #   64　v            (全角空格)
 #
 ################################################################

 音符 60 (C4): q
 音符 61 (C#4): 2
 ...
```
Entries are sorted by ascending pitch; keys are rendered by `format_key_string`; entries whose key
cannot be rendered are silently skipped. Timestamp format `%Y-%m-%d %H:%M:%S` local time.

### 6.7 Scheme presets in the app (not files)

* `m_keymapChoice` / editor dropdown index 0 = `默认键位` (id `""` or `@builtin_0`, FF14 table,
  range 48–84), index 1 = `燕云十六声` (id `@builtin_1`, Yys table, range 48–83), indices ≥ 2 =
  custom schemes stored **inside `config.ini`**, not as files.
* Custom schemes are created empty (`新键位` default name), renamed via a text prompt, deleted with
  a `DeleteGroup("/KeymapSchemes/List_<i>")`, imported from a `.txt` (`load_config` then stored
  under a scheme named after the file base name), or exported to a `.txt` (does not change the
  current scheme). Uniqueness via `GenerateUniqueName`: `base`, `base (1)`, `base (2)`, …

---

## 7. Keyboard simulation (`KeyboardSimulator`)

### 7.1 Two paths

| Condition | Path | Where |
|---|---|---|
| `window_handle == nullptr` | **`SendInput`** (global, hardware-level, goes to the foreground window) | required for games that read raw input / DirectInput |
| `window_handle != nullptr` | **`PostMessage`** to that `HWND` (per-message, no `SendInput`) | background/window-targeted delivery |

The routing decision is made **per event**, inside `send_key_events`, from `evt.window_handle`.
`send_key_down` / `send_key_up` (single-key API) use the same rule and are still present, but the
engine only uses the batch API.

### 7.2 `SendInput` details

* Keyboard `INPUT`: `type = INPUT_KEYBOARD`, `ki.wVk = vk`, `dwFlags = 0` (down) or
  `KEYEVENTF_KEYUP` (up). **`ki.wScan` is left 0** — deliberately, to avoid
  `KEYEVENTF_SCANCODE`+`KEYEVENTF_EXTENDEDKEY` conflicts and to stay thread-safe without a scan-code cache.
* Mouse `INPUT`: `type = INPUT_MOUSE`, `dx = dy = 0`, `mouseData = 0`,
  `dwFlags ∈ {MOUSEEVENTF_LEFTDOWN/UP, MIDDLEDOWN/UP, RIGHTDOWN/UP}`, `time = 0`, `dwExtraInfo = 0`
  → click at the **current cursor position**.
* Single-key path uses a stack buffer `INPUT inputs[16]` (6 modifier downs + main key + 6 modifier
  ups = 13, rounded to 16).
* Batch path uses `INPUT inputs[256]`, flushed automatically when full (and at the end).

### 7.3 Modifier handling — instantaneous wrapping

For every Note On the simulator emits **modifier down → main key down → modifier up** immediately
(never held across notes):

```
Note On : emit_mods(down=true); emit_key(vk, down=true); emit_mods(down=false)
Note Off: emit_key(vk, down=false); emit_mods(down=false)     // safety re-release, harmless
```
Mouse modifier bits are emitted as mouse down/up around the main key. Held-modifier bookkeeping is
`std::unordered_map<void* /*target*/, ModState{ int held; }>`; `held` is used only to fill the
`wParam` `MK_SHIFT`/`MK_CONTROL` bits of `PostMessage`d mouse messages. `release_all_mods()`
walks the map, emits the matching ups (PostMessage for windowed targets, one batched `SendInput`
for the `nullptr` target), then clears the map. It is called on pause/stop/seek and on shutdown.

### 7.4 `PostMessage` details

* Keyboard: `WM_KEYDOWN` / `WM_KEYUP` with
  `lParam = 1 | (scanCode << 16) | (extended ? 1<<24 : 0) | (up ? (1<<30)|(1<<31) : 0)`,
  where `scanCode = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)` and
  `extended = (vk >= VK_PRIOR && vk <= VK_DOWN) || vk == VK_INSERT || vk == VK_DELETE`.
  `wParam = vk`.
* Mouse: `WM_LBUTTONDOWN/UP`, `WM_MBUTTONDOWN/UP`, `WM_RBUTTONDOWN/UP` posted at the **centre of the
  client rect** (`MAKELPARAM((right-left)/2, (bottom-top)/2)`), `wParam` carrying `MK_SHIFT` /
  `MK_CONTROL` (never `MK_MENU`; the comment notes "无 Alt 标志位").

### 7.5 Bulk release (`release_keys`)

Input: `vector<pair<int vk, void* hwnd>>`. Groups by `hwnd`:
* windowed group → per key `PostMessage(WM_KEYUP)` with the same lParam recipe;
* `nullptr` group → accumulate into `INPUT inputs[256]` (flush when full) and send one `SendInput`.

### 7.6 Target-window enumeration and filtering (`GetWindowList`)

`EnumWindows(EnumWindowsProc)` keeps a window when **all** hold:

1. `IsWindowVisible(hwnd)` is true;
2. `GetWindowTextW(hwnd, buf, 256)` returns `len > 0` (title buffer 256 `wchar_t`);
3. the UTF-8-converted title is non-empty and contains at least one non-`isspace` character.

No process, class or ownership filtering — the app's own window, tool windows and hidden-owner
popups all appear if visible and titled. Process name: `GetWindowThreadProcessId` then
`OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)` + `GetModuleBaseNameW` (buffer
`MAX_PATH`); on any failure the name is the literal `"Unknown"`. `WindowInfo = {HWND hwnd; string
title; string process_name; unsigned long pid;}`.

Dropdown contents (`ChannelUIUpdater::UpdateWindowLists`): index 0 = `未选择` (client data
`nullptr`), then one item per window labelled `"%s(%lu)"` = `title(pid)`, client data = `HWND`.
`UpdateChoiceItems` rebuilds the list, preserves the previous selection **by display string** when
still present, otherwise selects index 0, all inside `Freeze()/Thaw()`.
The list is re-sorted by `_stricmp(title)` in `MainFrame::UpdateWindowList` before it is pushed to
the 8 dropdowns.
Refresh triggers: startup, clicking a window dropdown (`EVT_LEFT_DOWN`), every file-config load, and
the 5 s recovery scan while not playing.

### 7.7 Window recovery (`TryRecoverWindows`, every 5 s while stopped)

For each channel whose dropdown selection is still index 0 (未选择) and when a file is loaded:
read `/Files/<name>/Channel_N/{WindowTitle,ProcessName,WindowPID}`; match with
`FindWindowByTitleAndProcess(title, process, pid)`:

* level 1: title **and** process name **and** pid equal;
* level 2: title **and** process name;
* requires non-empty title and non-empty process name, else `-1`.

On a hit the dropdown selection is set to `index + 1` and the engine window is set. This is how the
app re-attaches after the game crashes/restarts.

---

## 8. Playlist manager

### 8.1 Data model

```cpp
struct Playlist {
    wxString name;
    std::vector<wxString> files;         // absolute paths
    bool   IsEmpty() const;
    size_t GetCount() const;
    bool   AddFile(const wxString& path);  // NO duplicates: returns false if path already present
    bool   RemoveFile(size_t index);
    void   Clear();
};

class PlaylistManager {
    std::vector<Playlist> m_playlists;
    int m_currentIndex;                    // ctor: 0, EnsureDefaultPlaylist()
    static wxString GetDefaultName() { return "默认列表"; }
};
```

* `EnsureDefaultPlaylist()` creates `默认列表` whenever the vector is empty (ctor, after a failed load).
* `GenerateUniqueName(base)`: `base`, then `"%s (%d)"` with counter starting at 1
  (`base (2)`, `base (3)`, …; the first increment makes `(2)`).
* `CreatePlaylist(name)`: empty name → `新列表`; always uniquely named; appends and returns the new index.
* `DeletePlaylist(index)`: false for an out-of-range index **or** when only one playlist remains;
  otherwise erases and fixes `m_currentIndex` (clamp to last, or decrement if the removed index was below it).
* `RenamePlaylist(index, name)`: false for empty name or a duplicate of another playlist; otherwise renames.
* File ops always act on the **current** playlist.

MainFrame keeps a **mirror copy** `std::vector<wxString> m_playlist_files` that is refreshed by
`RefreshPlaylistUI()`; the `wxListView` row's `wxItemData` stores the **mirror index** (this is the
model→view mapping used everywhere).

### 8.2 Persistence

All playlist data lives in `config.ini` (§11), group `/Playlists`:

| Key | Meaning |
|---|---|
| `Count` | number of playlists |
| `CurrentIndex` | index of the active playlist |
| `List_<i>/Name` | playlist name (load default `列表 %ld` with `i+1`; save always writes the real name) |
| `List_<i>/FileCount` | number of files |
| `List_<i>/File_<j>` | absolute file path |

`SaveConfig` does `DeleteGroup("/Playlists")` then rewrites everything and `Flush()`es.
`LoadConfig` reads `Count`; `<= 0` ⇒ single default playlist; `CurrentIndex` is range-checked.
**[ABSENT]** There is no per-playlist file on disk and no playlist import/export.

### 8.3 Import and list maintenance

* `导入文件` → `wxFileDialog` (title `选择MIDI文件`, multi-select, filter
  `MIDI文件 (*.mid;*.midi)|*.mid;*.midi|所有文件 (*.*)|*.*`) → `ImportFiles(paths)`.
* `ImportFiles`: for each path, `PlaylistManager::AddFile` (dedupe by exact path); accepted paths are
  appended to `m_playlist_files` and, if they match the current search filter, inserted into the view.
  One `SavePlaylistConfig()` if anything was added. The list control is `Freeze()`/`Thaw()`-ed.
* Drag & drop on the main panel: only `.mid` / `.midi` (case-insensitive) reach `ImportFiles`.
* `移除选中`: removes from the manager **and** the mirror, deletes the view row, decrements the
  `wxItemData` of every row after it. If the removed file was the currently loaded one, playback
  stops, the labels reset (`未选择文件`, `00:00`, `00:00`, slider 0, BPM field `BPM: --`) and the
  next surviving row is selected/loaded without auto-play.
* `清空列表`: stops playback, clears the mirror + manager, resets labels, saves the playlist config,
  and **deletes the whole `/Files` config group**.
* Search (`OnSearch`, bound to `EVT_TEXT`): case-insensitive substring match on the file **name**
  (`path.AfterLast('\\').Lower().Contains(keyword)`), rebuilds the view, marks `m_current_path`'s row
  selected and updates `m_current_play_index` to the view index.

### 8.4 Drag reorder

`OnPlaylistBeginDrag`: record `m_drag_source_view = event.GetIndex()`, set
`m_is_dragging_playlist = true`, `CaptureMouse()`.
`OnPlaylistEndDrag` (on `EVT_LEFT_UP`): release capture; `HitTest` the drop point for the target row
(falling back to the last row); if source == target, abort. Then in the **mirror**: erase the source
path, decrement the destination index if `src < dst`, insert at the destination. The view is rebuilt
via `OnSearch`, the moved file is re-selected/`EnsureVisible`d, then `SavePlaylistConfig()`.

**[QUIRK]** the reorder is applied to `m_playlist_files` (MainFrame's mirror) but **not** to the
`PlaylistManager`'s `Playlist::files`, and `SavePlaylistConfig()` persists the *manager's* order.
Consequently the new order is visible and used for playback in the current session but is lost on
any `RefreshPlaylistUI()` (playlist switch, restart). A reimplementation should reorder the manager
(or persist the mirror) instead of copying this bug.

### 8.5 Play modes — exact semantics

Mode strings and cycle order of the 模式 button (`OnModeClick`):

`单曲播放 → 单曲循环 → 列表播放 → 列表循环 → 随机播放 → 单曲播放` (label is set to the mode string;
change is saved globally).

| Mode | `下一曲` (`OnNext`) | `上一曲` (`OnPrev`) | End-of-track (`OnTimer`) |
|---|---|---|---|
| `单曲播放` | `idx+1`; if past the end: stop if playing, else nothing. Otherwise `PlayIndex(idx+1)` | `idx−1` clamped to 0, `PlayIndex` | `OnStop` |
| `单曲循环` | same as 单曲播放 | same | `seek(0)` + `play` (no reload) |
| `列表播放` | `idx+1`; if past the end → `OnStop`; else `SkipToNextValid(idx+1, +1, count−(idx+1))` | `idx−1` clamped to 0, `PlayIndex` | if current file no longer in the playlist → stop, else `OnNext` (which stops at the end) |
| `列表循环` | `(idx+1) % count` → `SkipToNextValid(…, +1, count)` | `(idx−1+count) % count` → `SkipToNextValid(…, −1, count)` | same as above; `OnNext` wraps |
| `随机播放` | `GetNextRandomIndex()` → `SkipToNextValid(idx, +1, count)` | **resets the shuffle sequence first**, then `GetNextRandomIndex()` → `SkipToNextValid(…, +1, count)` | same as above; `OnNext` draws the next shuffled index |

Random specifics: `InitializeRandomShuffle()` builds `m_shuffle_indices = [0 … count−1]` and applies
`std::shuffle` with `std::mt19937 m_random_engine` seeded from `std::random_device`; the cursor
advances; when exhausted `m_need_shuffle_reset` is set so the next call reshuffles (so the whole list
is played exactly once per cycle, no repeats until reshuffle). `ResetRandomSequence()` (called when
entering random mode and by `上一曲`) clears the sequence and forces a reshuffle.

`SkipToNextValid(startIndex, direction, maxRetries)` walks `index = start + direction*i` with
wrap-around (`<0 → +=count`, `>=count → -=count`), calls `PlayIndex(index, autoPlay=true,
showDialog=false)`, and on total failure shows a message box `播放列表中的文件均无法加载` with title
`错误` and moves the state machine to `Error`.

`PlayIndex(viewIndex, autoPlay = true, showDialog = true)`:
1. resolve `modelIndex = itemData(viewIndex)`; bail on any out-of-range value;
2. `m_current_play_index = viewIndex`; select + `EnsureVisible` with `m_is_programmatic_selection`
   guard;
3. if the path differs from the loaded one: `engine.stop()`, **clear AB points**
   (`m_abLoopEnabled=false`, A=B=−1, `slider.ClearABPoints()`), construct `MidiFile(path)`
   (parse on the UI thread), on failure set state `Error` + optional message box `加载失败: …` and
   return false, else `engine.load_midi()`, free `raw_notes_by_track`, set the slider range to
   `0 … (int)(length*1000)`, set the file label and total time, `UpdateTrackList()`,
   `LoadFileConfig(filename)`, and update the BPM/time-signature status field;
4. re-apply `engine.set_speed(spin value)` and `engine.set_pitch_range(m_minPitch, m_maxPitch)`;
5. if `autoPlay`: `engine.seek(0); engine.play();` state → `Playing` (song changes always restart
   from the beginning rather than toggling).

---

## 9. AB loop (progress-bar right-click)

Slider value space = **milliseconds**, range `0 … (int)(length_seconds * 1000)`; `ValueFromPos(x)`
maps x linearly with `padding = hoverThumbRadius + 2 DIP` and clamps to `[min,max]`. The AB state
lives in two places: `ModernSlider::m_aPoint/m_bPoint/m_abState` (drawing + hit-testing) and
`MainFrame::m_abPointA_ms/m_abPointB_ms/m_abLoopEnabled` (engine control).

### 9.1 Right-click state machine (in `ModernSlider`)

`m_abState`: `0` = nothing set, `1` = A set waiting for B, `2` = A and B set.

| Event | Condition | Action |
|---|---|---|
| **1st right-click up** | `m_abState == 0` | `m_aPoint = ValueFromPos(x)`, `m_abState = 1`, fire `EVT_AB_POINT_SET_A` with `int = A`. MainFrame stores A, status `A点: %02d:%02d` |
| **2nd right-click up** | `m_abState == 1` | `m_bPoint = ValueFromPos(x)`, `m_abState = 2`, fire `EVT_AB_POINT_SET_B`. MainFrame sets B, enables the loop, orders A≤B, pushes `slider.SetABPoints(A,B)`, seeks to A and starts playing if stopped, sets the slider value and the current-time label, status `AB点循环: %02d:%02d - %02d:%02d` |
| **3rd right-click up** | `m_abState == 2` **and** x is **not** within `FromDIP(10)` px of either marker | clears A and B to `-1`, `m_abState = 0`, fire `EVT_AB_POINT_CLEAR`. MainFrame: A=B=−1, `m_abLoopEnabled = false`, status `已清除AB点` |
| Right-click **down** | `m_abState == 2` and x within 10 DIP of A or B | `CaptureMouse()`, `m_isDraggingA/B = true`, cursor `wxCURSOR_SIZEWE` |
| Motion during drag | right button down | `m_aPoint/m_bPoint = ValueFromPos(x)`, fire `EVT_AB_POINT_DRAG` with `GetInt() = newPosition` and `GetClientData() = (void*)1` for A, `(void*)2` for B |
| Right-click **up** while dragging | dragging A/B | release capture, clear drag flags, restore the standard cursor — **no state change**, so the loop survives a drag |
| Hover near A/B (no drag) | – | cursor becomes `wxCURSOR_SIZEWE`; elsewhere the standard cursor |

`OnABPointDrag` writes the new A or B, then re-normalises **MainFrame's** pair so A ≤ B
(`min`/`max` swap). Note the slider's own `m_aPoint/m_bPoint` are **not** re-normalised, so dragging
A past B leaves the drawn markers in the opposite order to the MainFrame values (`OnABPointSetB`
does re-sync the slider, a plain drag does not). **[QUIRK]**

### 9.2 Loop enforcement and interaction with other features

* Enforced only by the 100 ms UI timer and only while `engine.is_playing()`:
  `if (m_abLoopEnabled && m_abPointA_ms >= 0 && m_abPointB_ms > m_abPointA_ms && t*1000 >= m_abPointB_ms) engine.seek(m_abPointA_ms / 1000.0);`
  ⇒ effective loop granularity is the poll interval (up to ~100 ms overshoot past B).
* `停止` (`OnStop`) deliberately **keeps** the AB points ("不清除 AB 点，保留设置供下次播放使用").
* Loading a different file clears the AB points (`PlayIndex` → `ClearABPoints`).
* The A/B markers are drawn as triangles above the track with the letters `A` / `B`; the region
  between them is painted with the translucent orange band `rgba(255,200,100,80)`.
* Left-drag seek and the AB loop are independent: seeking outside A…B is immediately corrected on
  the next timer tick if B has been passed.

---

## 10. Scheduled playback (定时播放) and NTP

### 10.1 NTP client (`NtpClient`)

* Servers, in this exact order (UDP port **123**, 48-byte request with `packet[0] = 0x1B`):
  1. `ntp.aliyun.com`
  2. `ntp.tencent.com`
  3. `cn.pool.ntp.org`
  4. `pool.ntp.org`
* Per-sample: `socket(AF_INET, SOCK_DGRAM)`, `SO_RCVTIMEO` = timeout, `getaddrinfo(server, "123")`,
  `t0` before `sendto`, `t3` after `recvfrom`; server receive timestamp from bytes 32…35/36…39 and
  transmit timestamp from 40…43/44…47; NTP→Unix epoch offset `2208988800`.
  `offset = ((t1 − t0) + (t2 − t3)) / 2`, `delay = (t3 − t0) − (t2 − t1)`.
  A sample is valid when `isfinite(offset) && isfinite(delay) && delay >= 0`.
* Sampling policy: **fast mode** while `!s_synced` — 2 samples per server, stop early once
  `all_samples.size() >= 3`, per-sample timeout **200 ms**; **normal mode** — 8 samples per server,
  no early stop, timeout **1 000 ms**. `s_auto_sync_stop` aborts the loops.
* Filtering: `delay_threshold = max(min_delay * 1.5, min_delay + 10.0)`; keep samples with
  `delay <= threshold` (if none — impossible in practice — fall back to all samples).
* Weighted mean with `weight = 1 / delay²` for both offset and delay.
* Base update: `now_est = local_now + final_offset`; `error = now_est − GetNow()`.
  * if `!s_synced` or `|error| > 5000 ms` → hard set `s_base_ntp = now_est`;
  * else EWMA `alpha = 0.2`, step clamped to **±5 ms** per sync, `s_base_ntp = current_now + step`.
* Skew (drift) estimation with an anchor pair: anchors are (re)initialised while `!s_synced`,
  `abs_err > 5000 ms`, or `sync_count < 5` (in which case `s_skew = 1.0`); otherwise, only when the
  anchor interval exceeds **60 s**, `measured_skew = real_delta / steady_delta` is accepted if
  `|measured_skew − 1| < 0.001` and smoothed with `skew_alpha = 0.3`.
* `GetNow()` = `system_clock::now()` when not synced, else
  `s_base_ntp + (steady_now − s_base_steady) * s_skew` (µs resolution).
* Auto-sync thread: `Sync()` then wait `interval = (count <= 3 || !s_synced) ? 1 s : 10 s`
  (interruptible). `StartAutoSync()` is called from the MainFrame constructor;
  `ForceShutdown()` (called on window close) sets the stop flag, joins and clears `s_synced`.
* If every sample fails: `NTP 同步失败: 无有效样本` and `GetNow()` falls back to local time.

### 10.2 Choosing the target time in the UI

* The user types minutes (`m_schedMin`, 0…59) and seconds (`m_schedSec`, 0…59) — **there is no hour
  field**; the target is "the next time the wall clock matches mm:ss".
* Pressing `定时`:
  1. cancels if already scheduled (`m_is_scheduled`), or
  2. sets `m_is_scheduled = true`, relabels the button to `取消`, disables both spin controls,
     sets state context to `目标: %02d:%02d` and transitions to `Scheduled`, status text
     `定时已启动 (目标: %02d:%02d)`; stamps `m_active_schedule_token` with a monotonically increasing
     token and spawns a background task.
* Background task:
  1. read `NtpClient::IsSynced()` and queue `ID_NTP_TIMER` with status
     `…- 时间已同步` / `…- 时间同步中...`;
  2. `now = NtpClient::GetNow()`, break down to local `tm`, then
     `target.tm_min = mins; target.tm_sec = secs; target.tm_isdst = -1; mktime(...)`;
     if `target <= now` → **`target += 1 hour`** (literally one hour, not one day) **[QUIRK]**;
  3. store the *uncompensated* target epoch (µs) in `m_schedule_target_epoch_us`;
  4. sleep loop against `effective_target = target − m_latency_comp_us`:
     * coarse: if remaining > 2 000 µs, `sleep_for(clamp(remaining − 500 µs, [200 µs, 50 ms]))`;
     * fine: if fine_remaining > 200 µs `sleep_for(100 µs)` else `std::this_thread::yield()`;
     * the loop re-reads `m_latency_comp_us` every iteration, so changing the latency spinner during
       the countdown changes the trigger instant;
  5. on exit, queue `ID_SCHEDULE_TRIGGER` with the token, unless shutting down or the token is stale.
* `OnScheduleTrigger`: verifies `m_is_scheduled && active token == token`; sets status
  `定时任务触发`; clears the scheduled flag/token/target; restores the button label `定时` and
  re-enables the spins; then calls `OnPlay(dummy)`.
  **[QUIRK]** `OnPlay` is a play/pause **toggle** and does not seek to 0 — if playback is already
  running at the trigger instant it will pause instead of (re)starting.
* Countdown display: **[ABSENT]**. There is no countdown widget. The visible feedback is the status
  bar text `定时: 目标: MM:SS` (state machine) / `定时已启动 (目标: MM:SS)` (transient), the button
  label `取消`, and the NTP clock label `m_ntpLabel` showing the current NTP **minute:second** as
  `%02d:%02d`, refreshed at most once per second while synced (and reset to `--:--` when sync is lost).
* Latency compensation semantics (comment A3 in source): it is applied **only** to scheduled
  playback — the trigger fires early by `latency` so notes arrive on time through the network. Manual
  playback never applies it. `m_latency_comp_us = spinner_value_ms * 1000`.

---

## 11. Config persistence

### 11.1 Location and mechanism

* File: **`config.ini` in the directory of the executable**
  (`wxFileName(wxStandardPaths::Get().GetExecutablePath()).GetPath() + "config.ini"`).
* `wxFileConfig("wx_GO_MIDI", "wx_GO_MIDI", <that path>, "", wxCONFIG_USE_LOCAL_FILE)`, owned by
  `std::unique_ptr<wxConfigBase> m_config`.
* `App::OnInit` opens the same file separately to bootstrap `/Global/LogLevel` (default written as
  `info`) and `/Global/LogEnabled` (default written as `0` = file logging off).
* **[NOTE]** logical paths below are what the code passes to `SetPath`/`Read`/`Write`. On disk
  wxFileConfig renders groups as INI sections; because the style has no
  `wxCONFIG_USE_RELATIVE_PATH`, section names are the full logical path, e.g.
  `[Files/song.mid/Channel_0]`, `[Playlists/List_0]`, `[KeymapSchemes/List_1]`, `[Global]`,
  `[LastSelected]`. The README's `[Channel_0] / Window=… / Track=… / Transpose=… / Enabled=1`
  example is **stale** and does not match the current code (especially `Window` vs
  `WindowTitle/WindowPID/ProcessName`). **Did not verify against a real .ini file.**

### 11.2 Complete key inventory

| Path | Key | Type / default | Written by |
|---|---|---|---|
| `/Global` | `PlayMode` | string, `单曲播放` | `SaveGlobalConfig` (mode button) |
| `/Global` | `Decompose` | bool, `false` | `SaveGlobalConfig` (decompose button) |
| `/Global` | `LatencyComp` | int ms, `0` | `SaveGlobalConfig` (only when the button/mode change fires it) |
| `/Global` | `WinW`, `WinH`, `WinX`, `WinY` | int, `0` | `SaveWindowGeometry` (**only on window close**) |
| `/Global` | `CurrentKeymap` | string; `""`/`@builtin_0` = FF14, `@builtin_1` = Yys, else scheme name | `SaveKeymapConfig` |
| `/Global` | `FF14Pitch`, `FF14PitchMax` | int | keymap editor / `SaveKeymapConfig` |
| `/Global` | `YYPitch`, `YYPitchMax` | int | keymap editor / `SaveKeymapConfig` |
| `/Global` | `MinPitch`, `MaxPitch` | int (legacy) | read-only fallback for the FF14 range when `FF14Pitch` is absent |
| `/Global` | `LogLevel` (`info`), `LogEnabled` (`0`) | string / long | `App.cpp` bootstrap |
| `/Playlists` | `Count`, `CurrentIndex` | long | `PlaylistManager::SaveConfig` |
| `/Playlists/List_<i>` | `Name`, `FileCount`, `File_<j>` | string / long / string | `PlaylistManager::SaveConfig` |
| `/KeymapSchemes` | `Count` | long | `SaveKeymapConfig` |
| `/KeymapSchemes/List_<i>` | `Name`, `NoteCount`, `Note<j>` = `"pitch,vk,modifier"` | string / long / string | `SaveKeymapConfig`, keymap editor |
| `/KeymapSchemes/List_<i>` | `Pitch`, `PitchMax` | int | keymap editor `WriteSchemePitch` |
| `/Files/<sanitized filename>/Channel_<n>` | `Enabled` | bool; only written when ≠ default (`n == 0` ⇒ true) | `SaveFileConfig` |
| `/Files/<name>/Channel_<n>` | `WindowTitle`, `WindowPID`, `ProcessName` | string / long / string | `SaveFileConfig` (only when a window is actually selected) |
| `/Files/<name>/Channel_<n>` | `Transpose` | int; written only when ≠ 0 | `SaveFileConfig` |
| `/Files/<name>/Channel_<n>` | `Track` | string (display label, e.g. `1: Piano`); written only when ≠ `全部音轨` | `SaveFileConfig` |
| `/LastSelected` | `FilePath` | string | `SaveLastSelectedFile` (on close) |

`<sanitized filename>` = `path.AfterLast('\\')` with every `/` and `\` replaced by `_`.
The per-file group is deleted entirely when no channel has anything non-default to store; a channel
group is deleted when all its entries are default. Selecting `未选择` **does not** delete a stored
window entry (so the 5 s recovery scan can re-attach it).

### 11.3 Save/restore policy and timing

| Trigger | Behaviour |
|---|---|
| Channel toggle / window / transpose / track change | `RequestConfigSave(ConfigSaveKind::File)` → 300 ms trailing-edge debounce → `SaveFileConfig()` (no-op when no file is loaded) |
| Mode button, decompose button | immediate `SaveGlobalConfig()` |
| Playlist add/remove/clear/create/delete/rename/switch/import/reorder | immediate `SavePlaylistConfig()` (`DeleteGroup("/Playlists")` + rewrite + `Flush`) |
| Keymap scheme switch / new / rename / delete / import | immediate `SaveKeymapConfig()` (`DeleteGroup("KeymapSchemes")` + rewrite all schemes + write `/Global/CurrentKeymap` + `Flush`) |
| Keymap editor pitch change | 300 ms debounce → `WriteSchemePitch` for the active scheme; flushed on dialog close |
| Window close | `FlushConfigSave()`, `SaveLastSelectedFile()`, `SaveWindowGeometry()` |
| Speed spin change | **nothing persisted** |

Restore order in the constructor: `InitUI()` → `LoadGlobalConfig()` → `LoadPlaylistConfig()` →
`LoadKeymapConfig()` → `LoadLastSelectedFile()` → start timers → `NtpClient::StartAutoSync()` →
state machine to `Idle` → status `时间同步中...` → `InstallGlobalHook()`.

* `LoadGlobalConfig`: PlayMode, Decompose (+ button label + `engine.set_decompose`), LatencyComp
  (+ spinner + `m_latency_comp_us`), window geometry; the pitch range is **not** read here (it comes
  from the keymap scheme).
* `LoadLastSelectedFile`: if `/LastSelected/FilePath` exists on disk and is present in the current
  playlist → `PlayIndex(index, autoPlay = false)` (selects and loads but does not play).
* Keymap restore: `@builtin_N` sentinel → select that built-in and load it; otherwise exact
  name match in `m_keymapFiles` → select index `i + 2` and load. Pitch range then applied via
  `ApplySchemePitch` → `engine.set_pitch_range(min,max)`. When there is no saved `CurrentKeymap`
  the FF14 default (48–84) is applied.

---

## 12. Global hotkey

| Property | Value |
|---|---|
| Key | **F12** (`VK_F12 = 0x7B`) |
| Mechanism | `SetWindowsHookEx(WH_KEYBOARD_LL, LowLevelKeyboardProc, GetModuleHandle(NULL), 0)` — low-level keyboard hook, process module handle, thread id 0 (global) |
| Trigger condition | `nCode == HC_ACTION && wParam == WM_KEYUP && pKey->vkCode == VK_F12` (key **release** only, so held F12 does not auto-repeat) |
| Action | `wxPostEvent(g_pMainFrame, wxCommandEvent(wxEVT_COMMAND_BUTTON_CLICKED, ID_PLAY_BTN))` → the same handler as the 播放 button, `MainFrame::OnPlay` |
| What it toggles | If a MIDI file is loaded: `playing && paused` → resume; `playing && !paused` → pause; `!playing` → start. If no file is loaded, `OnPlay` does nothing |
| Event propagation | `CallNextHookEx` is **always** called, so F12 still reaches the focused application |
| Lifetime | Installed at the end of the MainFrame constructor; uninstalled in `~MainFrame` (`UnhookWindowsHookEx`) |
| Self-healing | Every 300 × 100 ms = 30 s the timer checks `g_hKeyboardHook`; if null it logs `全局热键钩子已丢失，正在重新安装...` and reinstalls |
| Requires | the app to be running (hook dies with the process); admin rights are already requested for keyboard simulation |

There are no other global or application-level hotkeys, and no menu accelerators.

---

## 13. Keyboard/mouse interaction catalogue

### 13.1 Keyboard

| Context | Key | Effect |
|---|---|---|
| anywhere in Windows | **F12** (release) | global play/pause toggle (§12) |
| playlist search box | any text | live filter (`EVT_TEXT`); `Enter` is accepted (`wxTE_PROCESS_ENTER`) but has no extra handler |
| channel transpose spin | `Enter` | commit value (`EVT_TEXT_ENTER`) |
| keymap editor / piano roll | click a key, then press any key | bind that key to the selected note; modifiers Ctrl/Shift/Alt are taken from `wxGetModifiers`, and *held mouse buttons* are added as mouse modifier bits (`wxGetMouseState`) |
| keymap editor / piano roll | `Esc` | cancel the pending selection |
| keymap editor / piano roll | pure modifier keydown (`WXK_SHIFT/CONTROL/ALT`) | ignored but **not** swallowed (`event.Skip()`) |

### 13.2 Mouse

| Control | Gesture | Effect |
|---|---|---|
| playlist list | single click | when stopped: load the file **without** playing; while playing: only move `m_current_play_index` (the switch happens on stop) |
| playlist list | double click / activate | `PlayIndex(viewIndex)` — load and play |
| playlist list | drag | reorder (see §8.4; **[QUIRK]** not persisted) |
| playlist list | hover | tooltip = full path |
| progress slider | left drag / click | scrub: `m_is_dragging_slider` suppresses engine-driven updates; the current-time label follows during the drag; on release `engine.seek(value/1000)` and, if stopped, start playing and label the button `暂停` |
| progress slider | right click ×1 / ×2 / ×3 | AB point A / AB point B (starts looping at A) / clear — see §9 |
| progress slider | right-drag near A or B | reposition A/B (cursor becomes size-WE) |
| progress slider | hover near A or B | size-WE cursor |
| 模式 button | click | cycle the 5 play modes, relabel, save globally, reset the shuffle when entering random |
| 普通模式/和弦分解 button | click | toggle `engine.set_decompose`, relabel, save globally |
| 定时 button | click | start/cancel scheduled playback (§10) |
| 上一曲/下一曲 | click | mode-dependent navigation (§8.5) |
| 播放 | click | resume / pause / start (same handler as F12) |
| 停止 | click | stop, reset slider+current label to 0/`00:00`, keep AB points; if the selection changed while playing, load the newly selected file |
| window dropdown (per channel) | left click | refreshes the window list before the popup opens |
| main panel | drop `.mid`/`.midi` files | import into the current playlist |
| keymap editor | click piano key | select it (outside the target range: refuses with `该音符在目标音域外, 请先扩展音域再绑定`) |
| keymap editor | right click piano key | delete that note's binding (`已清除该音符绑定`); ignored on the scrollbar strip |
| keymap editor | mouse wheel | horizontal scroll, 40 px per notch, consumed (not bubbled) |
| keymap editor | drag the bottom scrollbar / click the track | scroll horizontally |
| keymap editor | scheme dropdown / ＋ / － / 重命名 / 加载 / 导出 | see §6.7 |

### 13.3 Context menus

**[ABSENT]** — there are no `wxMenu`/popup menus anywhere; every action is a button, a dropdown or a
click/drag gesture on a control.

### 13.4 Window/close lifecycle

`OnClose`: set `m_isShuttingDown` → `FlushConfigSave()` → `m_engine.stop()` →
`NtpClient::ForceShutdown()` → `SaveLastSelectedFile()` → `SaveWindowGeometry()` → stop the three UI
timers → `m_engine.shutdown()` (join) → wait ≤100 ms per background future (warning
`后台线程未在 100ms 内退出，强制关闭`) → `event.Skip()`. `~MainFrame` uninstalls the global hook.

---

## 14. Reimplementation notes: quirks, gaps, decisions required

1. **Drag reorder is not persisted** (§8.4) — mirror vs manager divergence. Fix by reordering the
   manager's vector.
2. **Scheduled target `+= 1 hour`, not `+1 day`** (§10.2) — a time earlier in the day fires within
   the hour.
3. **Scheduled trigger uses the play/pause toggle** and does not seek to 0 (§10.2).
4. **SMPTE 29.97 drop-frame is dropped** by `init_tempo_map` (§3.5).
5. **Drag of A past B** leaves the slider's own markers un-normalised (§9.1).
6. **AB loop accuracy** is bounded by the 100 ms UI poll, not by the engine (§9.2).
7. **OnPrev under random mode behaves like next** (§8.5) — intentional in source but surprising.
8. **Out-of-range pitches are dropped, not folded**, when a manual (non-zero) transpose is used (§5.5).
9. **`refresh` of the progress slider** is rate-limited to ~50 ms and to a >100 ms delta (§2.4); a
   120 Hz-accurate playhead in WinUI would be a deliberate improvement, not a parity requirement.
10. **All parsing happens on the UI thread** — long MIDI files freeze the window. Background parsing
    plus cancellation is recommended for the C# version.
11. **`GetWindowList` includes every visible titled window**; consider filtering to top-level,
    non-owned windows with a non-empty process name for the WinUI version, while keeping the
    `title(pid)` label format and the `未选择` first item.
12. **Keymap text format cannot express non-ASCII keys** (function keys, arrows, space, numpad): the
    piano-roll editor can bind them but the exporter silently skips them (§6.4, §6.6).
13. **Windows VK codes** are used end-to-end (`vk_code` in config, `.txt` files and the editor). A
    C# port should keep raw Win32 VK integers as the persisted representation rather than the
    `Windows.System.VirtualKey` enum, which lacks the OEM keys.
14. **`INPUT.ki.wScan = 0`** for every keyboard event — deliberate; keep it for games reading
    `SendInput`.
15. **[VERIFY]** the U+200B in `AUTHOR_SIGNATURE` and the exact on-disk INI section rendering; both
    need a byte-level check against a built binary's `config.ini`.
16. **[ABSENT]** no countdown UI, no menus, no settings dialog, no per-playlist export, no tests, and
    no `keymaps/` sample file in the repository.
