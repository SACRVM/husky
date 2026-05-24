using Retro.Crt;
using Retro.Crt.Input;

namespace Husky;

/// <summary>
/// The interactive-mode host (LEASH §10.4). Husky no longer takes over the
/// alt-screen: log lines flow into the terminal's native scrollback via
/// <see cref="Crt.Write(string)"/>, and a 3-row <see cref="StickyFooter"/>
/// pinned to the bottom carries the status line, a separator, and the
/// action bar. Keyboard hotkeys arrive through a self-driven
/// <see cref="RawMode"/> + <see cref="TerminalInput.WaitForEvent(int, out InputEvent)"/>
/// loop on the calling thread.
///
/// This replaces the old <c>Retro.Crt.Tui.Application</c> host, whose
/// blocking <c>ReadEvent</c> loop only repainted on input — so a launcher
/// fed entirely from background threads showed nothing until a keypress
/// (the "shows nothing, dumps at exit" bug). With native scrollback every
/// <see cref="Append"/> writes immediately; the footer is independent.
///
/// Implements <see cref="ConsoleOutput.IConsoleSink"/> so every
/// <c>ConsoleOutput.Husky</c>/<c>AppOut</c>/<c>AppErr</c>/<c>Pipe</c> call
/// from any thread renders a line and feeds the in-memory ring used by
/// <c>[s]</c> save-logs (LEASH §10.7).
///
/// Lifecycle:
///   <list type="number">
///     <item><see cref="Run"/> enters raw mode, starts the footer, and
///       blocks the calling thread on the input loop.</item>
///     <item>The launcher runtime executes on a separate task; when it
///       finishes (or the user presses Esc/x) <see cref="Dismiss"/> flips
///       a flag the loop checks each poll, and <see cref="Run"/> returns
///       after tearing the footer and raw mode down.</item>
///   </list>
/// </summary>
internal sealed class HuskyApp : ConsoleOutput.IConsoleSink
{
    private const int FooterRows = 3;
    // Poll cadence for the input loop. Bounds how long Dismiss() takes to
    // unblock Run() and how crisp a lone Escape feels (WaitForEvent commits
    // a buffered ESC after a non-zero timeout, resolving ESC-vs-CSI).
    private const int PollMs = 150;
    private const int RingCap = 5_000;

    private readonly string launcherVersion;
    private readonly string workingDirectory;
    private readonly Action onUpdateRequested;
    private readonly Action onExitRequested;

    // Serialises multi-segment scrollback writes across the background
    // threads that call Append / in-place updates so two lines can't
    // interleave their escape sequences. StickyFooter.Refresh is documented
    // thread-safe on its own and is not covered by this lock.
    private readonly object writeLock = new();

    // [s] save-logs ring (LEASH §10.7). Plain rendered text, oldest dropped
    // past RingCap. Guarded by its own lock so a save snapshot doesn't block
    // a console write.
    private readonly object ringLock = new();
    private readonly List<string> ring = new(256);
    // True while an in-place line owns the ring's tail entry, mirroring the
    // single line the user sees animating in the scrollback.
    private bool ringTailIsInPlace;

    // Footer state, guarded by stateLock; the paint callback snapshots under
    // the same lock. Mirrors the old HuskyChrome header/action-bar fields.
    private readonly object stateLock = new();
    private string? appName;
    private string? appVersion;
    private string? health;
    private string? crashRestart;
    private UpdateActionState updateState;
    private int toastGeneration;
    private string? toastText;
    private Color toastColor;

    // Gate for the single in-place line (LEASH §10.6). 0 = none, 1 = open.
    private int inPlaceClaimed;
    // True between an in-place open and its complete/end. Read & written only
    // under writeLock. While set, regular Append lines are buffered so they
    // don't scroll the animating line out of reach, then flushed on close.
    private bool inPlaceActive;
    private readonly Queue<GatedLine> gated = new();

    private StickyFooter? footer;
    private volatile bool dismissed;

    public HuskyApp(
        string launcherVersion,
        string workingDirectory,
        Action onUpdateRequested,
        Action onExitRequested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(onUpdateRequested);
        ArgumentNullException.ThrowIfNull(onExitRequested);

        this.launcherVersion = launcherVersion;
        this.workingDirectory = workingDirectory;
        this.onUpdateRequested = onUpdateRequested;
        this.onExitRequested = onExitRequested;
    }

