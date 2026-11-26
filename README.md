# Music Visualizer Loopback (Stream Dock / Stream Deck Plugin)

Lekki, wysokowydajny wizualizator audio (WASAPI Loopback) napisany w **C# / .NET 8** dla urządzeń **AJAZZ Stream Dock** (m.in. AKP153E) oraz kompatybilnych kontrolerów Stream Deck.

## Najważniejsze funkcje

- **Przechwytywanie WASAPI Loopback (NAudio)**: domyślne urządzenie systemowe (z automatycznym śledzeniem zmian urządzenia) lub ręcznie wybrane urządzenie wyjściowe.
- **Płynne 30 FPS na każdym przycisku przy minimalnym zużyciu CPU**:
  - dedykowana pętla renderująca na precyzyjnym timerze systemowym (`CreateWaitableTimerExW` High-Resolution),
  - równomierne taktowanie round-robin (bez burstów zapychających kolejkę hosta) + adaptacyjny `backpressure`,
  - szybka ścieżka **8-bitowego indeksowanego PNG** z keszowanym nagłówkiem (`IHDR`/`PLTE`) i bezalokacyjnym kodowaniem UTF-8 Base64 bezpośrednio do bufora ramki WebSocket (~0.12 ms/ramkę, ~700 B/PNG, praktycznie 0 GC),
  - automatyczne dopasowanie rozdzielczości renderu do podłączonego urządzenia (`85×85` dla AKP153E / 293SV3, `100×100` dla 293, `96×96` domyślnie).
- **5 trybów wizualizacji**: `Bars`, `Mirror`, `LED Segments`, `Dots`, `VU Meter` + opcjonalna nakładka `Numeric dB`.
- **Pełna konfiguracja w Property Inspectorze**:
  - presety pasm częstotliwości (`Low`, `Mid`, `High`, `Full`, `Custom`) i liczba słupków (`2–32`),
  - silnik decybelowy (`Gain`, `Min dB`, `Slope dB/oct`, `Gamma`, `Noise Gate`),
  - balistyka (`Attack`, `Release`, `FFT Smoothing`, `Peak Hold`, `Peak Fall`, `Auto-pause`),
  - edytor gradientów, kolorów LED i eksport/import presetów JSON.

## Struktura projektu

- `manifest.json` — manifest wtyczki Stream Dock / Stream Deck (SDK v2)
- `src/` — kod źródłowy wtyczki w C# (.NET 8 Windows, `Program.cs`, `MusicVisualizer.csproj`)
- `PropertyInspector/` — interfejs konfiguracji HTML/JS w aplikacji Stream Dock
- `Imgs/`, `previews/` — ikony i podgląd wtyczki
- `preset_bas.json`, `preset_srodek.json`, `preset_wysokie.json` — przykładowe presety 3-pasmowe do zaimportowania

## Budowanie i instalacja

Wymagania: Windows 10/11 oraz **.NET 8 SDK**.

1. Zatrzymaj aplikację **Stream Dock AJAZZ** (żeby pliki w `Plugin/` nie były zablokowane).
2. Zbuduj wtyczkę w trybie `Release` bezpośrednio do katalogu `Plugin/`:
   ```powershell
   dotnet build src\MusicVisualizer.csproj -c Release -o Plugin
   ```
3. Skopiuj cały katalog wtyczki jako `com.musicvisualizer.loopback.visualizer.sdPlugin` do:
   ```text
   %APPDATA%\HotSpot\StreamDock\plugins\
   ```
4. Uruchom ponownie **Stream Dock AJAZZ**.
