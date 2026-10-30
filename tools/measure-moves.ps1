#requires -Version 5.1
<#
.SYNOPSIS
  Shows the raw mouse counts and the pointer path a window receives, to compare a recorded mouse pattern of the
  WKI AutoClicker ("Mausbewegung") with its replay.

.DESCRIPTION
  Opens a topmost measuring window in the top-right corner of the primary work area. It has two views of every
  mouse motion, the same two the pattern recorder keeps:

  - Raw (orange): the mouse's Raw Input, through the window's own registration (foreground only, so the window must
    be in front). Relative counts are summed per source: "physisch" (a device; exactly what the recorder records),
    "WKI" (injected by the clicker helper, dwExtraInfo "WKIC") and "fremd" (injected by anything else, no device).
    Absolute records (the helper's "Zeiger" replay sends positions, not counts) are counted but not summed.
  - Pointer (blue): the WM_MOUSEMOVE path inside the window; its WKI count comes from the message's extra info.

  Both paths are drawn live at 1:1, the raw path as pixels starting where the pointer was when the motion began:
  with pointer speed 10/20 and "Zeigerbeschleunigung verbessern" off the two lines lie on top of each other. The
  pointer stops at screen edges, raw counts keep going. Pointer positions are physical pixels (the window runs
  per-monitor DPI aware, like the clipper).

  Motion is split into bursts: a burst ends after -GapMs (default 300 ms) without motion. Every finished burst is
  listed in the window and printed in the console: duration from its first to its last motion event, summed raw
  dx/dy with the record count per source, and the pointer's net dx/dy (cursor position after the burst minus before
  it, so it stays exact when the pointer leaves the window). "Reset" clears the totals, the list and the drawing.
  After the window closes, the bursts since the last reset are printed once more.

  Timestamps (QueryPerformanceCounter) are taken when the window's thread dispatches the message, not when the
  motion happened; the queue's own jitter is typically well under 1 ms.

  Live check from the design (only with Phillip present): keep this window in front, record a motion over it with
  the record hotkey, then replay the pattern on its hotkey at 1x/2x/0.5x, with Repeat, as "Zeiger" and as "Spiel",
  and compare each replay's burst with the recording's: same dx/dy, duration divided by the speed. A pause inside a
  pattern that is longer than -GapMs splits it into several bursts.

  The script injects no input, installs no hooks and writes no files.

