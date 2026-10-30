#requires -Version 5.1
<#
.SYNOPSIS
  Measures the real click rate and timing jitter of an auto clicker (the native WKI AutoClicker or any other).

.DESCRIPTION
  Opens a topmost counting window. Every mouse-button press (left, right, middle, X1, X2; a double click's second
  press counts as a press too) and every key press (including auto-repeats; Esc excluded) that reaches the window is
  stamped with QueryPerformanceCounter. The measurement starts with the first press, so there is time to start the
  clicker after the window opens, and ends after -Seconds or when the window is closed (Esc or the close button).
  Afterwards the console shows per input kind: count, measured span, mean CPS, mean/stddev/min/max/median/p1/p99 of
  the intervals between presses, and with -TargetCps the deviation from the target.

  Timestamps are taken when the window's thread dispatches the message, not when the input was injected, so the
  numbers include the message queue's own jitter (typically well under 1 ms on an idle machine). Keep the window
  in the foreground and the cursor over it; nothing else is measured.

  Live check from the design (only with Phillip present, hands off the mouse): run the clicker at 10/50/100/500 cps
  with the cursor over this window and compare the measured CPS and jitter with the target.

  The script injects no input, installs no hooks and writes no files.

.PARAMETER Seconds   Measurement length after the first press (default 10, 1..3600).
.PARAMETER TargetCps Optional target rate; adds the deviation of the measured CPS and of the mean interval.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\measure-clicks.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\measure-clicks.ps1 -Seconds 20 -TargetCps 100
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 3600)]
    [int]$Seconds = 10,
    [ValidateRange(0.0, 100000.0)]
    [double]$TargetCps = 0
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Compiled by Windows PowerShell 5.1's C# 5 compiler: no string interpolation, no expression-bodied members.
# The window procedure stamps presses itself, so PowerShell's slow event dispatch never delays a timestamp.
$source = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WkiMeasureClicks
{
    public sealed class PressStats
    {
        public string Kind;
        public int Count;
        public double SpanMs;
        public double Cps;
        public double MeanMs;
        public double StdDevMs;
        public double MinMs;
        public double MaxMs;
        public double MedianMs;
        public double P1Ms;
        public double P99Ms;
    }

    public sealed class CounterForm : Form
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203;
        const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONDBLCLK = 0x0206;
        const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONDBLCLK = 0x0209;
        const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONDBLCLK = 0x020D;
        const int VK_ESCAPE = 0x1B;

        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long value);

        readonly long frequency;
        readonly long durationTicks;
        readonly List<long> mouse = new List<long>(1 << 16);
        readonly List<long> keys = new List<long>(1 << 16);
        readonly Font font = new Font(FontFamily.GenericSansSerif, 14f);
        readonly Timer refresh;
        string statusText = "";
        long firstTick = -1;
        bool finished;

        public CounterForm(int seconds)
        {
            QueryPerformanceFrequency(out frequency);
            durationTicks = frequency * seconds;

            Text = "WKI Klick-Messung";
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 400);
            // No child controls: every press must reach this window's own procedure, so the text is painted.
            DoubleBuffered = true;
            ResizeRedraw = true;

            refresh = new Timer();
            refresh.Interval = 100;
            refresh.Tick += delegate { UpdateStatus(); };
            refresh.Start();
            UpdateStatus();
        }

        public List<long> MousePresses { get { return mouse; } }
        public List<long> KeyPresses { get { return keys; } }
        public long Frequency { get { return frequency; } }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK:
                case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK:
                case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK:
                case WM_XBUTTONDOWN: case WM_XBUTTONDBLCLK:
                    Stamp(mouse);
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
                Stamp(keys);
                // Handled here, so no key (Tab, arrows, Alt combos) moves focus or closes anything.
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, statusText, font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            refresh.Stop();
            refresh.Dispose();
            font.Dispose();
            base.OnFormClosed(e);
        }

        void Stamp(List<long> list)
        {
            if (finished) return;
            long now;
            QueryPerformanceCounter(out now);
            if (firstTick < 0) firstTick = now;
            if (now - firstTick > durationTicks)
            {
                Finish();
                return;
            }
            list.Add(now);
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            BeginInvoke(new MethodInvoker(Close));
        }

        void UpdateStatus()
        {
            if (finished) return;
            if (firstTick < 0)
            {
                SetStatus("Bereit. Die Messung startet mit dem ersten Klick oder Tastendruck.\n"
                    + "Cursor ueber diesem Fenster lassen. Esc oder Schliessen beendet.");
                return;
            }
            long now;
            QueryPerformanceCounter(out now);
            double elapsed = (now - firstTick) / (double)frequency;
            double total = durationTicks / (double)frequency;
            if (elapsed >= total)
            {
                Finish();
                return;
            }
            SetStatus(string.Format("Messung laeuft: {0:F1} / {1:F0} s\nMaus: {2}   Tasten: {3}",
                elapsed, total, mouse.Count, keys.Count));
        }

        void SetStatus(string text)
        {
            if (text == statusText) return;
            statusText = text;
            Invalidate();
        }

        // Interval statistics of one press list; null when it holds fewer than two presses.
        public static PressStats Analyze(string kind, List<long> presses, long frequency)
        {
            if (presses.Count < 2) return null;
            int n = presses.Count - 1;
            double[] intervals = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                intervals[i] = (presses[i + 1] - presses[i]) * 1000.0 / frequency;
                sum += intervals[i];
            }
            double mean = sum / n;
            double squares = 0;
            for (int i = 0; i < n; i++)
            {
                double d = intervals[i] - mean;
                squares += d * d;
            }
            Array.Sort(intervals);

            PressStats s = new PressStats();
            s.Kind = kind;
            s.Count = presses.Count;
            s.SpanMs = sum;
            s.Cps = sum > 0 ? n * 1000.0 / sum : 0;
            s.MeanMs = mean;
            s.StdDevMs = n > 1 ? Math.Sqrt(squares / (n - 1)) : 0;
            s.MinMs = intervals[0];
            s.MaxMs = intervals[n - 1];
            s.MedianMs = Percentile(intervals, 0.50);
            s.P1Ms = Percentile(intervals, 0.01);
            s.P99Ms = Percentile(intervals, 0.99);
            return s;
        }

        static double Percentile(double[] sorted, double p)
        {
            int index = (int)Math.Floor(p * sorted.Length);
            if (index > sorted.Length - 1) index = sorted.Length - 1;
            return sorted[index];
        }
    }
}
'@