    /// <summary>
    /// Enter raw mode, start the sticky footer, and block on the input loop.
    /// Returns when <see cref="Dismiss"/> is called or the user triggers an
    /// exit hotkey. Degrades gracefully: if raw mode can't be entered the
    /// loop still blocks (so the runtime keeps writing) but without hotkeys;
    /// the footer is a no-op on non-interactive terminals.
    /// </summary>
    public void Run()
    {
        IDisposable? raw = null;
        // keepSignals: true leaves Ctrl-C generating a real SIGINT so
        // Program.cs's CancelKeyPress handler keeps owning graceful/hard
        // shutdown — a hung launcher can't swallow the user's Ctrl-C.
        try { raw = RawMode.Enter(keepSignals: true); }
        catch { raw = null; }

        try
        {
            footer = StickyFooter.Start(FooterRows, Paint);

            if (raw is null)
            {
                // No key input available — just keep the thread parked so the
                // runtime task can write through us until it dismisses.
                while (!dismissed) Thread.Sleep(PollMs);
                return;
            }

            while (!dismissed)
            {
                if (TerminalInput.WaitForEvent(PollMs, out InputEvent ev)
                    && ev.Kind == InputEventKind.Key)
                {
                    HandleKey(ev.Key);
                }
            }
        }
        finally
        {
            footer?.Dispose();
            footer = null;
            raw?.Dispose();
        }
    }

    /// <summary>
    /// Signal the input loop to leave <see cref="Run"/>. Safe from any
    /// thread; the loop notices within <see cref="PollMs"/>.
    /// </summary>
    public void Dismiss() => dismissed = true;

    private void HandleKey(KeyEvent key)
    {
        if (key.Key == Key.Escape) { onExitRequested(); return; }
        if (key.Key != Key.Glyph) return;

        char g = key.Glyph;
        // Ctrl+C usually still routes to the SIGINT handler (keepSignals),
        // but honour it here too in case a terminal surfaces it as a glyph.
        if (g is 'c' or 'C' && (key.Modifiers & KeyModifiers.Ctrl) != 0)
        {
            onExitRequested();
            return;
        }
        // Plain letters only — no Ctrl/Alt (Shift tolerated for caps).
        if ((key.Modifiers & ~KeyModifiers.Shift) != KeyModifiers.None) return;

        switch (g)
        {
            case 's' or 'S': SaveLogsToFile(); break;
            case 'u' or 'U': onUpdateRequested(); break;
            case 'x' or 'X': onExitRequested(); break;
        }
    }

    // ── IConsoleSink ──────────────────────────────────────────────────────

    public void Append(
        DateTime when, string source, Color sourceColor, string message,
        Color? messageColor, bool force)
    {
        var segments = ConsoleOutput.BuildLine(when, source, sourceColor, message, messageColor);
        lock (writeLock)
        {
            if (inPlaceActive)
            {
                // An in-place line owns the bottom row; buffer regular lines
                // so they don't scroll it out of reach. `force` can't safely
                // bypass without clobbering the animating line, so it queues
                // too — but we beep so an escalation isn't silently delayed.
                if (force) Crt.Bell();
                gated.Enqueue(new GatedLine(segments));
                return;
            }
            WriteSegmentLine(segments, newline: true);
        }
        PushRing(Plain(segments));
    }

    // No-op: nothing in Husky calls ConsoleOutput.BeginLiveWidget — spinners
    // and the download progress bar all go through BeginInPlaceLine. Kept to
    // satisfy the sink contract.
    public IDisposable BeginLiveWidget() => NoopScope.Instance;

    public ConsoleOutput.IInPlaceLine BeginInPlaceLine(
        string source, Color sourceColor, string initialMessage)
    {
        if (Interlocked.CompareExchange(ref inPlaceClaimed, 1, 0) != 0)
            throw new InvalidOperationException(
                "ConsoleOutput already has an active in-place line — only one at a time.");

        // §10.6: the start timestamp freezes for the life of the line.
        DateTime when = DateTime.Now;
        var segments = ConsoleOutput.BuildLine(when, source, sourceColor, initialMessage);
        lock (writeLock)
        {
            // The cursor sits at column 0 of a fresh row after the last
            // WriteLine; render the opening frame without a trailing newline
            // so updates can rewrite it in place.
            WriteSegmentLine(segments, newline: false);
            inPlaceActive = true;
        }
        OpenRingTail(Plain(segments));

        return new FooterInPlaceLine(this, when, source, sourceColor);
    }

    public void SetAppInfo(string? appName, string? appVersion)
    {
        lock (stateLock) { this.appName = appName; this.appVersion = appVersion; }
        footer?.Refresh();
    }