.PARAMETER GapMs Idle time that ends a burst in ms (default 300, 50..5000).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\measure-moves.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\measure-moves.ps1 -GapMs 1000
#>
[CmdletBinding()]
param(
    [ValidateRange(50, 5000)]
    [int]$GapMs = 300
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Compiled by Windows PowerShell 5.1's C# 5 compiler: no string interpolation, no expression-bodied members.
# The window procedure reads and stamps every event itself, so PowerShell's slow event dispatch never delays one.
$source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WkiMeasureMoves
{
    // Summed relative raw counts of one source.
    public sealed class Tally
    {
        public long Dx;
        public long Dy;
        public int Count;

        public void Add(int dx, int dy)
        {
            Dx += dx;
            Dy += dy;
            Count++;
        }

        public void Clear()
        {
            Dx = 0;
            Dy = 0;
            Count = 0;
        }
    }

    // What one span (since the reset, or one burst) received.
    public sealed class Counts
    {
        public readonly Tally Physical = new Tally();
        public readonly Tally Wki = new Tally();
        public readonly Tally Foreign = new Tally();
        public int Absolute;
        public int AbsoluteWki;
        public int Pointer;
        public int PointerWki;

        public long RawDx { get { return Physical.Dx + Wki.Dx + Foreign.Dx; } }
        public long RawDy { get { return Physical.Dy + Wki.Dy + Foreign.Dy; } }
        public int Raw { get { return Physical.Count + Wki.Count + Foreign.Count; } }

        public void Clear()
        {
            Physical.Clear();
            Wki.Clear();
            Foreign.Clear();
            Absolute = 0;
            AbsoluteWki = 0;
            Pointer = 0;
            PointerWki = 0;
        }

        public string RawSources()
        {
            List<string> parts = new List<string>();
            if (Physical.Count > 0) parts.Add("physisch " + Physical.Count);
            if (Wki.Count > 0) parts.Add("WKI " + Wki.Count);
            if (Foreign.Count > 0) parts.Add("fremd " + Foreign.Count);
            string text = Raw == 0 ? "keine Datensaetze" : Raw + " Datensaetze: " + string.Join(", ", parts.ToArray());
            if (Absolute > 0) text += "; absolut " + Absolute + " (WKI " + AbsoluteWki + ")";
            return text;
        }

        public string PointerSources()
        {
            return Pointer + " Ereignisse" + (PointerWki > 0 ? ", WKI " + PointerWki : "");
        }
    }

    // Per-monitor DPI awareness for the calling thread, so pointer positions are physical pixels like the clicker
    // helper's (the clipper exe is PerMonitorV2, powershell.exe is DPI-unaware). Set before the window is created.
    public static class DpiScope
    {
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 and _PER_MONITOR_AWARE.
        static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        static readonly IntPtr PerMonitor = new IntPtr(-3);

        // The previous context to hand to Leave; IntPtr.Zero when Windows has no per-thread awareness.
        public static IntPtr Enter()
        {
            try
            {
                IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorV2);
                if (previous == IntPtr.Zero) previous = SetThreadDpiAwarenessContext(PerMonitor);
                return previous;
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        public static void Leave(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); }
            catch (EntryPointNotFoundException) { }
        }

        public static double SystemScale()
        {
            try { return Math.Max(1.0, GetDpiForSystem() / 96.0); }
            catch (EntryPointNotFoundException) { return 1.0; }
        }
    }

    // Clicking it never takes the keyboard focus, so Space or Enter (possible pattern hotkeys) never press it.
    sealed class NoFocusButton : Button
    {
        public NoFocusButton()
        {
            SetStyle(ControlStyles.Selectable, false);
        }
    }

    public sealed class MoveForm : Form
    {
        const int WM_INPUT = 0x00FF;
        const int WM_KEYDOWN = 0x0100;
        const int WM_MOUSEMOVE = 0x0200;
        const int VK_ESCAPE = 0x1B;
        const uint RID_INPUT = 0x10000003;
        const uint RIDEV_REMOVE = 0x00000001;
        const int RIM_TYPEMOUSE = 0;
        const int MOUSE_MOVE_ABSOLUTE = 0x01;
        const int MOUSE_ATTRIBUTES_CHANGED = 0x04;
        const int RawMouseSize = 24;
        const uint SPI_GETMOUSE = 0x0003;
        const uint SPI_GETMOUSESPEED = 0x0070;
        // dwExtraInfo of every event the clicker helper injects ("WKIC", InputInjector.Tag).
        const uint WkiTag = 0x574B4943;
        const int ShownBursts = 8;
        const int KeptBursts = 500;
        // GDI+ refuses far-off coordinates; a raw path that runs that far out of the window is not drawn on.
        const float DrawLimit = 100000f;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")]
        static extern IntPtr GetMessageExtraInfo();
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        static extern bool SpiInts(uint action, uint param, int[] values, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        static extern bool SpiInt(uint action, uint param, out int value, uint winIni);

        readonly double gapMs;
        readonly bool physicalPixels;
        readonly double scale;
        // RAWINPUTHEADER: dwType, dwSize, hDevice, wParam - 16 bytes in a 32-bit, 24 in a 64-bit PowerShell.
        readonly uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        IntPtr buffer;
        uint bufferSize;

        readonly Counts total = new Counts();
        readonly Counts burst = new Counts();
        readonly List<string> bursts = new List<string>();
        // Absolute devices repeat their position in every report; only a changed one is a move.
        readonly Dictionary<IntPtr, Point> absolutePositions = new Dictionary<IntPtr, Point>();
        int burstNumber;
        bool burstOpen;
        long burstFirst;
        long burstLast;
        Point burstOrigin;
        Point resetCursor;
        Point idleCursor;
        Point knownCursor;
        Point paintedCursor;
        PointF rawEnd;
        Point lastPointer;
        bool hasLastPointer;
        bool active;
        bool dirty;
        string rawError;
        string windowsMouse = "";

        Bitmap pointerLayer;
        Bitmap rawLayer;
        Graphics pointerGraphics;
        Graphics rawGraphics;
        readonly Pen pointerPen;
        readonly Pen rawPen;
        readonly Brush startBrush;
        readonly Brush backdrop;
        readonly Font font;
        readonly Button reset;
        readonly Timer refresh;

        // One line per finished burst and per reset, for the console.
        public event Action<string> Log;

        public MoveForm(int gapMs, bool physicalPixels)
        {
            this.gapMs = gapMs;
            this.physicalPixels = physicalPixels;
            scale = DpiScope.SystemScale();
            bufferSize = 128;
            buffer = Marshal.AllocHGlobal((int)bufferSize);

            Text = "WKI Bewegungs-Messung";
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            ForeColor = Color.Black;
            // No child control but the button: every motion over the window must reach this window's procedure.
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(Scaled(820), Scaled(620));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width, area.Top);

            font = new Font("Consolas", (float)(13 * scale), GraphicsUnit.Pixel);
            pointerPen = new Pen(Color.FromArgb(70, 130, 230), (float)(3 * scale));
            pointerPen.StartCap = LineCap.Round;
            pointerPen.EndCap = LineCap.Round;
            rawPen = new Pen(Color.FromArgb(230, 120, 20), (float)(1.5 * scale));
            rawPen.StartCap = LineCap.Round;
            rawPen.EndCap = LineCap.Round;
            startBrush = new SolidBrush(Color.FromArgb(40, 170, 70));
            backdrop = new SolidBrush(Color.FromArgb(215, 255, 255, 255));

            reset = new NoFocusButton();
            reset.Text = "Reset";
            reset.Font = font;
            reset.Size = new Size(Scaled(90), Scaled(30));
            reset.Location = new Point(ClientSize.Width - reset.Width - Scaled(8), Scaled(8));
            reset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            reset.Click += delegate { ResetAll(true); };
            Controls.Add(reset);

            refresh = new Timer();
            refresh.Interval = 40;
            refresh.Tick += delegate { Tick(); };
        }

        // The finished bursts since the last reset, oldest first.
        public string[] BurstLines()
        {
            return bursts.ToArray();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Flags 0 (no RIDEV_INPUTSINK): raw input arrives only while this window is in the foreground.
            if (!RegisterMouse(0, Handle))
                rawError = "Raw Input nicht verfuegbar (Fehler " + Marshal.GetLastWin32Error() + ") - nur der Zeiger wird gemessen.";
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            RegisterMouse(RIDEV_REMOVE, IntPtr.Zero);
            base.OnHandleDestroyed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // The real frame size is known only now: keep the window inside the work area, top-right.
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(area.Right - Width, area.Top);
            EnsureLayers();
            ResetAll(false);
            refresh.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            active = true;
            dirty = true;
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            active = false;
            dirty = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) EnsureLayers();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            // The pointer path continues where the pointer comes back in, without a line across.
            hasLastPointer = false;
            base.OnMouseLeave(e);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_INPUT:
                    OnRawInput(m.LParam, Stopwatch.GetTimestamp());
                    break;
                case WM_MOUSEMOVE:
                    OnPointer(m.LParam, Stopwatch.GetTimestamp());
                    break;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (msg.Msg == WM_KEYDOWN)
            {
                if ((int)msg.WParam == VK_ESCAPE)
                {
                    Close();
                    return true;
                }
                // Handled here, so a hotkey pressed into the window (Tab, arrows, Space) moves no focus.
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CloseBurst();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refresh.Stop();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            if (pointerLayer != null)
            {
                // Explicit pixel rectangles: DrawImage(image, x, y) would rescale by the bitmaps' DPI.
                g.DrawImage(pointerLayer, new Rectangle(0, 0, pointerLayer.Width, pointerLayer.Height));
                g.DrawImage(rawLayer, new Rectangle(0, 0, rawLayer.Width, rawLayer.Height));
            }
            int pad = Scaled(10);
            int bottom = DrawBlock(g, StatusText(), pad, pad, ClientSize.Width - reset.Width - 3 * pad, ForeColor, false);
            string warning = WarningText();
            if (warning != null)
                DrawBlock(g, warning, pad, bottom + pad, ClientSize.Width - reset.Width - 3 * pad, Color.Firebrick, false);
            DrawBlock(g, BurstText(), pad, ClientSize.Height - pad, ClientSize.Width - 2 * pad, ForeColor, true);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                refresh.Dispose();
                DisposeLayers();
                pointerPen.Dispose();
                rawPen.Dispose();
                startBrush.Dispose();
                backdrop.Dispose();
                font.Dispose();
            }
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
        }

        bool RegisterMouse(uint flags, IntPtr target)
        {
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[1];
            devices[0].UsagePage = 0x01;
            devices[0].Usage = 0x02;
            devices[0].Flags = flags;
            devices[0].Target = target;
            return RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        void OnRawInput(IntPtr rawInput, long now)
        {
            uint size = 0;
            if (GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0) return;
            if (size > bufferSize)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                bufferSize = size;
            }
            size = bufferSize;
            uint read = GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read < headerSize + RawMouseSize) return;
            if (Marshal.ReadInt32(buffer, 0) != RIM_TYPEMOUSE) return;

            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
            // RAWMOUSE: usFlags 0, (padding), usButtonFlags 4, usButtonData 6, ulRawButtons 8, lLastX 12, lLastY 16,
            // ulExtraInformation 20.
            int at = (int)headerSize;
            int flags = (ushort)Marshal.ReadInt16(buffer, at);
            int x = Marshal.ReadInt32(buffer, at + 12);
            int y = Marshal.ReadInt32(buffer, at + 16);
            bool wki = unchecked((uint)Marshal.ReadInt32(buffer, at + 20)) == WkiTag;
            if ((flags & MOUSE_ATTRIBUTES_CHANGED) != 0) return;

            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0)
            {
                // A position (0..65535), not a motion: counted, never summed.
                Point position = new Point(x, y);
                Point last;
                if (absolutePositions.TryGetValue(device, out last) && last == position) return;
                absolutePositions[device] = position;
                Motion(now);
                total.Absolute++;
                burst.Absolute++;
                if (wki)
                {
                    total.AbsoluteWki++;
                    burst.AbsoluteWki++;
                }
                return;
            }

            if (x == 0 && y == 0) return; // buttons or the wheel only
            Motion(now);
            Source(total, wki, device).Add(x, y);
            Source(burst, wki, device).Add(x, y);
            PointF next = new PointF(rawEnd.X + x, rawEnd.Y + y);
            DrawSegment(rawGraphics, rawPen, rawEnd, next);
            rawEnd = next;
        }

        static Tally Source(Counts counts, bool wki, IntPtr device)
        {
            if (wki) return counts.Wki;
            return device == IntPtr.Zero ? counts.Foreign : counts.Physical;
        }

        void OnPointer(IntPtr lParam, long now)
        {
            bool wki = GetMessageExtraInfo().ToInt64() == WkiTag;
            int value = unchecked((int)lParam.ToInt64());
            Point position = new Point(unchecked((short)(value & 0xFFFF)), unchecked((short)((value >> 16) & 0xFFFF)));
            if (!hasLastPointer)
            {
                // Entering the window (or a message on activation): a position, not yet a motion inside it.
                lastPointer = position;
                hasLastPointer = true;
                return;
            }
            if (position == lastPointer) return;

            Motion(now);
            total.Pointer++;
            burst.Pointer++;
            if (wki)
            {
                total.PointerWki++;
                burst.PointerWki++;
            }
            DrawSegment(pointerGraphics, pointerPen, lastPointer, position);
            lastPointer = position;
        }

        void Motion(long now)
        {
            // Normally the timer closed the last burst already. This catches a timer that came too late (the window's
            // thread was blocked for the whole gap); that burst's end and the new origin then include this event.
            if (burstOpen && Ms(now - burstLast) >= gapMs) CloseBurst();
            if (!burstOpen)
            {
                burstOpen = true;
                burstFirst = now;
                burst.Clear();
                burstOrigin = idleCursor;
                rawEnd = PointToClient(burstOrigin);
                if (rawGraphics != null && Drawable(rawEnd))
                {
                    float r = (float)(4 * scale);
                    rawGraphics.FillEllipse(startBrush, rawEnd.X - r, rawEnd.Y - r, 2 * r, 2 * r);
                }
            }
            burstLast = now;
            dirty = true;
        }

        void CloseBurst()
        {
            if (!burstOpen) return;
            burstOpen = false;
            // At least -GapMs without motion: the cursor rests where the burst left it.
            Point end = CursorNow();
            idleCursor = end;
            burstNumber++;
            string line = "#" + burstNumber + "  " + BurstSummary(Ms(burstLast - burstFirst), burst, end);
            bursts.Add(line);
            if (bursts.Count > KeptBursts) bursts.RemoveAt(0);
            dirty = true;
            Raise(line);
        }

        string BurstSummary(double ms, Counts counts, Point end)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:N3} s | Roh dx {1} dy {2} ({3}) | Zeiger dx {4} dy {5} ({6})",
                ms / 1000.0, Signed(counts.RawDx), Signed(counts.RawDy), counts.RawSources(),
                Signed(end.X - burstOrigin.X), Signed(end.Y - burstOrigin.Y), counts.PointerSources());
        }

        void ResetAll(bool log)
        {
            // An open burst is the way to the button: dropped, not listed.
            burstOpen = false;
            total.Clear();
            burst.Clear();
            bursts.Clear();
            absolutePositions.Clear();
            hasLastPointer = false;
            if (pointerGraphics != null) pointerGraphics.Clear(Color.Transparent);
            if (rawGraphics != null) rawGraphics.Clear(Color.Transparent);
            resetCursor = CursorNow();
            idleCursor = resetCursor;
            windowsMouse = ReadWindowsMouse();
            dirty = true;
            if (log) Raise("-- Reset --");
        }

        void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            if (burstOpen && Ms(now - burstLast) >= gapMs) CloseBurst();
            Point cursor = CursorNow();
            if (!burstOpen) idleCursor = cursor;
            // The pointer totals follow the cursor even outside the window, and an open burst's line runs live.
            if (burstOpen || cursor != paintedCursor) dirty = true;
            paintedCursor = cursor;
            if (!dirty) return;
            dirty = false;
            Invalidate();
        }

        void Raise(string line)
        {
            Action<string> handler = Log;
            if (handler != null) handler(line);
        }

        string StatusText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Seit Reset - Roh orange, Zeiger blau, gruener Punkt = Start eines Bursts");
            text.AppendLine(TallyLine("Roh physisch", total.Physical));
            text.AppendLine(TallyLine("Roh WKI", total.Wki));
            if (total.Foreign.Count > 0) text.AppendLine(TallyLine("Roh fremd", total.Foreign));
            if (total.Absolute > 0)
                text.AppendLine(string.Format("{0,-13}{1} Datensaetze (WKI {2}), nicht summiert", "Roh absolut",
                    total.Absolute, total.AbsoluteWki));
            Point cursor = CursorNow();
            text.AppendLine(string.Format("{0,-13}dx {1,7}  dy {2,7}  ({3})", "Zeiger",
                Signed(cursor.X - resetCursor.X), Signed(cursor.Y - resetCursor.Y), total.PointerSources()));
            text.Append(windowsMouse);
            return text.ToString();
        }

        static string TallyLine(string label, Tally tally)
        {
            return string.Format("{0,-13}dx {1,7}  dy {2,7}  ({3} Datensaetze)", label, Signed(tally.Dx), Signed(tally.Dy), tally.Count);
        }

        string WarningText()
        {
            List<string> lines = new List<string>();
            if (rawError != null) lines.Add(rawError);
            else if (!active) lines.Add("Fenster ist nicht vorne: keine Rohdaten. Titelleiste anklicken.");
            if (!physicalPixels) lines.Add("Zeigerwerte in skalierten Pixeln (kein Per-Monitor-DPI auf diesem Windows).");
            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines.ToArray());
        }

        string BurstText()
        {
            StringBuilder text = new StringBuilder();
            text.Append(string.Format("Bursts seit Reset (Ende nach {0} ms ohne Bewegung)", gapMs));
            int first = Math.Max(0, bursts.Count - ShownBursts);
            for (int i = first; i < bursts.Count; i++)
            {
                text.AppendLine();
                text.Append(bursts[i]);
            }
            text.AppendLine();
            if (burstOpen) text.Append("laeuft  " + BurstSummary(Ms(burstLast - burstFirst), burst, CursorNow()));
            else if (bursts.Count == 0) text.Append("noch keiner - Bewegung ueber dem Fenster machen, dann kurz stillhalten");
            return text.ToString();
        }

        // Draws a text block on a light backdrop; returns its bottom edge. fromBottom: y is the bottom edge.
        int DrawBlock(Graphics g, string text, int x, int y, int width, Color color, bool fromBottom)
        {
            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            Size size = TextRenderer.MeasureText(g, text, font, new Size(Math.Max(1, width), int.MaxValue), flags);
            if (fromBottom) y -= size.Height;
            int pad = Scaled(4);
            g.FillRectangle(backdrop, x - pad, y - pad, size.Width + 2 * pad, size.Height + 2 * pad);
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, Math.Max(1, width), size.Height), color, flags);
            return y + size.Height;
        }

        void DrawSegment(Graphics g, Pen pen, PointF from, PointF to)
        {
            if (g == null || !Drawable(from) || !Drawable(to)) return;
            g.DrawLine(pen, from, to);
        }

        static bool Drawable(PointF p)
        {
            return Math.Abs(p.X) < DrawLimit && Math.Abs(p.Y) < DrawLimit;
        }

        void EnsureLayers()
        {
            Size size = ClientSize;
            if (size.Width < 1 || size.Height < 1) return;
            if (pointerLayer != null && pointerLayer.Size == size) return;
            DisposeLayers();
            pointerLayer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            rawLayer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            pointerGraphics = Graphics.FromImage(pointerLayer);
            rawGraphics = Graphics.FromImage(rawLayer);
            pointerGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            rawGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            pointerGraphics.Clear(Color.Transparent);
            rawGraphics.Clear(Color.Transparent);
            dirty = true;
        }

        void DisposeLayers()
        {
            if (pointerGraphics != null) pointerGraphics.Dispose();
            if (rawGraphics != null) rawGraphics.Dispose();
            if (pointerLayer != null) pointerLayer.Dispose();
            if (rawLayer != null) rawLayer.Dispose();
            pointerGraphics = null;
            rawGraphics = null;
            pointerLayer = null;
            rawLayer = null;
        }

        Point CursorNow()
        {
            POINT p;
            // Fails on a secure desktop (UAC, lock screen): the last known position then.
            if (GetCursorPos(out p)) knownCursor = new Point(p.X, p.Y);
            return knownCursor;
        }

        static string ReadWindowsMouse()
        {
            int[] mouse = new int[3];
            int speed;
            string text;
            if (SpiInts(SPI_GETMOUSE, 0, mouse, 0))
            {
                bool accelerated = mouse[2] != 0;
                text = "Windows: Zeigerbeschleunigung " + (accelerated ? "an" : "aus");
                if (SpiInt(SPI_GETMOUSESPEED, 0, out speed, 0)) text += ", Geschwindigkeit " + speed + "/20";
                text += accelerated
                    ? " -> Wiedergabe Automatisch = Zeiger, solange er sichtbar ist"
                    : " -> Wiedergabe Automatisch = Spiel (roh)";
            }
            else
            {
                text = "Windows: Zeigerbeschleunigung unbekannt";
            }
            return text;
        }

        static string Signed(long value)
        {
            return value > 0 ? "+" + value : value.ToString(CultureInfo.CurrentCulture);
        }

        static double Ms(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        int Scaled(double value)
        {
            return (int)Math.Round(value * scale);
        }
    }
}
'@

