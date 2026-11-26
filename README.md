# Music Visualizer Loopback (Stream Dock / Stream Deck Plugin)

Lekki, wysokowydajny wizualizator audio (WASAPI Loopback) napisany w **C# / .NET 8** dla urządzeń **AJAZZ Stream Dock** (m.in. AKP153E) oraz kompatybilnych kontrolerów Stream Deck.

## Najważniejsze funkcje

- **Przechwytywanie WASAPI Loopback (NAudio)**: domyślne urządzenie systemowe (z automatycznym śledzeniem zmian urządzenia) lub ręcznie wybrane urządzenie wyjściowe, z pełną obsługą `WaveFormatExtensible` (32-bit IEEE float / PCM16).
- **Płynne 30 FPS na każdym przycisku przy minimalnym zużyciu CPU**:
  - dedykowana pętla renderująca na precyzyjnym timerze systemowym (`CreateWaitableTimerExW` High-Resolution),
  - równomierne taktowanie round-robin (bez burstów zapychających kolejkę hosta) + adaptacyjny `backpressure`,
  - **wszystkie 7 trybów + nakładki na szybkiej ścieżce 8-bitowego indeksowanego PNG** z keszowanym nagłówkiem (`IHDR`/`PLTE`) i bezalokacyjnym kodowaniem UTF-8 Base64 bezpośrednio do bufora ramki WebSocket (~0.12 ms/ramkę, ~700 B/PNG, praktycznie 0 GC),
  - automatyczne dopasowanie rozdzielczości renderu do podłączonego urządzenia (`85×85` dla AKP153E / 293SV3, `100×100` dla 293, `96×96` domyślnie).
- **7 trybów wizualizacji**: `Bars`, `Mirror`, `LED Segments`, `Dots`, `VU Meter`, `Waveform` (oscyloskop), `Stereo` (osobne kanały `L / R` z peak hold).
- **Multi-Key Span**: tryb „Kafelek X z N” (1–6 przycisków), który automatycznie dzieli wybrany zakres częstotliwości (np. `20 Hz – 20 kHz`) na sąsiednie kafelki tworzące jeden szeroki analizator.
- **Poprawiony tor DSP**: interpolacja ułamkowa binów FFT na niskich częstotliwościach (brak sklejania słupków basu `50–300 Hz`) oraz niezależne `Płynność FFT (Smoothing)` dla każdego przycisku.
- **Krótkie i długie przytrzymanie przycisku**:
  - krótkie kliknięcie = `Pause / Resume`,
  - długie przytrzymanie (`≥ 0.6 s`) = konfigurowalna akcja w Property Inspectorze (`Zmień tryb renderu`, `Zmień preset kolorów`, `Pause / Resume` lub `Wyłączone`).
- **Opcjonalne efekty i nakładki (0 GC)**: `Numeric dB` (wbudowana czcionka pikselowa 5×7), `Beat Pulse` (reaktywna ramka na bas 20–120 Hz), `Ikona ciszy / pauzy` (`||`).

## Struktura projektu

- `manifest.json` — manifest wtyczki Stream Dock / Stream Deck (SDK v2)
- `src/` — modularny kod źródłowy w C# (.NET 8 Windows):
  - `Program.cs` — punkt wejścia
  - `PluginApp.cs` — komunikacja WebSocket, pętla renderująca 30 FPS, obsługa klawiszy (`Short`/`LongPress`) i diagnostyka
  - `AudioEngine.cs` — przechwytywanie WASAPI Loopback, FFT, RMS L/R, waveform, wykrywanie basu i ciszy
  - `SpectrumMapper.cs` — mapowanie logarytmiczne, Multi-Key Span, interpolacja ułamkowa binów i niezależne Smoothing
  - `ActionRuntime.cs` — bezpieczny wątkowo stan kafelka i sygnatura wizualna `ComputeVisualSignature`
  - `Renderer.cs` — szybki renderer 8-bit PNG dla wszystkich 7 trybów, czcionki 5×7, Beat Pulse i ikony ciszy/pauzy
  - `Settings.cs` — model ustawień, normalizacja i wbudowane presety
  - `HighResWaitTimer.cs` — precyzyjny timer Win32
- `PropertyInspector/` — interfejs konfiguracji HTML/JS w aplikacji Stream Dock
- `Imgs/`, `previews/` — ikony i podgląd wtyczki
- `build.ps1` — skrypt budowania Release i pakowania `.streamDeckPlugin`
- `preset_bas.json`, `preset_srodek.json`, `preset_wysokie.json` — przykładowe presety 3-pasmowe

## Budowanie i instalacja

Wymagania: Windows 10/11 oraz **.NET 8 SDK**.

1. Zatrzymaj aplikację **Stream Dock AJAZZ** (żeby pliki w `Plugin/` nie były zablokowane).
2. Zbuduj wtyczkę w trybie `Release`:
   ```powershell
   .\build.ps1
   ```
   *(Aby dodatkowo wygenerować paczkę `dist\MusicVisualizerLoopback.streamDeckPlugin`, użyj `.\build.ps1 -Package`.)*
3. Skopiuj cały katalog wtyczki jako `com.musicvisualizer.loopback.visualizer.sdPlugin` do:
   ```text
   %APPDATA%\HotSpot\StreamDock\plugins\
   ```
4. Uruchom ponownie **Stream Dock AJAZZ**.