    public void SetHealth(string? status)
    {
        lock (stateLock) health = status;
        footer?.Refresh();
    }

    public void SetCrashRestart(string? message)
    {
        lock (stateLock) crashRestart = message;
        footer?.Refresh();
    }

    public void SetUpdateActionState(UpdateActionState state)
    {
        lock (stateLock) updateState = state;
        footer?.Refresh();
    }

    // ── in-place line plumbing (called under writeLock by FooterInPlaceLine) ──

    private void InPlaceUpdate(DateTime when, string source, Color sourceColor, string message)
    {
        var segments = ConsoleOutput.BuildLine(when, source, sourceColor, message);
        lock (writeLock)
        {
            if (!inPlaceActive) return;
            // Rewrite the current row: carriage-return to column 0, repaint,
            // clear any tail left by a previously longer frame.
            Crt.Write("\r");
            WriteSegmentLine(segments, newline: false);
            Crt.ClrEol();
        }
        ReplaceRingTail(Plain(segments));
    }

    private void InPlaceComplete(DateTime when, string source, Color color, string message)
    {
        var segments = ConsoleOutput.BuildLine(when, source, color, message);
        lock (writeLock)
        {
            if (inPlaceActive)
            {
                Crt.Write("\r");
                WriteSegmentLine(segments, newline: false);
                Crt.ClrEol();
                Crt.WriteLine();
                inPlaceActive = false;
            }
            else
            {
                WriteSegmentLine(segments, newline: true);
            }
            // Replace the in-place tail with the final frame BEFORE flushing
            // the gated lines, so the ring matches the on-screen order
            // (completed line first, then the buffered lines below it).
            ReplaceRingTail(Plain(segments));
            FlushGated();
        }
        Interlocked.Exchange(ref inPlaceClaimed, 0);
    }

    private void InPlaceEnd()
    {
        lock (writeLock)
        {
            if (inPlaceActive)
            {
                // Leave the last frame visible; commit the row so subsequent
                // lines land below it.
                Crt.WriteLine();
                inPlaceActive = false;
            }
            FlushGated();
        }
        // The ring already holds the last frame as its tail; just release it.
        lock (ringLock) ringTailIsInPlace = false;
        Interlocked.Exchange(ref inPlaceClaimed, 0);
    }

    // Must be called under writeLock.
    private void FlushGated()
    {
        while (gated.Count > 0)
        {
            GatedLine line = gated.Dequeue();
            WriteSegmentLine(line.Segments, newline: true);
            PushRing(Plain(line.Segments));
        }
    }

