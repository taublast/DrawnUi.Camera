using System.Text;
using DrawnUi.Draw;
using static System.Net.Mime.MediaTypeNames;

namespace CameraTests.Services
{
    /// <summary>
    /// Manages real-time caption display using a rolling window of timed entries.
    /// Deltas build the current partial line; completed text finalizes it.
    /// Old lines expire after a configurable timeout. Renders to SkiaLabel via TextSpans
    /// with per-span black background for overlay-style captions.
    /// </summary>
    public class RealtimeCaptionsEngine2 : IDisposable
    {
        private const int PartialRenderIntervalMs = 66;
        private static readonly long PartialRenderIntervalTicks = TimeSpan.FromMilliseconds(PartialRenderIntervalMs).Ticks;

        public IList<string> Spans = new List<string>(128);
        private readonly float _fontSize;
        private readonly int _maxLines;
        private readonly double _expirySeconds;
        private readonly List<CaptionLine> _lines = new();
        private readonly StringBuilder _partialText = new();
        private readonly object _sync = new();
        private Timer _expiryTimer;
        private Timer _partialRenderTimer;
        private bool _partialRenderScheduled;
        private long _lastRenderTicks;
        private bool _hasVisibleCaptions;

        public event Action<IList<string>> CaptionsChanged;

        private struct CaptionLine
        {
            public string Text;
            public DateTime ExpiresUtc;
        }

        /// <param name="label">Target SkiaLabel to render captions into (should have transparent background).</param>
        /// <param name="fontSize">Font size for caption text.</param>
        /// <param name="maxLines">Maximum visible caption lines (including partial).</param>
        /// <param name="expirySeconds">Seconds before a finalized line disappears.</param>
        public RealtimeCaptionsEngine2(int maxLines = 3, double expirySeconds = 3.0)
        {
            _maxLines = maxLines;
            _expirySeconds = expirySeconds;
            _lastRenderTicks = DateTime.UtcNow.Ticks;

            _expiryTimer = new Timer(_ => ClearExpiredBatch(), null, Timeout.Infinite, Timeout.Infinite);
            _partialRenderTimer = new Timer(_ => FlushPartialRender(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Append incremental delta text to the current partial line.
        /// </summary>
        public void AppendDelta(string delta)
        {
            lock (_sync)
            {
                _partialText.Append(delta);
                SchedulePartialRenderLocked();
            }
        }

        /// <summary>
        /// Finalize the current utterance with the completed transcript.
        /// Resets partial text and starts a new caption slot.
        /// </summary>
        public void CommitLine(string text)
        {
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var nowUtc = DateTime.UtcNow;
                    _lines.Add(new CaptionLine { Text = text.Trim(), ExpiresUtc = nowUtc.AddSeconds(_expirySeconds) });
                    ScheduleExpiryCheckLocked(nowUtc);
                }
                _partialText.Clear();
                CancelScheduledPartialRenderLocked();
                RenderLocked();
            }
        }

        /// <summary>
        /// Clear all captions immediately.
        /// </summary>
        public void Clear()
        {
            lock (_sync)
            {
                _lines.Clear();
                _partialText.Clear();
                CancelExpiryCheckLocked();
                CancelScheduledPartialRenderLocked();
                RenderLocked();
            }
        }

        private void ClearExpiredBatch()
        {
            lock (_sync)
            {
                if (_lines.Count == 0)
                {
                    CancelExpiryCheckLocked();
                    return;
                }

                var nowUtc = DateTime.UtcNow;
                bool allExpired = true;
                DateTime latestExpiryUtc = _lines[0].ExpiresUtc;

                for (int i = 0; i < _lines.Count; i++)
                {
                    var expiresUtc = _lines[i].ExpiresUtc;
                    if (expiresUtc > nowUtc)
                    {
                        allExpired = false;
                    }

                    if (expiresUtc > latestExpiryUtc)
                    {
                        latestExpiryUtc = expiresUtc;
                    }
                }

                if (allExpired)
                {
                    _lines.Clear();
                    CancelExpiryCheckLocked();
                    RenderLocked();
                    return;
                }

                ScheduleExpiryCheckLocked(nowUtc, latestExpiryUtc);
            }
        }

        private void ScheduleExpiryCheckLocked(DateTime nowUtc, DateTime? latestExpiryUtc = null)
        {
            if (_lines.Count == 0)
            {
                CancelExpiryCheckLocked();
                return;
            }

            var dueAtUtc = latestExpiryUtc ?? _lines.Max(line => line.ExpiresUtc);
            var dueMs = (int)Math.Max(1, (dueAtUtc - nowUtc).TotalMilliseconds);
            _expiryTimer?.Change(dueMs, Timeout.Infinite);
        }

        private void CancelExpiryCheckLocked()
        {
            _expiryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void SchedulePartialRenderLocked()
        {
            var now = DateTime.UtcNow.Ticks;
            var dueAt = _lastRenderTicks + PartialRenderIntervalTicks;
            if (now >= dueAt)
            {
                RenderLocked(now);
                return;
            }

            if (_partialRenderScheduled)
            {
                return;
            }

            var remainingTicks = dueAt - now;
            var dueMs = (int)Math.Max(1, TimeSpan.FromTicks(remainingTicks).TotalMilliseconds);
            _partialRenderScheduled = true;
            _partialRenderTimer?.Change(dueMs, Timeout.Infinite);
        }

        private void CancelScheduledPartialRenderLocked()
        {
            _partialRenderScheduled = false;
            _partialRenderTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void FlushPartialRender()
        {
            lock (_sync)
            {
                if (!_partialRenderScheduled)
                {
                    return;
                }

                _partialRenderScheduled = false;
                RenderLocked();
            }
        }

        /// <summary>
        /// Rebuild label spans from current state. Must be called under _sync lock.
        /// </summary>
        private void RenderLocked(long nowTicks = 0)
        {
            _partialRenderScheduled = false;
            _lastRenderTicks = nowTicks == 0 ? DateTime.UtcNow.Ticks : nowTicks;

            bool hasPartial = _partialText.Length > 0;
            int finalSlots = hasPartial ? Math.Max(0, _maxLines - 1) : _maxLines;
            int skip = Math.Max(0, _lines.Count - finalSlots);

            Spans.Clear();

            for (int i = skip; i < _lines.Count; i++)
            {
                Spans.Add($"{_lines[i].Text}");
            }

            if (hasPartial)
            {
                Spans.Add($"{_partialText}");
            }

            CaptionsChanged?.Invoke(Spans.ToList());
        }

 
        public void Dispose()
        {
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _partialRenderTimer?.Dispose();
            _partialRenderTimer = null;
        }
    }
}
