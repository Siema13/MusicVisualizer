using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicVisualizerLoopback
{
    internal static class BuiltInPresets
    {
        public static readonly string[] RenderModes =
        {
            "bars",
            "ledSegments",
            "mirror",
            "dots",
            "vuMeter",
            "waveform",
            "stereo"
        };

        public static readonly GradientStop[][] GradientPresets =
        {
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#154320" },
                new GradientStop { Pos = 0.7, Color = "#46e661" },
                new GradientStop { Pos = 1.0, Color = "#46e661" }
            },
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#00e5ff" },
                new GradientStop { Pos = 0.5, Color = "#00ff88" },
                new GradientStop { Pos = 1.0, Color = "#ff1744" }
            },
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#b71c1c" },
                new GradientStop { Pos = 0.45, Color = "#ff6d00" },
                new GradientStop { Pos = 0.8, Color = "#ffb300" },
                new GradientStop { Pos = 1.0, Color = "#fff59d" }
            },
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#0d47a1" },
                new GradientStop { Pos = 0.5, Color = "#00acc1" },
                new GradientStop { Pos = 1.0, Color = "#e1f5fe" }
            },
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#4a0072" },
                new GradientStop { Pos = 0.5, Color = "#e040fb" },
                new GradientStop { Pos = 1.0, Color = "#ffcc00" }
            },
            new[]
            {
                new GradientStop { Pos = 0.0, Color = "#111111" },
                new GradientStop { Pos = 0.5, Color = "#888888" },
                new GradientStop { Pos = 1.0, Color = "#ffffff" }
            }
        };

        public static string NextRenderMode(string? current)
        {
            for (int i = 0; i < RenderModes.Length; i++)
            {
                if (string.Equals(RenderModes[i], current, StringComparison.Ordinal))
                {
                    return RenderModes[(i + 1) % RenderModes.Length];
                }
            }

            return RenderModes[0];
        }

        public static List<GradientStop> NextGradientPreset(IReadOnlyList<GradientStop>? current)
        {
            int matchedIndex = -1;
            if (current != null)
            {
                for (int p = 0; p < GradientPresets.Length; p++)
                {
                    if (MatchesPreset(current, GradientPresets[p]))
                    {
                        matchedIndex = p;
                        break;
                    }
                }
            }

            int nextIdx = (matchedIndex + 1) % GradientPresets.Length;
            var preset = GradientPresets[nextIdx];
            var result = new List<GradientStop>(preset.Length);
            for (int i = 0; i < preset.Length; i++)
            {
                result.Add(preset[i].Clone());
            }
            return result;
        }

        private static bool MatchesPreset(IReadOnlyList<GradientStop> a, GradientStop[] b)
        {
            if (a.Count != b.Length)
            {
                return false;
            }

            for (int i = 0; i < b.Length; i++)
            {
                if (!string.Equals(a[i].Color, b[i].Color, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (Math.Abs(a[i].Pos - b[i].Pos) > 0.015)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static class SettingsNormalizer
    {
        public static void Normalize(VisualizerSettings settings)
        {
            settings.CaptureDeviceId = string.IsNullOrWhiteSpace(settings.CaptureDeviceId) ? "default" : settings.CaptureDeviceId;
            settings.FreqPreset = NormalizePreset(settings.FreqPreset);
            settings.FreqMin = Math.Clamp(settings.FreqMin, 20, 20000);
            settings.FreqMax = Math.Clamp(settings.FreqMax, 20, 20000);

            if (settings.FreqMax < settings.FreqMin)
            {
                (settings.FreqMin, settings.FreqMax) = (settings.FreqMax, settings.FreqMin);
            }

            settings.SpanCount = Math.Clamp(settings.SpanCount, 1, 6);
            settings.SpanIndex = Math.Clamp(settings.SpanIndex, 1, settings.SpanCount);

            settings.BarCount = Math.Clamp(settings.BarCount, 2, 32);

            settings.GainDb = Math.Clamp(settings.GainDb, -18.0, 30.0);
            settings.MinDb = Math.Clamp(settings.MinDb, -72.0, -12.0);
            settings.SlopeDbPerOct = Math.Clamp(settings.SlopeDbPerOct, 0.0, 8.0);
            settings.Gamma = Math.Clamp(settings.Gamma, 0.3, 4.0);
            settings.NoiseGateDb = Math.Clamp(settings.NoiseGateDb, -90.0, -20.0);

            settings.AttackSpeed = Math.Clamp(settings.AttackSpeed, 0.01, 1.0);
            settings.ReleaseSpeed = Math.Clamp(settings.ReleaseSpeed, 0.01, 1.0);

            settings.Smoothing = Math.Clamp(settings.Smoothing, 0.0, 0.85);
            settings.PeakFallSpeed = Math.Clamp(settings.PeakFallSpeed, 0.005, 0.25);
            settings.PeakThickness = Math.Clamp(settings.PeakThickness, 1, 8);
            settings.TopRadius = Math.Clamp(settings.TopRadius, 0, 12);
            settings.BgColor = NormalizeHex(settings.BgColor, "#08150e");

            settings.RenderMode = NormalizeRenderMode(settings.RenderMode);
            settings.LongPressAction = NormalizeLongPressAction(settings.LongPressAction);
            settings.DotSpacing = Math.Clamp(settings.DotSpacing, 2, 10);
            settings.AutoPauseTimeoutSec = Math.Clamp(settings.AutoPauseTimeoutSec, 5, 120);

            settings.LedSegmentMode = settings.LedSegmentMode == "fixedSize" ? "fixedSize" : "fixedCount";
            settings.LedSegmentCount = Math.Clamp(settings.LedSegmentCount, 4, 24);
            settings.LedSegmentSizePx = Math.Clamp(settings.LedSegmentSizePx, 4, 16);
            settings.LedSegmentGapPx = Math.Clamp(settings.LedSegmentGapPx, 1, 4);
            settings.LedLowColor = NormalizeHex(settings.LedLowColor, "#46e661");
            settings.LedMidColor = NormalizeHex(settings.LedMidColor, "#46e661");
            settings.LedHighColor = NormalizeHex(settings.LedHighColor, "#154320");
            settings.LedLowThreshold = Math.Clamp(settings.LedLowThreshold, 0.0, 1.0);
            settings.LedHighThreshold = Math.Clamp(settings.LedHighThreshold, settings.LedLowThreshold, 1.0);

            if (settings.GradientStops == null || settings.GradientStops.Count < 2)
            {
                settings.GradientStops = VisualizerSettings.CreateDefault().GradientStops;
            }

            settings.GradientStops = settings.GradientStops
                .Select(s => new GradientStop
                {
                    Pos = Math.Clamp(s.Pos, 0.0, 1.0),
                    Color = NormalizeHex(s.Color, "#46e661")
                })
                .OrderBy(s => s.Pos)
                .Take(6)
                .ToList();

            if (settings.GradientStops.Count < 2)
            {
                settings.GradientStops = VisualizerSettings.CreateDefault().GradientStops;
            }

            settings.GradientStops[0].Pos = 0.0;
            settings.GradientStops[^1].Pos = 1.0;
        }

        private static string NormalizePreset(string? preset)
        {
            return preset?.ToLowerInvariant() switch
            {
                "low" => "low",
                "mid" => "mid",
                "high" => "high",
                "full" => "full",
                "custom" => "custom",
                _ => "full"
            };
        }

        private static string NormalizeRenderMode(string? mode)
        {
            return mode switch
            {
                "bars" => "bars",
                "vuMeter" => "vuMeter",
                "mirror" => "mirror",
                "dots" => "dots",
                "ledSegments" => "ledSegments",
                "waveform" => "waveform",
                "stereo" => "stereo",
                _ => "bars"
            };
        }

        private static string NormalizeLongPressAction(string? action)
        {
            return action switch
            {
                "cycleRenderMode" => "cycleRenderMode",
                "cycleGradientPreset" => "cycleGradientPreset",
                "togglePause" => "togglePause",
                "none" => "none",
                _ => "cycleRenderMode"
            };
        }

        private static string NormalizeHex(string? hex, string fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }

            string value = hex.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal))
            {
                value = "#" + value;
            }

            if (value.Length == 7 && int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return value.ToLowerInvariant();
            }

            return fallback;
        }
    }

    internal static class JsonHelper
    {
        public static string? GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
    }

    internal sealed class VisualizerSettings
    {
        [JsonPropertyName("captureDeviceId")]
        public string CaptureDeviceId { get; set; } = "default";

        [JsonPropertyName("freqPreset")]
        public string FreqPreset { get; set; } = "full";

        [JsonPropertyName("freqMin")]
        public int FreqMin { get; set; } = 20;

        [JsonPropertyName("freqMax")]
        public int FreqMax { get; set; } = 20000;

        [JsonPropertyName("spanCount")]
        public int SpanCount { get; set; } = 1;

        [JsonPropertyName("spanIndex")]
        public int SpanIndex { get; set; } = 1;

        [JsonPropertyName("barCount")]
        public int BarCount { get; set; } = 16;

        [JsonPropertyName("gainDb")]
        public double GainDb { get; set; } = 6.0;

        [JsonPropertyName("minDb")]
        public double MinDb { get; set; } = -48.0;

        [JsonPropertyName("slopeDbPerOct")]
        public double SlopeDbPerOct { get; set; } = 4.0;

        [JsonPropertyName("gamma")]
        public double Gamma { get; set; } = 1.0;

        [JsonPropertyName("noiseGateDb")]
        public double NoiseGateDb { get; set; } = -65.0;

        [JsonPropertyName("attackSpeed")]
        public double AttackSpeed { get; set; } = 0.65;

        [JsonPropertyName("releaseSpeed")]
        public double ReleaseSpeed { get; set; } = 0.25;

        [JsonPropertyName("smoothing")]
        public double Smoothing { get; set; } = 0.22;

        [JsonPropertyName("peakHold")]
        public bool PeakHold { get; set; } = true;

        [JsonPropertyName("peakFallSpeed")]
        public double PeakFallSpeed { get; set; } = 0.08;

        [JsonPropertyName("peakThickness")]
        public int PeakThickness { get; set; } = 2;

        [JsonPropertyName("topRadius")]
        public int TopRadius { get; set; } = 2;

        [JsonPropertyName("bgColor")]
        public string BgColor { get; set; } = "#08150e";

        [JsonPropertyName("gradientStops")]
        public List<GradientStop> GradientStops { get; set; } = new()
        {
            new GradientStop { Pos = 0.0, Color = "#154320" },
            new GradientStop { Pos = 0.7, Color = "#46e661" },
            new GradientStop { Pos = 1.0, Color = "#46e661" }
        };

        [JsonPropertyName("paused")]
        public bool Paused { get; set; } = false;

        [JsonPropertyName("renderMode")]
        public string RenderMode { get; set; } = "bars";

        [JsonPropertyName("longPressAction")]
        public string LongPressAction { get; set; } = "cycleRenderMode";

        [JsonPropertyName("dotSpacing")]
        public int DotSpacing { get; set; } = 3;

        [JsonPropertyName("showNumericMeter")]
        public bool ShowNumericMeter { get; set; } = false;

        [JsonPropertyName("beatPulseEnabled")]
        public bool BeatPulseEnabled { get; set; } = false;

        [JsonPropertyName("idleIconEnabled")]
        public bool IdleIconEnabled { get; set; } = false;

        [JsonPropertyName("autoPauseEnabled")]
        public bool AutoPauseEnabled { get; set; } = false;

        [JsonPropertyName("autoPauseTimeoutSec")]
        public int AutoPauseTimeoutSec { get; set; } = 30;

        [JsonPropertyName("ledSegmentMode")]
        public string LedSegmentMode { get; set; } = "fixedCount";

        [JsonPropertyName("ledSegmentCount")]
        public int LedSegmentCount { get; set; } = 14;

        [JsonPropertyName("ledSegmentSizePx")]
        public int LedSegmentSizePx { get; set; } = 8;

        [JsonPropertyName("ledSegmentGapPx")]
        public int LedSegmentGapPx { get; set; } = 2;

        [JsonPropertyName("ledLowColor")]
        public string LedLowColor { get; set; } = "#46e661";

        [JsonPropertyName("ledMidColor")]
        public string LedMidColor { get; set; } = "#46e661";

        [JsonPropertyName("ledHighColor")]
        public string LedHighColor { get; set; } = "#154320";

        [JsonPropertyName("ledLowThreshold")]
        public double LedLowThreshold { get; set; } = 0.5;

        [JsonPropertyName("ledHighThreshold")]
        public double LedHighThreshold { get; set; } = 0.85;

        public static VisualizerSettings CreateDefault()
        {
            return new VisualizerSettings();
        }

        public VisualizerSettings Clone()
        {
            return new VisualizerSettings
            {
                CaptureDeviceId = CaptureDeviceId,
                FreqPreset = FreqPreset,
                FreqMin = FreqMin,
                FreqMax = FreqMax,
                SpanCount = SpanCount,
                SpanIndex = SpanIndex,
                BarCount = BarCount,
                GainDb = GainDb,
                MinDb = MinDb,
                SlopeDbPerOct = SlopeDbPerOct,
                Gamma = Gamma,
                NoiseGateDb = NoiseGateDb,
                AttackSpeed = AttackSpeed,
                ReleaseSpeed = ReleaseSpeed,
                Smoothing = Smoothing,
                PeakHold = PeakHold,
                PeakFallSpeed = PeakFallSpeed,
                PeakThickness = PeakThickness,
                TopRadius = TopRadius,
                BgColor = BgColor,
                GradientStops = GradientStops.Select(x => x.Clone()).ToList(),
                Paused = Paused,
                RenderMode = RenderMode,
                LongPressAction = LongPressAction,
                DotSpacing = DotSpacing,
                ShowNumericMeter = ShowNumericMeter,
                BeatPulseEnabled = BeatPulseEnabled,
                IdleIconEnabled = IdleIconEnabled,
                AutoPauseEnabled = AutoPauseEnabled,
                AutoPauseTimeoutSec = AutoPauseTimeoutSec,
                LedSegmentMode = LedSegmentMode,
                LedSegmentCount = LedSegmentCount,
                LedSegmentSizePx = LedSegmentSizePx,
                LedSegmentGapPx = LedSegmentGapPx,
                LedLowColor = LedLowColor,
                LedMidColor = LedMidColor,
                LedHighColor = LedHighColor,
                LedLowThreshold = LedLowThreshold,
                LedHighThreshold = LedHighThreshold
            };
        }
    }

    internal sealed class GradientStop
    {
        [JsonPropertyName("pos")]
        public double Pos { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; } = "#46e661";

        public GradientStop Clone()
        {
            return new GradientStop
            {
                Pos = Pos,
                Color = Color
            };
        }
    }
}