    // Must be called under writeLock.
    private static void WriteSegmentLine(
        IReadOnlyList<ConsoleOutput.LineSegment> segments, bool newline)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            ConsoleOutput.LineSegment seg = segments[i];
            if (seg.Color is { } c)
            {
                using (Crt.WithStyle(fg: c)) Crt.Write(seg.Text);
            }
            else
            {
                Crt.Write(seg.Text);
            }
        }
        if (newline) Crt.WriteLine();
    }

    private static string Plain(IReadOnlyList<ConsoleOutput.LineSegment> segments)
    {
        if (segments.Count == 1) return segments[0].Text;
        var sb = new System.Text.StringBuilder(64);
        for (int i = 0; i < segments.Count; i++) sb.Append(segments[i].Text);
        return sb.ToString();
    }

    // ── [s] save-logs ring (LEASH §10.7) ──────────────────────────────────

    private void PushRing(string line)
    {
        lock (ringLock)
        {
            ring.Add(line);
            ringTailIsInPlace = false;
            TrimRing();
        }
    }

    private void OpenRingTail(string line)
    {
        lock (ringLock)
        {
            ring.Add(line);
            ringTailIsInPlace = true;
            TrimRing();
        }
    }

    private void ReplaceRingTail(string line)
    {
        lock (ringLock)
        {
            if (ringTailIsInPlace && ring.Count > 0) ring[^1] = line;
            else { ring.Add(line); ringTailIsInPlace = true; TrimRing(); }
        }
    }

    // Must be called under ringLock.
    private void TrimRing()
    {
        int excess = ring.Count - RingCap;
        if (excess > 0) ring.RemoveRange(0, excess);
    }

    /// <summary>
    /// Snapshot the ring and write it to
    /// <c>husky-logs-&lt;UTC-timestamp&gt;.txt</c> in the working directory.
    /// The file IO runs on a background task so the input loop stays
    /// responsive; the result lands as a footer toast (green on success,
    /// yellow on failure) rather than a log line — the user just dumped the
    /// buffer, echoing a confirmation into it would be noise.
    /// </summary>
    public void SaveLogsToFile()
    {
        List<string> snapshot;
        lock (ringLock) snapshot = [.. ring];

        string fileName = $"husky-logs-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}Z.txt";
        // §10.4: save-logs lands in the resolved working directory, not the
        // process CWD — `--dir` may have pointed Husky elsewhere.
        string path = Path.Combine(workingDirectory, fileName);

        Task.Run(() =>
        {
            try
            {
                File.WriteAllLines(path, snapshot);
                ShowToast($"wrote {snapshot.Count} lines -> {fileName}", Color.LightGreen);
            }
            catch (Exception ex)
            {
                ShowToast($"save logs failed: {ex.Message}", Color.Yellow);
            }
        });
    }

    /// <summary>
    /// Replace the action-bar row with a transient toast for
    /// <paramref name="duration"/> (default 3s, LEASH §10.4). Safe from any
    /// thread; supersession uses a generation counter so a stale clear can't
    /// wipe a newer toast.
    /// </summary>
    private void ShowToast(string text, Color color, TimeSpan? duration = null)
    {
        int generation;
        lock (stateLock)
        {
            generation = ++toastGeneration;
            toastText = text;
            toastColor = color;
        }
        footer?.Refresh();
        _ = ClearToastAfterAsync(generation, duration ?? TimeSpan.FromSeconds(3));
    }

    private async Task ClearToastAfterAsync(int generation, TimeSpan duration)
    {
        try { await Task.Delay(duration).ConfigureAwait(false); }
        catch { return; }

        lock (stateLock)
        {
            if (toastGeneration != generation) return;
            toastText = null;
            toastColor = default;
        }
        footer?.Refresh();
    }

    // ── footer paint ──────────────────────────────────────────────────────

    private void Paint(ScreenBuffer screen)
    {
        // ScreenBuffer is sized Width × FooterRows. Row 0 = status line,
        // row 1 = separator rule, row 2 = action bar (or a toast).
        if (screen.Width <= 0 || screen.Height <= 0) return;

        string? localName, localVer, localHealth, localCrash, localToast;
        Color localToastColor;
        UpdateActionState localState;
        lock (stateLock)
        {
            localName = appName;
            localVer = appVersion;
            localHealth = health;
            localCrash = crashRestart;
            localToast = toastText;
            localToastColor = toastColor;
            localState = updateState;
        }

        screen.Clear(new Cell(' ', Color.LightGray, Color.Black));

        DrawStatusRow(screen, 0, localName, localVer, localHealth, localCrash);
        if (screen.Height > 1) DrawSeparatorRow(screen, 1);
        if (screen.Height > 2) DrawActionRow(screen, 2, localState, localToast, localToastColor);
    }

    private void DrawStatusRow(
        ScreenBuffer screen, int y, string? name, string? ver, string? health, string? crash)
    {
        int width = screen.Width;

        // Left: launcher branding in the husky accent colour.
        string left = $" husky v{launcherVersion}";
        int leftLen = Math.Min(left.Length, width);
        screen.PutString(0, y, left.AsSpan(0, leftLen),
            Color.LightCyan, Color.Black, CellAttrs.Bold);

        // Right: crash-restart override wins over live health.
        (string text, Color color) right = ResolveRightSlot(health, crash);
        int rightLen = Math.Min(right.text.Length, width - leftLen);
        int rightX = width - rightLen;
        if (rightLen > 0 && rightX >= leftLen)
        {
            screen.PutString(rightX, y, right.text.AsSpan(0, rightLen),
                right.color, Color.Black, CellAttrs.Bold);
        }

        // Middle: app name + version, centered. '(starting…)' pre-handshake.
        // Dropped entirely when it can't fit — a truncated name is worse.
        string mid = name is null
            ? "(starting…)"
            : (ver is null ? name : $"{name} v{ver}");
        Color midColor = name is null ? Color.DarkGray : Color.White;
        int slotStart = leftLen + 1;
        int slotEnd = (rightLen > 0 ? rightX : width) - 1;
        int slotWidth = slotEnd - slotStart;
        if (slotWidth >= mid.Length)
        {
            int midX = slotStart + (slotWidth - mid.Length) / 2;
            screen.PutString(midX, y, mid.AsSpan(), midColor, Color.Black, CellAttrs.Bold);
        }
    }

    private static void DrawSeparatorRow(ScreenBuffer screen, int y)
    {
        // U+2500 BOX DRAWINGS LIGHT HORIZONTAL across the full width.
        screen.PutString(0, y, new string('─', screen.Width).AsSpan(),
            Color.DarkGray, Color.Black, CellAttrs.None);
    }

    private static void DrawActionRow(
        ScreenBuffer screen, int y, UpdateActionState state, string? toast, Color toastColor)
    {
        int width = screen.Width;
        if (toast is not null)
        {
            int toastLen = Math.Min(toast.Length, width - 2);
            if (toastLen > 0)
                screen.PutString(1, y, toast.AsSpan(0, toastLen),
                    toastColor, Color.Black, CellAttrs.Bold);
            return;
        }

        int x = 1;
        x = DrawCommand(screen, x, y, width, "s", "save logs");
        if (state != UpdateActionState.Hidden)
        {
            x = DrawDotSeparator(screen, x, y, width);
            x = DrawCommand(screen, x, y, width, "u", "check & install");
        }
        x = DrawDotSeparator(screen, x, y, width);
        DrawCommand(screen, x, y, width, "x", "exit");
    }

    private static int DrawCommand(
        ScreenBuffer screen, int x, int y, int max, string hotkey, string label)
    {
        int hkLen = Math.Min(hotkey.Length, max - x);
        if (hkLen > 0)
            screen.PutString(x, y, hotkey.AsSpan(0, hkLen),
                Color.LightCyan, Color.Black, CellAttrs.Bold);
        x += hkLen;

        if (x < max)
        {
            screen.PutString(x, y, " ".AsSpan(), Color.LightGray, Color.Black, CellAttrs.None);
            x += 1;
        }

        int lblLen = Math.Min(label.Length, max - x);
        if (lblLen > 0)
            screen.PutString(x, y, label.AsSpan(0, lblLen),
                Color.LightGray, Color.Black, CellAttrs.None);
        return x + lblLen;
    }

    private static int DrawDotSeparator(ScreenBuffer screen, int x, int y, int max)
    {
        // U+00B7 MIDDLE DOT, padded by a space on each side.
        const string Sep = " · ";
        int len = Math.Min(Sep.Length, max - x);
        if (len > 0)
            screen.PutString(x, y, Sep.AsSpan(0, len), Color.LightGray, Color.Black, CellAttrs.None);
        return x + len;
    }

    private static (string text, Color color) ResolveRightSlot(string? health, string? crash)
    {
        if (crash is not null) return ($" {crash} ", Color.LightRed);
        if (health is not null) return ($" {health} ", HealthColour(health));
        return (string.Empty, Color.LightGray);
    }

    private static Color HealthColour(string status) => status switch
    {
        "healthy"   => Color.LightGreen,
        "degraded"  => Color.Yellow,
        "unhealthy" => Color.LightRed,
        _           => Color.Black,
    };

    private readonly record struct GatedLine(IReadOnlyList<ConsoleOutput.LineSegment> Segments);

    /// <summary>
    /// In-place line for interactive mode: each <see cref="Update"/> rewrites
    /// the current scrollback row via a carriage return; <see cref="Complete"/>
    /// rewrites it once more and commits the row with a newline. Updates are
    /// throttled to 10 Hz at the enqueue side so a tight download loop doesn't
    /// spam escape sequences.
    /// </summary>
    private sealed class FooterInPlaceLine(
        HuskyApp owner, DateTime when, string source, Color sourceColor) : ConsoleOutput.IInPlaceLine
    {
        private const long ThrottleMs = 100;

        private long lastUpdateTick = Environment.TickCount64;
        private bool completed;
        private bool disposed;

        public void Update(string message)
        {
            if (completed || disposed) return;
            long now = Environment.TickCount64;
            if (now - lastUpdateTick < ThrottleMs) return;
            lastUpdateTick = now;
            owner.InPlaceUpdate(when, source, sourceColor, message);
        }

        public void UpdateNow(string message)
        {
            if (completed || disposed) return;
            // §10.6: 0% / 100% frame guarantee — bypass the throttle so the
            // final progress frame lands before the Complete summary.
            lastUpdateTick = Environment.TickCount64;
            owner.InPlaceUpdate(when, source, sourceColor, message);
        }

        public void Complete(string finalMessage, Color? finalMessageColor = null)
        {
            if (completed) return;
            completed = true;
            // §10.6: the completion line gets a fresh timestamp so the buffer
            // reads 'started at X, finished at Y'.
            owner.InPlaceComplete(
                DateTime.Now, source, finalMessageColor ?? sourceColor, finalMessage);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!completed)
            {
                owner.InPlaceEnd();
                completed = true;
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
