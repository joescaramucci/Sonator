# Integrazione SDK VST3 in Sonator — piano architetturale

Questo documento descrive lo stato attuale e il piano per integrare il vero
SDK VST3 di Steinberg in Sonator, così da poter caricare ed eseguire
plug-in VST3 reali (non solo elencarli come file).

## Stato attuale (questa iterazione)

Implementati tutti i punti della roadmap: **module loading reale** +
**istanziazione reale IComponent/IEditController** + **lettura/scrittura dei
parametri reali** + **GUI nativa (IPlugView) ospitata in una finestra WPF
dedicata** + **motore audio in tempo reale (IAudioProcessor + WASAPI)**.

- `native/VstHostBridge/` — progetto C++ nativo (CMake). `VstHostBridge_LoadModule`
  ora: risolve il binario reale dentro un bundle `.vst3`
  (`Contents/x86_64-win/<nome>.vst3`), lo carica con `LoadLibraryW`, chiama
  `GetPluginFactory()`, enumera le classi del factory per trovare la classe
  "Audio Module Class" (il vero instrument/effect) e la istanzia come
  `IComponent`. Ottiene poi l'`IEditController` corrispondente (stesso oggetto
  via `queryInterface`, oppure oggetto separato via
  `getControllerClassId`+`createInstance`), li collega con `IConnectionPoint`
  e sincronizza lo stato (`getState`/`setComponentState`) — passaggio
  necessario perché molti plugin costruiscono l'elenco parametri solo dopo
  questo handshake (verificato: TAL-NoiseMaker riporta 0 parametri prima del
  `connect()`, 2169 parametri reali dopo). Espone poi:
  `VstHostBridge_GetPluginDisplayName`, `VstHostBridge_GetParameterCount`,
  `VstHostBridge_GetParameterInfo` (id, titolo, unità, default, dal vero
  `IEditController::getParameterInfo`), `VstHostBridge_GetParameterNormalized`
  e `VstHostBridge_SetParameterNormalized` (lettura/scrittura in tempo reale
  dei parametri del plugin).
- Usa **solo** il modulo `pluginterfaces` della SDK VST3 di Steinberg
  (vendorizzato, MIT, in `native/VstHostBridge/thirdparty/pluginterfaces/`):
  sono dichiarazioni di interfacce astratte pure (nessuna implementazione,
  nessun codice GPL/commerciale di `public.sdk`).
- `Services/Vst3/Vst3HostInterop.cs` — dichiarazioni `[DllImport]` verso
  `VstHostBridge.dll` (incluse le funzioni parametri).
- `Services/Vst3/IVst3PluginHost.cs` / `Vst3PluginHost.cs` — layer C# con
  `GetPluginDisplayName`, `GetParameters` (elenco reale con id/titolo/unità/
  valore) e `SetParameterValue`, tutti con gestione difensiva delle eccezioni
  se il bridge nativo non è disponibile.
- `VstPluginEditorDialog` — al momento dell'apertura carica il modulo VST3
  reale, mostra l'elenco reale dei parametri (con slider funzionanti che
  scrivono davvero sul plugin tramite `setParamNormalized`) e, se il plugin
  espone un `IAudioProcessor` con almeno un bus di output, avvia
  automaticamente il motore audio in tempo reale (WASAPI). Il pulsante
  "Apri editor nativo" apre `Vst3NativeEditorWindow`, una finestra WPF
  dedicata che ospita l'`HWND` reale della GUI del plugin tramite
  `Vst3EditorHost` (`HwndHost`), dimensionata correttamente in base al DPI
  dello schermo.
- **GUI nativa (`IPlugView`)**: il bridge crea una finestra Win32 "contenitore"
  leggera (`SonatorVstEditorHostWindow`), ci "attacca" la view del plugin
  (`IPlugView::attached`) e implementa `IPlugFrame::resizeView` lato host per
  gestire il resize richiesto dal plugin. Il lato C# adotta questo HWND con un
  `HwndHost` standard. Verificato dal vivo con TAL-NoiseMaker: la finestra si
  apre alla dimensione corretta (bug di ridimensionamento all'80% dovuto al
  DPI scaling, risolto dividendo le dimensioni per `DpiScaleX`/`DpiScaleY`).
- **Motore audio in tempo reale**: un thread nativo dedicato inizializza COM,
  apre il device di output WASAPI in modalità shared (formato IEEE float),
  attiva il bus di output del plugin (`activateBus`/`setActive`), chiama
  `setupProcessing`/`setProcessing` e poi cicla su `IAudioProcessor::process`
  scrivendo i campioni nel buffer WASAPI, finché non viene fermato. Nessun
  evento MIDI viene inoltrato dall'host (`inputEvents`/`inputParameterChanges`
  a `nullptr`, consentito dalla spec VST3): la tastiera a schermo del plugin
  gestisce da sola il proprio stato delle note. **Verificato dal vivo**:
  cliccando i tasti della tastiera nella GUI nativa di TAL-NoiseMaker si sente
  davvero il suono in tempo reale.

