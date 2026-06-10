# Game-Only Audio Capture — Design Spec

## Ziel

System-Audio auf einen einzelnen Prozess (z.B. Arma Reforger) beschraenken, sodass Discord, Chrome, Spotify und alles andere nicht im Clip landen. Nutzt die Windows WASAPI Process Loopback API (ab Windows 10 2004).

## Entscheidungen

| Frage | Entscheidung |
|-------|-------------|
| Include vs Exclude | **Include** — "Nur Audio von Prozess X aufnehmen". Robuster, ein API-Call, kein Tracking von N Exclude-Prozessen. |
| Prozess-Auswahl | **Beides**: Fester Prozess aus Dropdown (Settings) + Auto-Detect Vordergrundfenster als Fallback fuer manuelle Recordings. |
| Buffer-Verhalten | **Auto-Restart**: Buffer startet mit AllAudio. Sobald der eingestellte Spiel-Prozess laeuft, Neustart mit Game-Only. Bei Prozess-Exit zurueck auf AllAudio. |

## Architektur

### Neue Dateien

#### `Services/ProcessLoopbackCapture.cs` (~250 Zeilen)

COM-Interop-Wrapper fuer `ActivateAudioInterfaceAsync` mit `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`.

Implementiert `IWaveIn`:
- `WaveFormat` — Format des Capture-Streams (typisch 32-bit float, device sample rate)
- `DataAvailable` — Event mit PCM-Chunks (identisch zu WasapiLoopbackCapture)
- `StartRecording()` / `StopRecording()`
- `Dispose()`

Constructor: `ProcessLoopbackCapture(uint processId)`

Interner Ablauf:
1. `AUDIOCLIENT_ACTIVATION_PARAMS` mit PID + INCLUDE mode aufbauen
2. In `PROPVARIANT` verpacken
3. `ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, params, handler, out op)` aufrufen
4. Completion-Handler wartet auf `IAudioClient`
5. `IAudioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, ...)`
6. `IAudioClient.GetService<IAudioCaptureClient>()`
7. Capture-Thread: `WaitForSingleObject(event)` → `GetBuffer()` → `DataAvailable` feuern → `ReleaseBuffer()`

COM-Interfaces (P/Invoke):
- `IActivateAudioInterfaceCompletionHandler`
- `IActivateAudioInterfaceAsyncOperation`
- `IAudioClient` (Activate, Initialize, Start, Stop, GetService, GetMixFormat)
- `IAudioCaptureClient` (GetBuffer, ReleaseBuffer, GetNextPacketSize)

#### `Services/GameProcessWatcher.cs` (~80 Zeilen)

Pollt alle 5 Sekunden ob der Ziel-Prozess (nach Name) laeuft.

```csharp
public sealed class GameProcessWatcher : IDisposable
{
    public event Action<int>? ProcessFound;   // PID
    public event Action? ProcessLost;
    
    public bool IsRunning { get; }
    public int? CurrentPid { get; }
    
    public GameProcessWatcher(string processName);
    public void Start();
    public void Stop();
    public void Dispose();
}
```

Logik:
- `Process.GetProcessesByName(name)` alle 5s
- Wenn vorher nicht da und jetzt da → `ProcessFound(pid)`
- Wenn vorher da und jetzt weg → `ProcessLost()`
- Mehrere Instanzen: erste nehmen (niedrigste PID = aeltester Prozess)

### Geaenderte Dateien

#### `Models/AppSettings.cs`

```csharp
public sealed class AudioSettings
{
    // Bestehend — unveraendert:
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemSound { get; set; } = true;
    public string MicDeviceId { get; set; } = "default";
    public string SystemDeviceId { get; set; } = "default";
    public double MicVolume { get; set; } = 2.0;
    public double SystemVolume { get; set; } = 1.0;
    public int OffsetMilliseconds { get; set; } = 0;
    
    // Neu:
    public AudioCaptureMode SystemCaptureMode { get; set; } = AudioCaptureMode.AllAudio;
    public string? GameProcessName { get; set; }  // null = Auto-Detect
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCaptureMode
{
    AllAudio,
    GameOnly
}
```

#### `Services/AudioPipeService.cs`

Aenderung nur in `Start()`, System-Loopback-Block (Zeilen ~103-141):

```csharp
if (_wantSys)
{
    try
    {
        if (settings.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly && gamePid.HasValue)
        {
            // Process-specific loopback via ActivateAudioInterfaceAsync
            var plc = new ProcessLoopbackCapture((uint)gamePid.Value);
            _sysCapture = plc;  // IWaveIn interface
        }
        else
        {
            // Standard loopback — alles
            var sysDev = FindRender(enumerator, _sysDeviceName)
                        ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _sysCapture = new WasapiLoopbackCapture(sysDev);
        }
        
        // Ab hier identisch: BufferedWaveProvider, DataAvailable, StartRecording
        _sysBuf = new BufferedWaveProvider(_sysCapture.WaveFormat) { ... };
        _sysCapture.DataAvailable += (_, e) => { ... };
        _sysCapture.StartRecording();
    }
    catch { ... }
}
```

Der Constructor bekommt ein zusaetzliches `int? gamePid` oder liest es aus den Settings.

Typfeld-Aenderung: `_sysCapture` wird von `WasapiLoopbackCapture?` zu `IWaveIn?` (beide implementieren es).

#### `Views/AudioSettingsView.xaml.cs`

`BuildPerAppHintCard()` wird ersetzt durch `BuildGameAudioCard()`:

