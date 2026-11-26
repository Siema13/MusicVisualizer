using System;

namespace MusicVisualizerLoopback
{
    internal struct BandMapping
    {
        public int StartBin;
        public int EndBin;
        public float FractionalBin; // >= 0 when sub-bin interpolation is used (e.g. bass 50-300 Hz)
        public double TiltDb;
    }

    internal static class SpectrumMapper
    {
        public static void MapInto(SpectrumSnapshot snapshot, ActionRuntime runtime)
        {
            var settings = runtime.Settings;
            var output = runtime.MappedScratch;
            int barCount = Math.Clamp(settings.BarCount, 2, 32);
            int binCount = snapshot.Magnitudes.Length;
            if (binCount == 0)
            {
                Array.Clear(output, 0, Math.Min(barCount, output.Length));
                return;
            }

            int spanCount = Math.Clamp(settings.SpanCount, 1, 6);
            int spanIndex = Math.Clamp(settings.SpanIndex, 1, spanCount);

            var bands = runtime.CachedBands;
            if (bands == null ||
                bands.Length != barCount ||
                runtime.CachedBandsSampleRate != snapshot.SampleRate ||
                runtime.CachedBandsBinCount != binCount ||
                runtime.CachedBandsBarCount != barCount ||
                runtime.CachedBandsFreqMin != settings.FreqMin ||
                runtime.CachedBandsFreqMax != settings.FreqMax ||
                runtime.CachedBandsSpanCount != spanCount ||
                runtime.CachedBandsSpanIndex != spanIndex ||
                Math.Abs(runtime.CachedBandsSlope - settings.SlopeDbPerOct) > 1e-6)
            {
                bands = BuildBandMappings(
                    snapshot.SampleRate,
                    binCount,
                    barCount,
                    settings.FreqMin,
                    settings.FreqMax,
                    settings.SlopeDbPerOct,
                    spanCount,
                    spanIndex);
                runtime.CachedBands = bands;
                runtime.CachedBandsSampleRate = snapshot.SampleRate;
                runtime.CachedBandsBinCount = binCount;
                runtime.CachedBandsBarCount = barCount;
                runtime.CachedBandsFreqMin = settings.FreqMin;
                runtime.CachedBandsFreqMax = settings.FreqMax;
                runtime.CachedBandsSpanCount = spanCount;
                runtime.CachedBandsSpanIndex = spanIndex;
                runtime.CachedBandsSlope = settings.SlopeDbPerOct;
            }

            double minDb = settings.MinDb;
            double maxDb = 0.0;
            double dbRange = Math.Max(6.0, maxDb - minDb);
            double invDbRange = 1.0 / dbRange;
            double gainDb = settings.GainDb + (settings.AutoGainEnabled ? runtime.AgcOffsetDb : 0.0);
            double noiseGateDb = settings.NoiseGateDb;
            bool applyGamma = Math.Abs(settings.Gamma - 1.0) > 0.01;
            double invGamma = applyGamma ? 1.0 / Math.Max(0.1, settings.Gamma) : 1.0;

            float smoothing = (float)Math.Clamp(settings.Smoothing, 0.0, 0.85);
            float oneMinusSmoothing = 1f - smoothing;
            bool isNewSnapshot = runtime.LastSmoothedSnapshotVersion != snapshot.Version;
            runtime.LastSmoothedSnapshotVersion = snapshot.Version;

            float[] magnitudes = snapshot.Magnitudes;
            float[] smoothedBands = runtime.SmoothedBands;
            double normSum = 0.0;

            for (int i = 0; i < barCount; i++)
            {
                ref readonly var band = ref bands[i];
                double rawEnergy;

                if (band.FractionalBin >= 0f)
                {
                    int b0 = (int)band.FractionalBin;
                    int b1 = Math.Min(binCount - 1, b0 + 1);
                    float frac = band.FractionalBin - b0;
                    float m0 = magnitudes[b0];
                    float m1 = magnitudes[b1];
                    rawEnergy = m0 + (m1 - m0) * frac;
                }
                else
                {
                    int startBin = band.StartBin;
                    int endBin = band.EndBin;
                    double sum = 0.0;
                    double peak = 0.0;
                    int count = endBin - startBin + 1;

                    for (int b = startBin; b <= endBin; b++)
                    {
                        double value = magnitudes[b];
                        sum += value * value;
                        if (value > peak)
                        {
                            peak = value;
                        }
                    }

                    double rms = count > 0 ? Math.Sqrt(sum / count) : 0.0;
                    rawEnergy = rms * 0.70 + peak * 0.30;
                }

                // Independent per-action FFT smoothing (applied once per new FFT snapshot)
                float energy;
                if (isNewSnapshot)
                {
                    float prev = smoothedBands[i];
                    energy = prev + ((float)rawEnergy - prev) * oneMinusSmoothing;
                    if (rawEnergy <= 1e-6 && energy < 1e-5f)
                    {
                        energy = 0f;
                    }
                    smoothedBands[i] = energy;
                }
                else
                {
                    energy = smoothedBands[i];
                }

                double safeEnergy = energy > 1e-6f ? energy : 1e-6;
                double db = 20.0 * Math.Log10(safeEnergy) + gainDb + band.TiltDb;

                if (db < noiseGateDb)
                {
                    output[i] = 0f;
                    continue;
                }

                double norm = (db - minDb) * invDbRange;
                norm = Math.Clamp(norm, 0.0, 1.0);

                if (applyGamma)
                {
                    norm = Math.Pow(norm, invGamma);
                }

                float finalVal = (float)Math.Clamp(norm, 0.0, 1.0);
                output[i] = finalVal;
                normSum += finalVal;
            }

            // Optional Auto-Gain (AGC): gently nudges gain within +-12 dB to keep bars active on quiet/loud tracks
            if (settings.AutoGainEnabled)
            {
                if (isNewSnapshot)
                {
                    double avgNorm = barCount > 0 ? normSum / barCount : 0.0;
                    if (snapshot.HasSignal && avgNorm > 0.015)
                    {
                        double error = 0.48 - avgNorm;
                        runtime.AgcOffsetDb = Math.Clamp(runtime.AgcOffsetDb + error * 0.45, -12.0, 12.0);
                    }
                    else if (!snapshot.HasSignal)
                    {
                        runtime.AgcOffsetDb *= 0.985;
                        if (Math.Abs(runtime.AgcOffsetDb) < 0.05) runtime.AgcOffsetDb = 0.0;
                    }
                }
            }
            else
            {
                runtime.AgcOffsetDb = 0.0;
            }
        }