**✅ Il bridge nativo è stato compilato e testato in questo ambiente** usando
un toolchain MinGW-w64 (GCC 16.2.0) + CMake 4.4.2 installato appositamente
(nessun Visual Studio richiesto). Test eseguito con successo contro il
plugin VST3 reale `TAL-NoiseMaker.vst3` installato sulla macchina
dell'utente (`C:\Program Files\Common Files\VST3\`): caricamento del bundle,
istanziazione `IComponent`/`IEditController`, lettura di 2169 parametri
reali (es. "Master Volume", "Filter Cutoff", "Filter Resonance", "Filter
Attack/Decay/Sustain/Release", ...) con valori correnti e di default
corretti, e scrittura di nuovi valori tramite `setParamNormalized` — tutto
riuscito. Verificata anche la corretta gestione dell'errore su un percorso
inesistente. La DLL compilata è inclusa nel repo in
`native/VstHostBridge/prebuilt/VstHostBridge.dll` e viene copiata
automaticamente accanto a `Sonator.exe` ad ogni build .NET (vedi
`Sonator.csproj`). Se in futuro modifichi `VstHostBridge.cpp`, ricompila
seguendo le istruzioni in `native/VstHostBridge/README.md`.

## Perché non un hosting completo in un solo passaggio

Un host VST3 reale e funzionante richiede, in ordine:

1. ✅ **Caricamento modulo**: `LoadLibrary` del bundle `.vst3` + risoluzione di
   `GetPluginFactory` (esportata dal plugin).
2. ✅ **Enumerazione classi** tramite `IPluginFactory` (nome, categoria, UID).
3. ✅ **Istanziazione** di `IComponent` (audio effect/instrument) e
   `IEditController` (parametri/automazione), collegati con
   `IConnectionPoint`.
4. ✅ **Setup di processing**: bus arrangement, `setupProcessing`,
   `IAudioProcessor::process` chiamato in tempo reale da un motore audio
   dedicato (WASAPI shared mode, thread nativo separato).
5. ✅ **GUI hosting**: `IPlugView` restituisce un `HWND` nativo, "adottato" da
   un `HwndHost` WPF (`Vst3EditorHost`) dentro `Vst3NativeEditorWindow`.

Tutti i punti 1-5 sono completi e verificati dal vivo con TAL-NoiseMaker
(caricamento, parametri, GUI nativa dimensionata correttamente, audio
realtime udibile cliccando la tastiera a schermo).

## Roadmap (completata)

1. ✅ **Module loading reale**: `LoadLibraryW` + risoluzione bundle + verifica
   dell'export `GetPluginFactory`.
2. ✅ **IComponent/IEditController**: istanziazione reale, connessione via
   `IConnectionPoint`, lettura/scrittura dei parametri reali esposti
   dal plugin (elenco "PARAMETRI REALI" in `VstPluginEditorDialog`).
3. ✅ **GUI hosting**: `IPlugView` + `HwndHost` per mostrare la GUI nativa del
   plugin in una finestra WPF dedicata (`Vst3NativeEditorWindow`).
4. ✅ **Motore audio**: motore realtime (WASAPI shared) che instrada davvero
   l'audio dal plugin ospitato verso l'output della scheda audio, avviato
   automaticamente all'apertura del pannello parametri.

Possibili estensioni future (non richieste finora): inoltro di eventi MIDI
reali (`IEventList`/`NoteOnEvent`) dall'host verso il plugin (per pilotare i
synth da una tastiera MIDI esterna o dal sequencer di Sonator, non solo dalla
tastiera a schermo del plugin), supporto per plugin effetto con bus di input
audio, selezione del device di output WASAPI.

## File coinvolti

| File | Ruolo |
|---|---|
| `native/VstHostBridge/CMakeLists.txt` | Build del bridge nativo |
| `native/VstHostBridge/include/VstHostBridge.h` | ABI C esportata (v3: parametri + GUI nativa + audio realtime) |
| `native/VstHostBridge/src/VstHostBridge.cpp` | Implementazione (v3: IComponent/IEditController + IPlugView + IAudioProcessor/WASAPI) |
| `native/VstHostBridge/thirdparty/pluginterfaces/` | Interfacce VST3 MIT (Steinberg) vendorizzate |
| `Services/Vst3/Vst3HostInterop.cs` | Dichiarazioni P/Invoke |
| `Services/Vst3/IVst3PluginHost.cs` | Interfaccia lato C# (parametri, GUI nativa, audio) |
| `Services/Vst3/Vst3PluginHost.cs` | Implementazione con fallback controllato |
| `Vst3EditorHost.cs` | `HwndHost` che adotta l'`HWND` della GUI nativa del plugin |
| `Vst3NativeEditorWindow.xaml(.cs)` | Finestra WPF dedicata che ospita la GUI nativa (DPI-aware) |
| `VstPluginEditorDialog.xaml(.cs)` | UI con parametri reali, avvio automatico dell'audio, pulsante GUI nativa |
