using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MusicVisualizerLoopback
{
    internal struct SparkParticle
    {
        public float X;
        public float Y;
        public float Vy;
        public float Life;
    }

    internal struct StarParticle
    {
        public float X;
        public float Y;
        public float Z;
    }

    internal sealed class ActionRuntime
    {
        private readonly object _settingsLock = new object();
        private VisualizerSettings? _pendingSettings;
        private bool _pendingVisualInvalidation;
        private bool _clearAutoPausedPending;
        private volatile VisualizerSettings _latestSettingsSnapshot;

        public ActionRuntime(string context, VisualizerSettings settings)
        {
            Context = context;
            Settings = settings;
            _latestSettingsSnapshot = settings;
            int count = Math.Clamp(settings.BarCount, 2, 32);
            Bars = new float[count];
            Peaks = new float[count];
            MappedScratch = new float[count];
            SmoothedBands = new float[count];
            Waveform = new float[AudioEngine.WaveformPoints];

            SilenceStopwatch = new Stopwatch();
            KeyPressStopwatch = new Stopwatch();

            string escapedContext = JsonSerializer.Serialize(context);
            SetImagePrefixUtf8 = Encoding.UTF8.GetBytes("{\"event\":\"setImage\",\"context\":" + escapedContext + ",\"payload\":{\"image\":\"data:image/png;base64,");
        }

        public string Context { get; }
        public byte[] SetImagePrefixUtf8 { get; }
        public VisualizerSettings Settings { get; set; }
        public float[] Bars { get; private set; }
        public float[] Peaks { get; private set; }
        public float[] MappedScratch { get; private set; }
        public float[] SmoothedBands { get; private set; }
        public float[] Waveform { get; }
        public float[] WaveformLeft { get; } = new float[AudioEngine.WaveformPoints];
        public float[] WaveformRight { get; } = new float[AudioEngine.WaveformPoints];
        public float StereoLeft { get; set; }
        public float StereoRight { get; set; }
        public float StereoPeakLeft { get; set; }
        public float StereoPeakRight { get; set; }
        public float BeatPulseLevel { get; set; }
        public float BeatBassBaseline { get; set; }
        public float EkgBassBaseline { get; set; }
        public double AgcOffsetDb { get; set; }
        public float EnergyLevel { get; set; }
        public float ClipHoldSeconds { get; set; }
        public int EstimatedBpm { get; set; }
        public long DynamicFrameTick { get; set; }
        public byte[]? WaterfallHistory { get; set; }
        public int WaterfallWidth { get; set; }
        public int WaterfallHeight { get; set; }
        public long WaterfallTick { get; set; }
        public byte[]? FireBuffer { get; set; }
        public int FireWidth { get; set; }
        public int FireHeight { get; set; }
        public float[]? MatrixDrops { get; set; }
        public float[]? MatrixSpeeds { get; set; }
        public float[]? EkgHistory { get; set; }
        public int EkgPulseCountdown { get; set; }
        public byte[]? ScopeTrail { get; set; }
        public int ScopeWidth { get; set; }
        public int ScopeHeight { get; set; }
        public StarParticle[]? Stars { get; set; }
        public SparkParticle[]? Sparks { get; set; }
        public int SparkWriteIndex { get; set; }
        public float LastDtSeconds { get; set; } = 0.033f;
        public uint RngState { get; set; } = 0x13579BDFu;
        public bool IsIdleState { get; set; }
        public long LastSmoothedSnapshotVersion { get; set; } = -1;

        public Stopwatch SilenceStopwatch { get; }
        public volatile bool AutoPaused;
        public volatile bool ManualPauseOverride;
        public Stopwatch KeyPressStopwatch { get; }
        public int LastVisualSignature { get; set; }
        public bool HasSentInitialFrame { get; set; }
        public bool IsVisualActive { get; set; }
        public volatile float LastOverallLevel;
        public long LastPushTicks { get; set; }
        public long LastAnimTicks { get; set; }
        public int SettingsVersion { get; private set; }

        public bool HasPendingUpdates
        {
            get
            {
                lock (_settingsLock)
                {
                    return _pendingSettings != null || _pendingVisualInvalidation || _clearAutoPausedPending;
                }
            }
        }

        // Cached frequency band mapping (rebuilt only when settings or audio format change)
        public BandMapping[]? CachedBands;
        public int CachedBandsSampleRate;
        public int CachedBandsBinCount;
        public int CachedBandsBarCount;
        public int CachedBandsFreqMin;
        public int CachedBandsFreqMax;
        public int CachedBandsSpanCount;
        public int CachedBandsSpanIndex;
        public double CachedBandsSlope;

        // Cached pre-built PNG header (Signature + IHDR + PLTE) for fast 8-bit indexed PNG encoding
        public byte[]? CachedIndexedPngHeader;
        public int CachedPngHeaderWidth;
        public int CachedPngHeaderHeight;
        public string? CachedPngHeaderMode;
        public int CachedPngHeaderSettingsVersion;

        public VisualizerSettings GetSettingsSnapshot()
        {
            return _latestSettingsSnapshot;
        }

        public void QueueSettingsUpdate(VisualizerSettings settings)
        {
            lock (_settingsLock)
            {
                _pendingSettings = settings;
                _latestSettingsSnapshot = settings;
                _pendingVisualInvalidation = true;
            }
        }

        public VisualizerSettings QueueManualPause(bool paused)
        {
            lock (_settingsLock)
            {
                var next = (_pendingSettings ?? _latestSettingsSnapshot).Clone();
                next.Paused = paused;
                _pendingSettings = next;
                _latestSettingsSnapshot = next;
                _clearAutoPausedPending = true;
                _pendingVisualInvalidation = true;
                return next.Clone();
            }
        }

        public VisualizerSettings QueueManualPauseToggle()
        {
            lock (_settingsLock)
            {
                var next = (_pendingSettings ?? _latestSettingsSnapshot).Clone();
                next.Paused = !next.Paused;
                _pendingSettings = next;
                _latestSettingsSnapshot = next;
                _clearAutoPausedPending = true;
                _pendingVisualInvalidation = true;
                return next.Clone();
            }
        }

        public VisualizerSettings QueueLongPressTransform(Action<VisualizerSettings> mutator)
        {
            lock (_settingsLock)
            {
                var next = (_pendingSettings ?? _latestSettingsSnapshot).Clone();
                mutator(next);
                SettingsNormalizer.Normalize(next);
                _pendingSettings = next;
                _latestSettingsSnapshot = next;
                _pendingVisualInvalidation = true;
                return next.Clone();
            }
        }

        public void QueueVisualInvalidation()
        {
            lock (_settingsLock)
            {
                _pendingVisualInvalidation = true;
            }
        }

        public void ApplyPendingUpdatesOnRenderThread()
        {
            VisualizerSettings? nextSettings;
            bool invalidate;
            bool clearAutoPaused;

            lock (_settingsLock)
            {
                nextSettings = _pendingSettings;
                invalidate = _pendingVisualInvalidation;
                clearAutoPaused = _clearAutoPausedPending;
                _pendingSettings = null;
                _pendingVisualInvalidation = false;
                _clearAutoPausedPending = false;
            }

            if (clearAutoPaused)
            {
                AutoPaused = false;
                SilenceStopwatch.Reset();
            }

            if (nextSettings != null)
            {
                Settings = nextSettings;
                EnsureArrays(Math.Clamp(nextSettings.BarCount, 2, 32));
                SettingsVersion++;
                InvalidateVisualCache();
            }
            else if (invalidate)
            {
                InvalidateVisualCache();
            }
        }

        public void InvalidateVisualCache()
        {
            HasSentInitialFrame = false;
            LastVisualSignature = 0;
            CachedIndexedPngHeader = null;
            CachedBands = null;
        }

        public int ComputeVisualSignature(int renderWidth, int renderHeight)
        {
            var settings = Settings;
            var hash = new HashCode();
            hash.Add(SettingsVersion);
            hash.Add(settings.RenderMode);
            hash.Add(settings.Orientation);
            hash.Add(settings.SpanCount);
            hash.Add(settings.SpanIndex);
            hash.Add(settings.BarCount);
            hash.Add(settings.BgColor);
            hash.Add(settings.PeakHold);
            hash.Add(settings.PeakThickness);
            hash.Add(settings.TopRadius);
            hash.Add(settings.DotSpacing);
            if (settings.RenderMode == "waveform")
            {
                hash.Add(settings.WaveBrightness);
                hash.Add(settings.WaveContrast);
                hash.Add(settings.WaveSaturation);
            }
            hash.Add(settings.ShowNumericMeter);
            hash.Add(settings.ShowBpmOverlay);
            hash.Add(settings.BeatPulseEnabled);
            hash.Add(settings.IdleIconEnabled);
            hash.Add(settings.FloorReflectionEnabled);
            hash.Add(settings.PeakSparksEnabled);
            hash.Add(settings.EnergyTintEnabled);
            hash.Add(settings.ScanlinesEnabled);
            hash.Add(settings.ClipLedEnabled);
            hash.Add(settings.Paused);
            hash.Add(IsIdleState);

            if (settings.ShowBpmOverlay)
            {
                hash.Add(EstimatedBpm);
            }

            if (settings.ClipLedEnabled)
            {
                hash.Add(ClipHoldSeconds > 0f);
            }

            if (settings.EnergyTintEnabled)
            {
                hash.Add((int)MathF.Round(EnergyLevel * 10f));
            }

            if (settings.PeakSparksEnabled)
            {
                hash.Add(DynamicFrameTick);
            }

            if (settings.BeatPulseEnabled)
            {
                int pulseLevel = Math.Clamp((int)MathF.Round(BeatPulseLevel * 7f), 0, 7);
                hash.Add(pulseLevel);
            }

            if (settings.RenderMode == "ledSegments")
            {
                hash.Add(settings.LedSegmentMode);
                hash.Add(settings.LedSegmentCount);
                hash.Add(settings.LedSegmentSizePx);
                hash.Add(settings.LedSegmentGapPx);
                hash.Add(settings.LedLowColor);
                hash.Add(settings.LedMidColor);
                hash.Add(settings.LedHighColor);
                hash.Add(settings.LedLowThreshold);
                hash.Add(settings.LedHighThreshold);
            }
            else
            {
                var stops = settings.GradientStops;
                for (int s = 0; s < stops.Count; s++)
                {
                    hash.Add(stops[s].Pos);
                    hash.Add(stops[s].Color);
                }
            }

            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            int actualBars = Math.Min(barCount, Bars.Length);
            float scale = renderHeight / 144f;
            double sum = 0.0;

            if (settings.RenderMode == "waterfall")
            {
                hash.Add(WaterfallTick);
                sum = LastOverallLevel * actualBars;
            }
            else if (settings.RenderMode == "matrix" ||
                     settings.RenderMode == "fire" ||
                     settings.RenderMode == "ekg" ||
                     settings.RenderMode == "vectorscope" ||
                     settings.RenderMode == "starfield")
            {
                hash.Add(DynamicFrameTick);
                sum = LastOverallLevel * actualBars;
            }
            else if (settings.RenderMode == "waveform")
            {
                float midY = renderHeight * 0.5f;
                int step = Math.Max(1, Waveform.Length / Math.Max(1, renderWidth));
                for (int i = 0; i < Waveform.Length; i += step)
                {
                    int py = (int)MathF.Round(midY - Waveform[i] * midY * 0.85f);
                    hash.Add(py);
                }
                sum = LastOverallLevel * actualBars;
            }
            else if (settings.RenderMode == "stereo")
            {
                hash.Add((int)MathF.Round(Math.Clamp(StereoLeft, 0f, 1f) * renderHeight));
                hash.Add((int)MathF.Round(Math.Clamp(StereoRight, 0f, 1f) * renderHeight));
                if (settings.PeakHold)
                {
                    hash.Add((int)MathF.Round(Math.Clamp(StereoPeakLeft, 0f, 1f) * renderHeight));
                    hash.Add((int)MathF.Round(Math.Clamp(StereoPeakRight, 0f, 1f) * renderHeight));
                }
                sum = ((StereoLeft + StereoRight) * 0.5) * actualBars;
            }
            else if (settings.RenderMode == "vuMeter")
            {
                for (int i = 0; i < actualBars; i++)
                {
                    sum += Math.Clamp(Bars[i], 0f, 1f);
                }
                double avg = actualBars > 0 ? sum / actualBars : 0.0;
                hash.Add((int)Math.Round(avg * 280.0));
            }
            else if (settings.RenderMode == "ledSegments")
            {
                int gapPx = Math.Max(1, (int)MathF.Round(Math.Clamp(settings.LedSegmentGapPx, 1, 4) * scale));
                int totalSegments;
                if (settings.LedSegmentMode == "fixedSize")
                {
                    int segmentSizePx = Math.Max(2, (int)MathF.Round(Math.Clamp(settings.LedSegmentSizePx, 4, 16) * scale));
                    totalSegments = Math.Max(1, (int)(renderHeight / (float)(segmentSizePx + gapPx)));
                }
                else
                {
                    totalSegments = Math.Clamp(settings.LedSegmentCount, 4, 24);
                }

                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    hash.Add((int)Math.Round(b * totalSegments));
                }
            }
            else if (settings.RenderMode == "dots")
            {
                float barWidth = renderWidth / (float)barCount;
                float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
                float drawWidth = Math.Max(2f, barWidth - gap);
                float spacing = Math.Max(1f, Math.Clamp(settings.DotSpacing, 2, 10) * scale);
                float dotSize = Math.Max(2f, drawWidth);
                int maxDots = Math.Max(1, (int)(renderHeight / (dotSize + spacing)));

                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    hash.Add((int)Math.Round(b * maxDots));
                }
            }
            else if (settings.RenderMode == "mirror")
            {
                float centerY = renderHeight * 0.5f;
                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    float halfH = Math.Max(1f, b * centerY);
                    int y0 = Math.Clamp((int)MathF.Round(centerY - halfH), 0, renderHeight - 1);
                    int y1 = Math.Clamp((int)MathF.Round(centerY + halfH), y0 + 1, renderHeight);
                    hash.Add(y0);
                    hash.Add(y1);
                }
            }
            else
            {
                int minPeakY = Math.Max(1, (int)MathF.Round(2f * scale));
                for (int i = 0; i < actualBars; i++)
                {
                    float b = Math.Clamp(Bars[i], 0f, 1f);
                    sum += b;
                    int barH = Math.Clamp((int)MathF.Round(Math.Max(2f * scale, b * renderHeight)), 1, renderHeight);
                    hash.Add(barH);

                    if (settings.PeakHold && i < Peaks.Length)
                    {
                        float p = Math.Clamp(Peaks[i], 0f, 1f);
                        int peakY = Math.Clamp((int)MathF.Round(renderHeight - p * renderHeight), minPeakY, renderHeight - 1);
                        int peakColorIdx = Math.Clamp((int)MathF.Round((1f - p) * (renderHeight - 1)), 0, renderHeight - 1);
                        hash.Add(peakY);
                        hash.Add(peakColorIdx);
                    }
                }
            }

            if (settings.ShowNumericMeter)
            {
                double mean = actualBars > 0 ? sum / actualBars : 0.0;
                double safe = Math.Max(mean, 0.0001);
                int dbRounded = (int)Math.Round(Math.Clamp(20.0 * Math.Log10(safe), -60.0, 0.0));
                hash.Add(dbRounded);
            }

            return hash.ToHashCode();
        }

        public void EnsureArrays(int barCount)
        {
            if (Bars.Length != barCount)
            {
                Bars = new float[barCount];
            }

            if (Peaks.Length != barCount)
            {
                Peaks = new float[barCount];
            }

            if (MappedScratch.Length != barCount)
            {
                MappedScratch = new float[barCount];
            }

            if (SmoothedBands.Length != barCount)
            {
                SmoothedBands = new float[barCount];
            }
        }
    }
}