        public static BandMapping[] BuildBandMappings(
            int sampleRate,
            int binCount,
            int barCount,
            int freqMin,
            int freqMax,
            double slopeDbPerOct,
            int spanCount = 1,
            int spanIndex = 1)
        {
            double nyquist = sampleRate / 2.0;
            int minHz = Math.Clamp(freqMin, 20, Math.Max(20, (int)nyquist));
            int maxHz = Math.Clamp(freqMax, 20, Math.Max(20, (int)nyquist));

            if (maxHz <= minHz)
            {
                maxHz = Math.Min(Math.Max(21, (int)nyquist), minHz + 1);
            }

            spanCount = Math.Clamp(spanCount, 1, 6);
            spanIndex = Math.Clamp(spanIndex, 1, spanCount);

            double fullLogMin = Math.Log10(minHz);
            double fullLogSpan = Math.Log10(maxHz) - fullLogMin;

            int totalBars = barCount * spanCount;
            int barOffset = (spanIndex - 1) * barCount;

            int rangeStartBin = Math.Max(1, (int)Math.Floor((minHz / nyquist) * binCount));
            int rangeEndBin = Math.Min(binCount - 1, (int)Math.Ceiling((maxHz / nyquist) * binCount));

            if (rangeEndBin <= rangeStartBin)
            {
                rangeEndBin = Math.Min(binCount - 1, rangeStartBin + 1);
            }

            var bands = new BandMapping[barCount];

            for (int i = 0; i < barCount; i++)
            {
                double startRatio = (barOffset + i) / (double)totalBars;
                double endRatio = (barOffset + i + 1) / (double)totalBars;

                double bandStartHz = Math.Pow(10.0, fullLogMin + startRatio * fullLogSpan);
                double bandEndHz = Math.Pow(10.0, fullLogMin + endRatio * fullLogSpan);
                double centerHz = Math.Sqrt(bandStartHz * bandEndHz);

                double exactStartBin = (bandStartHz / nyquist) * binCount;
                double exactEndBin = (bandEndHz / nyquist) * binCount;
                double exactCenterBin = Math.Clamp((centerHz / nyquist) * binCount, 1.0, binCount - 1.0001);

                double octavesFrom100Hz = Math.Log2(Math.Max(20.0, centerHz) / 100.0);

                if (exactEndBin - exactStartBin < 1.0)
                {
                    // Sub-bin fractional interpolation for smooth low-frequency / bass separation
                    bands[i] = new BandMapping
                    {
                        StartBin = (int)Math.Floor(exactCenterBin),
                        EndBin = Math.Min(binCount - 1, (int)Math.Ceiling(exactCenterBin)),
                        FractionalBin = (float)exactCenterBin,
                        TiltDb = octavesFrom100Hz * slopeDbPerOct
                    };
                }
                else
                {
                    int startBin = Math.Max(rangeStartBin, (int)Math.Floor(exactStartBin));
                    int endBin = Math.Min(rangeEndBin, (int)Math.Ceiling(exactEndBin));
                    if (endBin <= startBin)
                    {
                        endBin = Math.Min(rangeEndBin, startBin + 1);
                    }

                    bands[i] = new BandMapping
                    {
                        StartBin = startBin,
                        EndBin = endBin,
                        FractionalBin = -1f,
                        TiltDb = octavesFrom100Hz * slopeDbPerOct
                    };
                }
            }

            return bands;
        }
    }
}
