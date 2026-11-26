using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace MusicVisualizerLoopback
{
    internal static class Renderer
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] ChunkTypeIhdr = { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        private static readonly byte[] ChunkTypePlte = { (byte)'P', (byte)'L', (byte)'T', (byte)'E' };
        private static readonly byte[] ChunkTypeIdat = { (byte)'I', (byte)'D', (byte)'A', (byte)'T' };
        private static readonly byte[] PngIendChunk = { 0x00, 0x00, 0x00, 0x00, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 };
        private static readonly uint[] CrcTable = BuildCrc32Table();

        // Fixed high palette indices (valid for all render heights <= 238 px; 255 total palette entries)
        private const int PaletteSize = 255;
        private const byte IdxBg = 0;
        private const byte IdxLedLow = 1;
        private const byte IdxLedMid = 2;
        private const byte IdxLedHigh = 3;
        private const byte IdxPulseBase = 240; // 240..247: 8 smooth pulsing background levels
        private const byte IdxBlack = 248;
        private const byte IdxOverlayBg = 249;
        private const byte IdxWhite = 250;
        private const byte IdxVuArc = 251;

        private sealed class RenderScratch : IDisposable
        {
            public readonly MemoryStream Stream;
            public readonly MemoryStream ZlibStreamBuffer;
            public readonly byte[] IndexedScanlines;
            public readonly byte[] RotateBuffer;
            public readonly int Width;
            public readonly int Height;

            public RenderScratch(int width, int height)
            {
                Width = width;
                Height = height;
                Stream = new MemoryStream(4096);
                ZlibStreamBuffer = new MemoryStream(4096);
                IndexedScanlines = new byte[(width + 1) * height];
                RotateBuffer = new byte[(width + 1) * height];
            }

            public void Dispose()
            {
                Stream.Dispose();
                ZlibStreamBuffer.Dispose();
            }
        }

        private static readonly ThreadLocal<RenderScratch?> _scratch = new ThreadLocal<RenderScratch?>(false);

        public static void DisposeThreadScratch()
        {
            if (_scratch.IsValueCreated)
            {
                _scratch.Value?.Dispose();
                _scratch.Value = null;
            }
        }

        private static RenderScratch GetScratch(int width, int height)
        {
            var s = _scratch.Value;
            if (s == null || s.Width != width || s.Height != height)
            {
                s?.Dispose();
                s = new RenderScratch(width, height);
                _scratch.Value = s;
            }
            return s;
        }

        public static ArraySegment<byte> RenderToPngBytes(int width, int height, ActionRuntime runtime)
        {
            var settings = runtime.Settings;
            var scratch = GetScratch(width, height);
            byte[] header = GetOrBuildIndexedPngHeader(runtime, width, height);
            byte[] raw = scratch.IndexedScanlines;
            Array.Clear(raw, 0, raw.Length);

            // Optional Beat Pulse full pulsing background on bass hits
            if (settings.BeatPulseEnabled && !settings.Paused && runtime.BeatPulseLevel > 0.05f)
            {
                RenderBeatPulseBackground(raw, width, height, runtime.BeatPulseLevel);
            }

            // Render selected visualization mode (all 10 modes on zero-GC 8-bit indexed PNG path)
            switch (settings.RenderMode)
            {
                case "ledSegments":
                    RenderIndexedLedSegments(raw, width, height, settings, runtime.Bars);
                    break;
                case "mirror":
                    RenderIndexedMirrorBars(raw, width, height, settings, runtime.Bars);
                    break;
                case "dots":
                    RenderIndexedDots(raw, width, height, settings, runtime.Bars);
                    break;
                case "vuMeter":
                    RenderIndexedVuMeter(raw, width, height, runtime.Bars);
                    break;
                case "waveform":
                    RenderIndexedWaveform(raw, width, height, settings, runtime.Waveform);
                    break;
                case "stereo":
                    RenderIndexedStereo(raw, width, height, settings, runtime.StereoLeft, runtime.StereoRight, runtime.StereoPeakLeft, runtime.StereoPeakRight);
                    break;
                case "area":
                    RenderIndexedArea(raw, width, height, settings, runtime.Bars);
                    break;
                case "radial":
                    RenderIndexedRadial(raw, width, height, settings, runtime.Bars);
                    break;
                case "waterfall":
                    RenderIndexedWaterfall(raw, width, height, runtime);
                    break;
                default:
                    RenderIndexedBars(raw, width, height, settings, runtime.Bars, runtime.Peaks);
                    break;
            }

            // Apply orientation transform (down/left/right) BEFORE text/pause overlays so glyphs stay upright
            if (settings.Orientation != "up")
            {
                ApplyOrientation(raw, scratch.RotateBuffer, width, height, settings.Orientation);
            }

            // Optional Idle / Pause glyph when paused or silent
            if (settings.IdleIconEnabled && (settings.Paused || runtime.IsIdleState))
            {
                RenderIdleOrPauseIcon(raw, width, height, settings.Paused);
            }

            // Optional Numeric dB overlay using built-in crisp 5x7 bitmap pixel font (0 GC)
            if (settings.ShowNumericMeter)
            {
                RenderNumericOverlayIndexed(raw, width, height, runtime.LastOverallLevel);
            }

            return EncodeIndexedPng(header, raw, scratch.ZlibStreamBuffer, scratch.Stream);
        }

        private static byte[] GetOrBuildIndexedPngHeader(ActionRuntime runtime, int width, int height)
        {
            var settings = runtime.Settings;
            byte[]? cached = runtime.CachedIndexedPngHeader;
            if (cached != null &&
                runtime.CachedPngHeaderWidth == width &&
                runtime.CachedPngHeaderHeight == height &&
                runtime.CachedPngHeaderSettingsVersion == runtime.SettingsVersion &&
                string.Equals(runtime.CachedPngHeaderMode, settings.RenderMode, StringComparison.Ordinal))
            {
                return cached;
            }

            Color bg = ParseColor(settings.BgColor);
            Color accent = settings.RenderMode == "ledSegments"
                ? ParseColor(settings.LedLowColor)
                : InterpolateColor(settings.GradientStops, 0.85f);

            byte[] paletteRgb = new byte[PaletteSize * 3];
            WriteRgb(paletteRgb, IdxBg, bg);

            if (settings.RenderMode == "ledSegments")
            {
                WriteRgb(paletteRgb, IdxLedLow, ParseColor(settings.LedLowColor));
                WriteRgb(paletteRgb, IdxLedMid, ParseColor(settings.LedMidColor));
                WriteRgb(paletteRgb, IdxLedHigh, ParseColor(settings.LedHighColor));
            }
            else
            {
                int gradCount = Math.Min(height, 238);
                float denom = Math.Max(1, gradCount - 1);
                for (int y = 0; y < gradCount; y++)
                {
                    float t = 1.0f - (y / denom);
                    Color rowColor = InterpolateColor(settings.GradientStops, t);
                    WriteRgb(paletteRgb, 1 + y, rowColor);
                }
            }

            // 8 smooth pulsing background levels (0% -> 46% blend toward accent)
            for (int p = 0; p < 8; p++)
            {
                float blend = (p / 7f) * 0.46f;
                WriteRgb(paletteRgb, IdxPulseBase + p, Blend(bg, accent, blend));
            }

            WriteRgb(paletteRgb, IdxBlack, Color.Black);
            WriteRgb(paletteRgb, IdxOverlayBg, Blend(bg, Color.Black, 0.65f));
            WriteRgb(paletteRgb, IdxWhite, Color.White);
            WriteRgb(paletteRgb, IdxVuArc, Blend(bg, Color.White, 0.35f));

            // Signature (8) + IHDR chunk (25) + PLTE chunk (12 + paletteRgb.Length)
            byte[] header = new byte[8 + 25 + 12 + paletteRgb.Length];
            int pos = 0;

            Buffer.BlockCopy(PngSignature, 0, header, pos, 8);
            pos += 8;

            Span<byte> ihdrData = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdrData.Slice(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdrData.Slice(4, 4), height);
            ihdrData[8] = 8;  // Bit depth: 8
            ihdrData[9] = 3;  // Color type: 3 (Indexed)
            ihdrData[10] = 0; // Compression: Deflate
            ihdrData[11] = 0; // Filter: Adaptive
            ihdrData[12] = 0; // Interlace: None
            pos = WritePngChunk(header, pos, ChunkTypeIhdr, ihdrData);

            WritePngChunk(header, pos, ChunkTypePlte, paletteRgb);

            runtime.CachedIndexedPngHeader = header;
            runtime.CachedPngHeaderWidth = width;
            runtime.CachedPngHeaderHeight = height;
            runtime.CachedPngHeaderMode = settings.RenderMode;
            runtime.CachedPngHeaderSettingsVersion = runtime.SettingsVersion;
            return header;
        }

        private static byte GradientIndexForRow(int y, int height)
        {
            int maxGrad = Math.Min(height, 238);
            if (height <= 238)
            {
                return (byte)(1 + Math.Clamp(y, 0, maxGrad - 1));
            }
            return (byte)(1 + Math.Clamp((int)((y / (float)Math.Max(1, height - 1)) * (maxGrad - 1)), 0, maxGrad - 1));
        }

        private static byte GradientIndexForNormalized(float t, int height)
        {
            int maxGrad = Math.Min(height, 238);
            int idx = (int)MathF.Round((1f - Math.Clamp(t, 0f, 1f)) * (maxGrad - 1));
            return (byte)(1 + Math.Clamp(idx, 0, maxGrad - 1));
        }

        private static void RenderBeatPulseBackground(byte[] raw, int width, int height, float beatLevel)
        {
            int stride = width + 1;
            float clamped = Math.Clamp(beatLevel, 0f, 1f);
            for (int y = 0; y < height; y++)
            {
                // Subtle bottom-to-top ambient glow: strongest near bottom, gently softer near top
                float verticalFactor = 0.55f + 0.45f * (y / (float)Math.Max(1, height - 1));
                int level = Math.Clamp((int)MathF.Round(clamped * verticalFactor * 7f), 0, 7);
                if (level > 0)
                {
                    raw.AsSpan(y * stride + 1, width).Fill((byte)(IdxPulseBase + level));
                }
            }
        }

        private static void RenderIndexedBars(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars, float[] peaks)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float scale = height / 144f;
            int scaledTopRadius = settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0;
            int scaledPeakThickness = Math.Max(1, (int)MathF.Round(settings.PeakThickness * scale));

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;
                int barH = Math.Clamp((int)MathF.Round(Math.Max(2f * scale, value * height)), 1, height);
                int y0 = height - barH;
                int radius = Math.Min(scaledTopRadius, Math.Min(barW / 2, barH));

                for (int y = y0; y < height; y++)
                {
                    int rx0 = x0;
                    int rx1 = x1;
                    int dy = y - y0;
                    if (radius > 0 && dy < radius)
                    {
                        float cy = radius - dy - 0.5f;
                        float dx = MathF.Sqrt(Math.Max(0f, radius * radius - cy * cy));
                        int inset = Math.Max(0, (int)MathF.Floor(radius - dx));
                        rx0 = Math.Min(x1 - 1, x0 + inset);
                        rx1 = Math.Max(rx0 + 1, x1 - inset);
                    }

                    raw.AsSpan(y * stride + 1 + rx0, rx1 - rx0).Fill(GradientIndexForRow(y, height));
                }

                if (settings.PeakHold)
                {
                    float peakValue = i < peaks.Length ? Math.Clamp(peaks[i], 0f, 1f) : 0f;
                    int peakY = Math.Clamp((int)MathF.Round(height - peakValue * height), Math.Max(1, (int)MathF.Round(2f * scale)), height - 1);
                    int peakEndY = Math.Min(height, peakY + scaledPeakThickness);
                    byte peakColorIdx = GradientIndexForNormalized(peakValue, height);
                    for (int py = peakY; py < peakEndY; py++)
                    {
                        raw.AsSpan(py * stride + 1 + x0, barW).Fill(peakColorIdx);
                    }
                }
            }
        }

        private static void RenderIndexedMirrorBars(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(1f, barWidth - gap);
            float centerY = height * 0.5f;
            float scale = height / 144f;
            int scaledTopRadius = settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0;

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + gap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;
                float halfH = Math.Max(1f, value * centerY);
                int y0 = Math.Clamp((int)MathF.Round(centerY - halfH), 0, height - 1);
                int y1 = Math.Clamp((int)MathF.Round(centerY + halfH), y0 + 1, height);
                int radius = Math.Min(scaledTopRadius, Math.Min(barW / 2, (int)halfH));

                for (int y = y0; y < y1; y++)
                {
                    int rx0 = x0;
                    int rx1 = x1;
                    int dy = y - y0;
                    if (radius > 0 && dy < radius)
                    {
                        float cy = radius - dy - 0.5f;
                        float dx = MathF.Sqrt(Math.Max(0f, radius * radius - cy * cy));
                        int inset = Math.Max(0, (int)MathF.Floor(radius - dx));
                        rx0 = Math.Min(x1 - 1, x0 + inset);
                        rx1 = Math.Max(rx0 + 1, x1 - inset);
                    }

                    raw.AsSpan(y * stride + 1 + rx0, rx1 - rx0).Fill(GradientIndexForRow(y, height));
                }
            }
        }

        private static void RenderIndexedLedSegments(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int stride = width + 1;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float barGap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(2f, barWidth - barGap);
            float scale = height / 144f;

            int gapPx = Math.Max(1, (int)MathF.Round(Math.Clamp(settings.LedSegmentGapPx, 1, 4) * scale));

            int totalSegments;
            float segmentHeight;
            if (settings.LedSegmentMode == "fixedSize")
            {
                int segmentSizePx = Math.Max(2, (int)MathF.Round(Math.Clamp(settings.LedSegmentSizePx, 4, 16) * scale));
                totalSegments = Math.Max(1, (int)(height / (float)(segmentSizePx + gapPx)));
                segmentHeight = segmentSizePx;
            }
            else
            {
                totalSegments = Math.Clamp(settings.LedSegmentCount, 4, 24);
                float totalGap = gapPx * Math.Max(0, totalSegments - 1);
                segmentHeight = Math.Max(2f, (height - totalGap) / totalSegments);
            }

            double lowThreshold = Math.Clamp(settings.LedLowThreshold, 0.0, 1.0);
            double highThreshold = Math.Clamp(settings.LedHighThreshold, lowThreshold, 1.0);

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int litSegments = (int)Math.Round(value * totalSegments);
                int x0 = Math.Clamp((int)MathF.Round(i * barWidth + barGap * 0.5f), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Round(i * barWidth + barGap * 0.5f + drawWidth), x0 + 1, width);
                int barW = x1 - x0;

                for (int s = 0; s < litSegments; s++)
                {
                    float fy = height - (s + 1) * (segmentHeight + gapPx) + gapPx;
                    if (fy < -segmentHeight)
                    {
                        break;
                    }

                    int y0 = Math.Clamp((int)MathF.Round(fy), 0, height - 1);
                    int y1 = Math.Clamp((int)MathF.Round(fy + segmentHeight), y0 + 1, height);

                    double ratio = (s + 1) / (double)totalSegments;
                    byte colorIdx = ratio > highThreshold ? IdxLedHigh : (ratio > lowThreshold ? IdxLedMid : IdxLedLow);

                    bool drawBorder = barW >= 4 && (y1 - y0) >= 5;
                    for (int y = y0; y < y1; y++)
                    {
                        var rowSpan = raw.AsSpan(y * stride + 1 + x0, barW);
                        if (drawBorder)
                        {
                            if (y == y0 || y == y1 - 1)
                            {
                                rowSpan.Fill(IdxBlack);
                            }
                            else
                            {
                                rowSpan.Fill(colorIdx);
                                rowSpan[0] = IdxBlack;
                                rowSpan[barW - 1] = IdxBlack;
                            }
                        }
                        else
                        {
                            rowSpan.Fill(colorIdx);
                        }
                    }
                }
            }
        }

        private static void RenderIndexedDots(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            float barWidth = width / (float)barCount;
            float gap = Math.Max(1f, MathF.Floor(barWidth * 0.14f));
            float drawWidth = Math.Max(2f, barWidth - gap);
            float scale = height / 144f;
            float spacing = Math.Max(1f, Math.Clamp(settings.DotSpacing, 2, 10) * scale);
            float dotSize = Math.Max(2f, drawWidth);
            int maxDots = Math.Max(1, (int)(height / (dotSize + spacing)));
            float radius = dotSize * 0.5f;

            for (int i = 0; i < barCount; i++)
            {
                float value = i < bars.Length ? Math.Clamp(bars[i], 0f, 1f) : 0f;
                int litDots = (int)Math.Round(value * maxDots);
                float cx = i * barWidth + gap * 0.5f + radius;

                for (int d = 0; d < litDots; d++)
                {
                    float y = height - (d + 1) * (dotSize + spacing) + spacing;
                    if (y < -dotSize)
                    {
                        break;
                    }

                    float cy = y + radius;
                    float t = (float)d / Math.Max(1, maxDots - 1);
                    byte colorIdx = GradientIndexForNormalized(t, height);
                    DrawFilledCircle(raw, width, height, cx, cy, radius, colorIdx);
                }
            }
        }

        private static void RenderIndexedVuMeter(byte[] raw, int width, int height, float[] bars)
        {
            float sum = 0f;
            for (int i = 0; i < bars.Length; i++)
            {
                sum += bars[i];
            }
            float value = bars.Length > 0 ? Math.Clamp(sum / bars.Length, 0f, 1f) : 0f;

            float centerX = width * 0.5f;
            float centerY = height * 0.82f;
            float radius = Math.Min(width, height) * 0.62f;

            // Draw VU arc (200 deg to 340 deg)
            for (int deg = 200; deg <= 340; deg += 2)
            {
                double rad = deg * Math.PI / 180.0;
                float px = centerX + (float)(Math.Cos(rad) * radius);
                float py = centerY + (float)(Math.Sin(rad) * radius);
                DrawFilledCircle(raw, width, height, px, py, 1.1f, IdxVuArc);
            }

            // Draw needle
            double angleDeg = 200.0 + value * 140.0;
            double angleRad = angleDeg * Math.PI / 180.0;
            float needleLength = radius * 0.92f;
            float tipX = centerX + (float)(Math.Cos(angleRad) * needleLength);
            float tipY = centerY + (float)(Math.Sin(angleRad) * needleLength);
            byte needleColor = GradientIndexForNormalized(value, height);

            DrawThickLine(raw, width, height, (int)MathF.Round(centerX), (int)MathF.Round(centerY), (int)MathF.Round(tipX), (int)MathF.Round(tipY), 1, needleColor);
            DrawFilledCircle(raw, width, height, centerX, centerY, Math.Max(2.5f, height * 0.045f), needleColor);
        }

        private static void RenderIndexedWaveform(byte[] raw, int width, int height, VisualizerSettings settings, float[] waveform)
        {
            int stride = width + 1;
            int midY = height / 2;

            // Subtle center baseline
            raw.AsSpan(midY * stride + 1, width).Fill(IdxVuArc);

            if (waveform.Length < 2)
            {
                return;
            }

            // Multi-Key Span: slice the waveform continuously across SpanCount buttons
            int spanCount = Math.Clamp(settings.SpanCount, 1, 6);
            int spanIndex = Math.Clamp(settings.SpanIndex, 1, spanCount);
            float sliceStart = ((spanIndex - 1) / (float)spanCount) * (waveform.Length - 1);
            float sliceEnd = (spanIndex / (float)spanCount) * (waveform.Length - 1);
            float sliceSpan = Math.Max(1f, sliceEnd - sliceStart);
            byte fillIdx = (byte)(IdxPulseBase + 2);

            int prevY = SampleWaveY(waveform, sliceStart, midY, height);

            for (int x = 0; x < width; x++)
            {
                float samplePos = sliceStart + (x / (float)Math.Max(1, width - 1)) * sliceSpan;
                int y = SampleWaveY(waveform, samplePos, midY, height);

                // Soft filled area between center baseline and wave curve
                int fillTop = Math.Min(midY, y);
                int fillBot = Math.Max(midY, y);
                for (int fy = fillTop; fy <= fillBot; fy++)
                {
                    raw[fy * stride + 1 + x] = fillIdx;
                }

                // Bright 2-px wave trace on top
                int yMin = Math.Min(prevY, y);
                int yMax = Math.Max(prevY, y);
                byte colorIdx = GradientIndexForRow(yMin, height);
                for (int py = yMin; py <= yMax; py++)
                {
                    raw[py * stride + 1 + x] = colorIdx;
                    if (py + 1 < height)
                    {
                        raw[(py + 1) * stride + 1 + x] = colorIdx;
                    }
                }
                prevY = y;
            }
        }

        private static int SampleWaveY(float[] waveform, float samplePos, int midY, int height)
        {
            int idx0 = Math.Clamp((int)samplePos, 0, waveform.Length - 1);
            int idx1 = Math.Min(waveform.Length - 1, idx0 + 1);
            float frac = samplePos - idx0;
            float sample = waveform[idx0] + (waveform[idx1] - waveform[idx0]) * frac;
            return Math.Clamp(midY - (int)MathF.Round(sample * midY * 0.82f), 1, height - 2);
        }

        private static void RenderIndexedStereo(
            byte[] raw,
            int width,
            int height,
            VisualizerSettings settings,
            float left,
            float right,
            float peakLeft,
            float peakRight)
        {
            int stride = width + 1;
            float scale = height / 144f;
            int margin = Math.Max(4, (int)MathF.Round(10f * scale));
            int peakThick = Math.Max(1, (int)MathF.Round(settings.PeakThickness * scale));
            int spanCount = Math.Clamp(settings.SpanCount, 1, 6);
            int spanIndex = Math.Clamp(settings.SpanIndex, 1, spanCount);
            int labelY = Math.Max(1, height - 9);

            // Multi-Key Stereo: 2 keys => [L | R], 3 keys => [L | M | R]
            if (spanCount == 2 || spanCount == 3)
            {
                int singleW = Math.Max(8, width - margin * 2);
                int radius = Math.Min(settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0, singleW / 2);
                float val;
                float peak;
                char label;

                if (spanIndex == 1)
                {
                    val = left;
                    peak = peakLeft;
                    label = 'L';
                }
                else if (spanCount == 3 && spanIndex == 2)
                {
                    val = (left + right) * 0.5f;
                    peak = (peakLeft + peakRight) * 0.5f;
                    label = 'M';
                }
                else
                {
                    val = right;
                    peak = peakRight;
                    label = 'R';
                }

                DrawSingleStereoChannel(raw, stride, height, margin, singleW, val, peak, radius, settings.PeakHold, peakThick, scale);
                DrawChar5x7(raw, width, height, margin + Math.Max(0, (singleW - 5) / 2), labelY, label, IdxWhite);
                return;
            }

            int centerGap = Math.Max(4, (int)MathF.Round(10f * scale));
            int barW = Math.Max(4, (width - margin * 2 - centerGap) / 2);
            int leftX0 = margin;
            int rightX0 = width - margin - barW;
            int dualRadius = Math.Min(settings.TopRadius > 0 ? Math.Max(1, (int)MathF.Round(settings.TopRadius * scale)) : 0, barW / 2);

            DrawSingleStereoChannel(raw, stride, height, leftX0, barW, left, peakLeft, dualRadius, settings.PeakHold, peakThick, scale);
            DrawSingleStereoChannel(raw, stride, height, rightX0, barW, right, peakRight, dualRadius, settings.PeakHold, peakThick, scale);

            DrawChar5x7(raw, width, height, leftX0 + Math.Max(0, (barW - 5) / 2), labelY, 'L', IdxWhite);
            DrawChar5x7(raw, width, height, rightX0 + Math.Max(0, (barW - 5) / 2), labelY, 'R', IdxWhite);
        }

        private static void RenderIndexedArea(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int stride = width + 1;
            int barCount = Math.Min(Math.Clamp(settings.BarCount, 2, 32), bars.Length);
            if (barCount < 2) return;

            byte fillIdx = (byte)(IdxPulseBase + 3);
            int prevY = height - 1 - Math.Clamp((int)MathF.Round(Math.Clamp(bars[0], 0f, 1f) * (height - 2)), 0, height - 2);

            for (int x = 0; x < width; x++)
            {
                float pos = (x / (float)Math.Max(1, width - 1)) * (barCount - 1);
                int i0 = Math.Clamp((int)pos, 0, barCount - 1);
                int i1 = Math.Min(barCount - 1, i0 + 1);
                float frac = pos - i0;
                float val = Math.Clamp(bars[i0] + (bars[i1] - bars[i0]) * frac, 0f, 1f);
                int y = height - 1 - Math.Clamp((int)MathF.Round(val * (height - 2)), 0, height - 2);

                // Vertical gradient fill from curve down to bottom
                for (int fy = y; fy < height; fy++)
                {
                    raw[fy * stride + 1 + x] = (fy - y) < 3 ? GradientIndexForRow(fy, height) : fillIdx;
                }

                // Smooth bright top ridge connecting adjacent pixels
                int yMin = Math.Min(prevY, y);
                int yMax = Math.Max(prevY, y);
                byte ridgeColor = GradientIndexForRow(yMin, height);
                for (int ry = yMin; ry <= yMax; ry++)
                {
                    raw[ry * stride + 1 + x] = ridgeColor;
                }
                prevY = y;
            }
        }

        private static void RenderIndexedRadial(byte[] raw, int width, int height, VisualizerSettings settings, float[] bars)
        {
            int barCount = Math.Min(Math.Clamp(settings.BarCount, 2, 32), bars.Length);
            if (barCount == 0) return;

            float cx = width * 0.5f;
            float cy = height * 0.5f;
            float maxR = Math.Min(width, height) * 0.47f;
            float innerR = maxR * 0.34f;
            float spanR = maxR - innerR;

            // Subtle inner guide ring
            for (int deg = 0; deg < 360; deg += 4)
            {
                double rad = deg * Math.PI / 180.0;
                int px = Math.Clamp((int)MathF.Round(cx + (float)(Math.Cos(rad) * innerR)), 0, width - 1);
                int py = Math.Clamp((int)MathF.Round(cy + (float)(Math.Sin(rad) * innerR)), 0, height - 1);
                raw[py * (width + 1) + 1 + px] = IdxVuArc;
            }

            int lineRadius = barCount <= 16 ? 1 : 0;
            for (int i = 0; i < barCount; i++)
            {
                float val = Math.Clamp(bars[i], 0f, 1f);
                double angle = (i / (double)barCount) * Math.PI * 2.0 - Math.PI * 0.5;
                float cos = (float)Math.Cos(angle);
                float sin = (float)Math.Sin(angle);
                float rEnd = innerR + Math.Max(1.5f, val * spanR);

                int x0 = (int)MathF.Round(cx + cos * innerR);
                int y0 = (int)MathF.Round(cy + sin * innerR);
                int x1 = (int)MathF.Round(cx + cos * rEnd);
                int y1 = (int)MathF.Round(cy + sin * rEnd);

                byte colorIdx = GradientIndexForNormalized(val, height);
                DrawThickLine(raw, width, height, x0, y0, x1, y1, lineRadius, colorIdx);
            }
        }

        private static void RenderIndexedWaterfall(byte[] raw, int width, int height, ActionRuntime runtime)
        {
            if (runtime.WaterfallHistory == null || runtime.WaterfallWidth != width || runtime.WaterfallHeight != height)
            {
                runtime.WaterfallHistory = new byte[width * height];
                runtime.WaterfallWidth = width;
                runtime.WaterfallHeight = height;
            }

            byte[] hist = runtime.WaterfallHistory;
            // Scroll history down by 1 row (zero allocation)
            if (height > 1)
            {
                Buffer.BlockCopy(hist, 0, hist, width, width * (height - 1));
            }

            // Write newest spectrum slice into row 0
            float[] bars = runtime.Bars;
            int barCount = Math.Min(Math.Clamp(runtime.Settings.BarCount, 2, 32), bars.Length);
            for (int x = 0; x < width; x++)
            {
                float val = 0f;
                if (barCount > 1)
                {
                    float pos = (x / (float)Math.Max(1, width - 1)) * (barCount - 1);
                    int i0 = Math.Clamp((int)pos, 0, barCount - 1);
                    int i1 = Math.Min(barCount - 1, i0 + 1);
                    float frac = pos - i0;
                    val = Math.Clamp(bars[i0] + (bars[i1] - bars[i0]) * frac, 0f, 1f);
                }

                byte pixelIdx;
                if (val <= 0.02f)
                {
                    pixelIdx = IdxBg;
                }
                else if (val < 0.25f)
                {
                    int pLevel = Math.Clamp((int)MathF.Round((val / 0.25f) * 7f), 1, 7);
                    pixelIdx = (byte)(IdxPulseBase + pLevel);
                }
                else
                {
                    pixelIdx = GradientIndexForNormalized(val, height);
                }

                hist[x] = pixelIdx;
            }

            int stride = width + 1;
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(hist, y * width, raw, y * stride + 1, width);
            }
        }

        private static void ApplyOrientation(byte[] raw, byte[] tmp, int width, int height, string orientation)
        {
            int stride = width + 1;
            Buffer.BlockCopy(raw, 0, tmp, 0, raw.Length);

            if (orientation == "down")
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(tmp, (height - 1 - y) * stride + 1, raw, y * stride + 1, width);
                }
            }
            else if (orientation == "right" && width == height)
            {
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * stride + 1;
                    int dstCol = height - 1 - y;
                    for (int x = 0; x < width; x++)
                    {
                        raw[x * stride + 1 + dstCol] = tmp[srcRow + x];
                    }
                }
            }
            else if (orientation == "left" && width == height)
            {
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * stride + 1;
                    for (int x = 0; x < width; x++)
                    {
                        raw[(width - 1 - x) * stride + 1 + y] = tmp[srcRow + x];
                    }
                }
            }
        }

        private static void DrawSingleStereoChannel(
            byte[] raw,
            int stride,
            int height,
            int x0,
            int barW,
            float value,
            float peakValue,
            int radius,
            bool peakHold,
            int peakThick,
            float scale)
        {
            int x1 = x0 + barW;
            int barH = Math.Clamp((int)MathF.Round(Math.Max(2f * scale, Math.Clamp(value, 0f, 1f) * height)), 1, height);
            int y0 = height - barH;
            int r = Math.Min(radius, barH);

            for (int y = y0; y < height; y++)
            {
                int rx0 = x0;
                int rx1 = x1;
                int dy = y - y0;
                if (r > 0 && dy < r)
                {
                    float cy = r - dy - 0.5f;
                    float dx = MathF.Sqrt(Math.Max(0f, r * r - cy * cy));
                    int inset = Math.Max(0, (int)MathF.Floor(r - dx));
                    rx0 = Math.Min(x1 - 1, x0 + inset);
                    rx1 = Math.Max(rx0 + 1, x1 - inset);
                }

                raw.AsSpan(y * stride + 1 + rx0, rx1 - rx0).Fill(GradientIndexForRow(y, height));
            }

            if (peakHold)
            {
                int peakY = Math.Clamp((int)MathF.Round(height - Math.Clamp(peakValue, 0f, 1f) * height), Math.Max(1, (int)MathF.Round(2f * scale)), height - 1);
                int peakEndY = Math.Min(height, peakY + peakThick);
                byte peakColorIdx = GradientIndexForNormalized(peakValue, height);
                for (int py = peakY; py < peakEndY; py++)
                {
                    raw.AsSpan(py * stride + 1 + x0, barW).Fill(peakColorIdx);
                }
            }
        }

        private static void RenderIdleOrPauseIcon(byte[] raw, int width, int height, bool isPaused)
        {
            int stride = width + 1;
            int cx = width / 2;
            int cy = height / 2;

            if (isPaused)
            {
                // Crisp centered Pause icon (||) inside subtle dark badge
                int badgeW = Math.Max(22, width / 3);
                int badgeH = Math.Max(22, height / 3);
                int bx0 = Math.Max(0, cx - badgeW / 2);
                int by0 = Math.Max(0, cy - badgeH / 2);
                for (int y = by0; y < Math.Min(height, by0 + badgeH); y++)
                {
                    raw.AsSpan(y * stride + 1 + bx0, Math.Min(badgeW, width - bx0)).Fill(IdxOverlayBg);
                }

                int barW = Math.Max(3, badgeW / 5);
                int barH = Math.Max(10, badgeH * 3 / 5);
                int gap = Math.Max(3, barW);
                int leftX = cx - gap / 2 - barW;
                int rightX = cx + gap / 2;
                int topY = cy - barH / 2;
                for (int y = topY; y < Math.Min(height, topY + barH); y++)
                {
                    raw.AsSpan(y * stride + 1 + leftX, barW).Fill(IdxWhite);
                    raw.AsSpan(y * stride + 1 + rightX, barW).Fill(IdxWhite);
                }
            }
            else
            {
                // Minimal calm idle icon: three small dimmed dots in center
                int dotR = Math.Max(2, width / 28);
                int step = dotR * 4;
                DrawFilledCircle(raw, width, height, cx - step, cy, dotR, IdxVuArc);
                DrawFilledCircle(raw, width, height, cx, cy, dotR, IdxVuArc);
                DrawFilledCircle(raw, width, height, cx + step, cy, dotR, IdxVuArc);
            }
        }

        private static void RenderNumericOverlayIndexed(byte[] raw, int width, int height, float overallLevel)
        {
            double safe = Math.Max(overallLevel, 0.0001);
            int db = (int)Math.Round(Math.Clamp(20.0 * Math.Log10(safe), -60.0, 0.0));

            Span<char> chars = stackalloc char[8];
            int len = 0;
            if (db < 0)
            {
                chars[len++] = '-';
                db = -db;
            }
            if (db >= 10)
            {
                chars[len++] = (char)('0' + (db / 10));
            }
            chars[len++] = (char)('0' + (db % 10));
            chars[len++] = 'd';
            chars[len++] = 'B';

            int textW = len * 6 - 1;
            int boxW = Math.Min(width - 2, textW + 4);
            int boxH = Math.Min(height - 2, 11);
            int stride = width + 1;

            for (int y = 2; y < 2 + boxH; y++)
            {
                raw.AsSpan(y * stride + 1 + 2, boxW).Fill(IdxOverlayBg);
            }

            int x = 4;
            for (int i = 0; i < len; i++)
            {
                DrawChar5x7(raw, width, height, x, 4, chars[i], IdxWhite);
                x += 6;
            }
        }

        private static void DrawFilledCircle(byte[] raw, int width, int height, float cx, float cy, float radius, byte colorIdx)
        {
            int stride = width + 1;
            int y0 = Math.Clamp((int)MathF.Floor(cy - radius), 0, height - 1);
            int y1 = Math.Clamp((int)MathF.Ceiling(cy + radius), 0, height - 1);
            float r2 = radius * radius;

            for (int y = y0; y <= y1; y++)
            {
                float dy = (y + 0.5f) - cy;
                float rem = r2 - dy * dy;
                if (rem < 0f)
                {
                    continue;
                }

                float dx = MathF.Sqrt(rem);
                int x0 = Math.Clamp((int)MathF.Floor(cx - dx), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Ceiling(cx + dx), x0 + 1, width);
                raw.AsSpan(y * stride + 1 + x0, x1 - x0).Fill(colorIdx);
            }
        }

        private static void DrawThickLine(byte[] raw, int width, int height, int x0, int y0, int x1, int y1, int radius, byte colorIdx)
        {
            int stride = width + 1;
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                for (int oy = -radius; oy <= radius; oy++)
                {
                    int py = y0 + oy;
                    if (py < 0 || py >= height) continue;
                    int px0 = Math.Clamp(x0 - radius, 0, width - 1);
                    int px1 = Math.Clamp(x0 + radius + 1, px0 + 1, width);
                    raw.AsSpan(py * stride + 1 + px0, px1 - px0).Fill(colorIdx);
                }

                if (x0 == x1 && y0 == y1)
                {
                    break;
                }

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private static void DrawChar5x7(byte[] raw, int width, int height, int x0, int y0, char c, byte colorIdx)
        {
            ulong bitmap = c switch
            {
                '0' => 0b01110_10001_10011_10101_11001_10001_01110UL,
                '1' => 0b00100_01100_00100_00100_00100_00100_01110UL,
                '2' => 0b01110_10001_00001_00110_01000_10000_11111UL,
                '3' => 0b11110_00001_00001_01110_00001_00001_11110UL,
                '4' => 0b00010_00110_01010_10010_11111_00010_00010UL,
                '5' => 0b11111_10000_11110_00001_00001_10001_01110UL,
                '6' => 0b01110_10000_11110_10001_10001_10001_01110UL,
                '7' => 0b11111_00001_00010_00100_01000_01000_01000UL,
                '8' => 0b01110_10001_10001_01110_10001_10001_01110UL,
                '9' => 0b01110_10001_10001_01111_00001_00010_01100UL,
                '-' => 0b00000_00000_00000_11111_00000_00000_00000UL,
                'd' => 0b00001_00001_01111_10001_10001_10001_01111UL,
                'B' => 0b11110_10001_10001_11110_10001_10001_11110UL,
                'L' => 0b10000_10000_10000_10000_10000_10000_11111UL,
                'M' => 0b10001_11011_10101_10101_10001_10001_10001UL,
                'R' => 0b11110_10001_10001_11110_10100_10010_10001UL,
                _ => 0UL
            };

            if (bitmap == 0UL)
            {
                return;
            }

            int stride = width + 1;
            for (int row = 0; row < 7; row++)
            {
                int py = y0 + row;
                if (py < 0 || py >= height) continue;
                int shift = (6 - row) * 5;
                int rowBits = (int)((bitmap >> shift) & 0x1F);
                for (int col = 0; col < 5; col++)
                {
                    if ((rowBits & (1 << (4 - col))) != 0)
                    {
                        int px = x0 + col;
                        if (px >= 0 && px < width)
                        {
                            raw[py * stride + 1 + px] = colorIdx;
                        }
                    }
                }
            }
        }

        private static void WriteRgb(byte[] paletteRgb, int index, Color c)
        {
            int o = index * 3;
            paletteRgb[o] = c.R;
            paletteRgb[o + 1] = c.G;
            paletteRgb[o + 2] = c.B;
        }

        private static int WritePngChunk(byte[] dst, int pos, byte[] chunkType, ReadOnlySpan<byte> data)
        {
            BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(pos, 4), data.Length);
            pos += 4;
            Buffer.BlockCopy(chunkType, 0, dst, pos, 4);
            pos += 4;
            data.CopyTo(dst.AsSpan(pos, data.Length));
            pos += data.Length;

            uint crc = UpdateCrc(0xFFFFFFFFu, chunkType);
            crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(pos, 4), crc);
            pos += 4;
            return pos;
        }

        private static ArraySegment<byte> EncodeIndexedPng(byte[] header, byte[] rawScanlines, MemoryStream zlibBuffer, MemoryStream pngStream)
        {
            zlibBuffer.SetLength(0);
            using (var z = new ZLibStream(zlibBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(rawScanlines, 0, rawScanlines.Length);
            }

            int idatLen = (int)zlibBuffer.Length;
            byte[] idatRaw = zlibBuffer.GetBuffer();
            ReadOnlySpan<byte> idatSpan = idatRaw.AsSpan(0, idatLen);

            pngStream.SetLength(0);
            pngStream.Write(header, 0, header.Length);

            Span<byte> lenAndType = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(lenAndType.Slice(0, 4), idatLen);
            ChunkTypeIdat.CopyTo(lenAndType.Slice(4, 4));
            pngStream.Write(lenAndType);
            pngStream.Write(idatSpan);

            uint crc = UpdateCrc(0xFFFFFFFFu, ChunkTypeIdat);
            crc = UpdateCrc(crc, idatSpan) ^ 0xFFFFFFFFu;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            pngStream.Write(crcBytes);

            pngStream.Write(PngIendChunk, 0, PngIendChunk.Length);
            return new ArraySegment<byte>(pngStream.GetBuffer(), 0, (int)pngStream.Length);
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                }
                table[n] = c;
            }
            return table;
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        private static Color ParseColor(string hex)
        {
            return ColorTranslator.FromHtml(string.IsNullOrWhiteSpace(hex) ? "#000000" : hex);
        }

        private static Color InterpolateColor(IReadOnlyList<GradientStop> stops, float t)
        {
            int count = stops.Count;
            if (count == 0)
            {
                return Color.White;
            }

            GradientStop? bestBefore = null;
            GradientStop? bestAfter = null;
            GradientStop? minStop = null;
            GradientStop? maxStop = null;

            for (int i = 0; i < count; i++)
            {
                var s = stops[i];
                if (minStop == null || s.Pos < minStop.Pos) minStop = s;
                if (maxStop == null || s.Pos > maxStop.Pos) maxStop = s;
                if (s.Pos <= t && (bestBefore == null || s.Pos > bestBefore.Pos)) bestBefore = s;
                if (s.Pos >= t && (bestAfter == null || s.Pos < bestAfter.Pos)) bestAfter = s;
            }

            if (bestBefore == null)
            {
                return ParseColor(minStop!.Color);
            }

            if (bestAfter == null)
            {
                return ParseColor(maxStop!.Color);
            }

            if (bestBefore == bestAfter || Math.Abs(bestAfter.Pos - bestBefore.Pos) < 1e-6)
            {
                return ParseColor(bestBefore.Color);
            }

            float local = (float)((t - bestBefore.Pos) / (bestAfter.Pos - bestBefore.Pos));
            return Blend(ParseColor(bestBefore.Color), ParseColor(bestAfter.Color), local);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(255, r, g, bl);
        }
    }
}