if (-not ('WkiMeasureClicks.CounterForm' -as [type])) {
    Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms, System.Drawing
}

[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object WkiMeasureClicks.CounterForm($Seconds)
[void]$form.ShowDialog()

$results = @(@(
    [WkiMeasureClicks.CounterForm]::Analyze("Maus", $form.MousePresses, $form.Frequency),
    [WkiMeasureClicks.CounterForm]::Analyze("Tasten", $form.KeyPresses, $form.Frequency)
) | Where-Object { $_ -ne $null })
$form.Dispose()

Write-Host ""
if ($results.Count -eq 0) {
    Write-Host "Zu wenige Eingaben: mindestens zwei Klicks oder Tastendruecke derselben Art noetig." -ForegroundColor Yellow
    exit 1
}

foreach ($s in $results) {
    Write-Host ("== {0} ==" -f $s.Kind) -ForegroundColor Cyan
    Write-Host ("Anzahl          : {0}" -f $s.Count)
    Write-Host ("Zeitspanne      : {0:N3} s (erster bis letzter Druck)" -f ($s.SpanMs / 1000))
    Write-Host ("Mittlere CPS    : {0:N2}" -f $s.Cps)
    Write-Host ("Intervall Mittel: {0:N3} ms" -f $s.MeanMs)
    Write-Host ("Intervall Stdabw: {0:N3} ms ({1:N1} % vom Mittel)" -f $s.StdDevMs, $(if ($s.MeanMs -gt 0) { 100 * $s.StdDevMs / $s.MeanMs } else { 0 }))
    Write-Host ("Intervall Min   : {0:N3} ms" -f $s.MinMs)
    Write-Host ("Intervall Max   : {0:N3} ms" -f $s.MaxMs)
    Write-Host ("Median / p1/p99 : {0:N3} / {1:N3} / {2:N3} ms" -f $s.MedianMs, $s.P1Ms, $s.P99Ms)
    if ($TargetCps -gt 0) {
        $targetMs = 1000 / $TargetCps
        $cpsDev = 100 * ($s.Cps - $TargetCps) / $TargetCps
        $meanDev = 100 * ($s.MeanMs - $targetMs) / $targetMs
        $color = if ([Math]::Abs($cpsDev) -le 5) { "Green" } elseif ([Math]::Abs($cpsDev) -le 15) { "Yellow" } else { "Red" }
        Write-Host ("Ziel            : {0:N2} CPS = {1:N3} ms" -f $TargetCps, $targetMs)
        Write-Host ("Abweichung CPS  : {0:+0.0;-0.0;0.0} %   (Intervall {1:+0.0;-0.0;0.0} %)" -f $cpsDev, $meanDev) -ForegroundColor $color
    }
    Write-Host ""
}