if (-not ('WkiMeasureMoves.MoveForm' -as [type])) {
    Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms, System.Drawing
}

Write-Host ("Messfenster oben rechts. Ein Burst endet nach {0} ms ohne Bewegung; Esc oder Schliessen beendet." -f $GapMs) -ForegroundColor Cyan

[System.Windows.Forms.Application]::EnableVisualStyles()
# Before the window exists: its pointer positions are then physical pixels, like the clicker helper's.
$dpiContext = [WkiMeasureMoves.DpiScope]::Enter()
try {
    $form = New-Object WkiMeasureMoves.MoveForm -ArgumentList $GapMs, ($dpiContext -ne [IntPtr]::Zero)
    $form.add_Log({ param($line) Write-Host $line })
    [void]$form.ShowDialog()
    $lines = @($form.BurstLines())
    $form.Dispose()
}
finally {
    [WkiMeasureMoves.DpiScope]::Leave($dpiContext)
}

Write-Host ""
if ($lines.Count -eq 0) {
    Write-Host "Keine Bewegung gemessen (seit dem letzten Reset)." -ForegroundColor Yellow
    exit 1
}

Write-Host "== Bursts seit dem letzten Reset ==" -ForegroundColor Cyan
foreach ($line in $lines) {
    Write-Host $line
}