Aufbau der Card:
1. **Header**: "Spiel-Audio" + Status-Dot
2. **Radio-Buttons**: "Alle Sounds aufnehmen" (default) / "Nur Spiel-Audio"
3. **Prozess-Dropdown** (nur sichtbar bei "Nur Spiel-Audio"):
   - Erster Eintrag: "Automatisch (Vordergrundfenster)"
   - Dann: alle Prozesse mit MainWindowHandle != 0, sortiert nach Name
   - DisplayMember: "Arma Reforger (ArmaReforger.exe)"
   - Refresh-Button daneben
4. **Status-Text**: 
   - "Arma Reforger erkannt — nur Game-Audio aktiv" (gruen)
   - "Arma Reforger nicht gestartet — alle Sounds aktiv" (gelb)
   - "Alle Sounds werden aufgenommen" (neutral, wenn AllAudio)

Aenderungen speichern → `host.Settings.Save()` + `host.ReplayBuffer.RestartIfRunningAsync()`

#### `AppHost.cs`

Neues Feld: `GameProcessWatcher? _gameWatcher`

In Service-Init:
```csharp
if (Settings.Current.Audio.SystemCaptureMode == AudioCaptureMode.GameOnly 
    && !string.IsNullOrEmpty(Settings.Current.Audio.GameProcessName))
{
    _gameWatcher = new GameProcessWatcher(Settings.Current.Audio.GameProcessName);
    _gameWatcher.ProcessFound += pid => 
    {
        Logger.Info($"Game process found: {Settings.Current.Audio.GameProcessName} (PID {pid})");
        _ = ReplayBuffer.RestartIfRunningAsync();
        ShowToast($"Spiel erkannt: {Settings.Current.Audio.GameProcessName}\nNur Game-Audio aktiv", ToastKind.Info);
    };
    _gameWatcher.ProcessLost += () =>
    {
        Logger.Info("Game process lost, reverting to all audio");
        _ = ReplayBuffer.RestartIfRunningAsync();
        ShowToast("Spiel beendet — alle Sounds aktiv", ToastKind.Info);
    };
    _gameWatcher.Start();
}
```

Bei Settings-Aenderung (Capture-Mode oder GameProcessName): alten Watcher stoppen, neuen starten.

#### `ReplayBufferService.cs`

Keine direkte Aenderung noetig. `RestartIfRunningAsync()` existiert bereits und baut AudioPipeService neu auf — der liest dann die aktuellen Settings + aktuellen PID.

AudioPipeService braucht nur Zugriff auf den aktuellen PID des Watchers. Loesung: `AppHost` uebergibt den PID beim AudioPipeService-Konstruktor oder der Service fragt `GameProcessWatcher.CurrentPid` ab.

### Datenfluss

```
[AllAudio Modus — unveraendert]
Alle Prozesse → Render Device → WasapiLoopbackCapture → BufferedWaveProvider → Pipeline → FFmpeg

[GameOnly Modus]
Spiel-Prozess (PID) → WASAPI Process Loopback INCLUDE → ProcessLoopbackCapture → BufferedWaveProvider → Pipeline → FFmpeg
                                                          (gleiche IWaveIn-Schnittstelle)
```

### Auto-Restart Sequenz

```
1. App startet, Buffer startet
   GameProcessName = "ArmaReforger", Prozess laeuft nicht
   → Buffer mit AllAudio (Fallback)
   
2. User startet Arma Reforger
   GameProcessWatcher: ProcessFound(PID 5678)
   → Buffer restart mit ProcessLoopbackCapture(5678)
   → Toast: "Spiel erkannt — nur Game-Audio aktiv"
   
3. User drueckt F9
   → Clip hat NUR Arma-Audio, kein Discord
   
4. User schliesst Arma
   GameProcessWatcher: ProcessLost()
   → Buffer restart mit WasapiLoopbackCapture (alles)
   → Toast: "Spiel beendet — alle Sounds aktiv"
```

### Manuelle Recordings (Strg+F9)

Bei Recording-Start:
- Wenn `GameProcessName` gesetzt und Prozess laeuft → ProcessLoopbackCapture mit dem PID
- Wenn `GameProcessName` gesetzt aber nicht laeuft → WasapiLoopbackCapture (Fallback)
- Wenn `GameProcessName` null (Auto-Detect) → Vordergrundfenster PID nehmen, ProcessLoopbackCapture

### Fallback-Kaskade

| Situation | Verhalten |
|-----------|-----------|
| Prozess nicht gefunden | Normales Loopback, Warn-Log |
| `ActivateAudioInterfaceAsync` schlaegt fehl | Normales Loopback, Error-Toast |
| Windows Build < 19041 | Setting ausgegraut, Tooltip: "Erfordert Windows 10 2004+" |
| PID stirbt waehrend Aufnahme | DataAvailable liefert Stille, Drift-Trim greift, naechster Restart fixt es |

### Prozess-Erkennung fuer Dropdown

`GetAudioCapableProcesses()`:
```csharp
Process.GetProcesses()
    .Where(p => p.MainWindowHandle != IntPtr.Zero)
    .Select(p => new { p.ProcessName, p.Id, Title = p.MainWindowTitle })
    .OrderBy(p => p.ProcessName)
```

Plus hardcoded Extras (Prozesse ohne Fenster die trotzdem Audio machen):
- `Discord` (oft im Tray)
- `Spotify` (kann minimiert sein)

### Nicht im Scope

- Multi-Prozess-Include (mehrere Spiele gleichzeitig aufnehmen)
- Per-Prozess-Lautstaerke im Clipper
- Audio-Session-Manager-Integration (zu komplex fuer V1, polling reicht)
