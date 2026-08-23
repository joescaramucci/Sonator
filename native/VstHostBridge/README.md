# VstHostBridge (native bridge)

Questo folder contiene un bridge nativo C++ che il lato .NET di Sonator usa
via P/Invoke per caricare **realmente** moduli VST3: risoluzione bundle,
`LoadLibrary`, `GetPluginFactory`, istanziazione di `IComponent`/
`IEditController`, lettura/scrittura dei parametri reali del plugin
(`getParameterInfo`/`getParamNormalized`/`setParamNormalized`), **hosting
della GUI nativa** (`IPlugView`/`IPlugFrame`, HWND adottato da un `HwndHost`
WPF) ed **elaborazione audio in tempo reale** (`IAudioProcessor` + WASAPI
shared mode su un thread dedicato). Usa solo il modulo `pluginterfaces` della
SDK VST3 di Steinberg (vendorizzato, licenza MIT, in
`thirdparty/pluginterfaces/`) — nessun codice GPL/commerciale di `public.sdk`
è usato o necessario per questo. Il lato .NET (`Services/Vst3` nel progetto
`Sonator.csproj`) rileva comunque se il bridge è assente o non funzionante e
degrada in modo controllato.

Una **DLL precompilata** è inclusa in `native/VstHostBridge/prebuilt/` e
viene copiata automaticamente accanto a `Sonator.exe` ad ogni build .NET
(vedi `Sonator.csproj`), quindi non è necessario compilare nulla per
usare la funzionalità così com'è oggi. Ricompila solo se modifichi
`src/VstHostBridge.cpp`.

## Perché serve un progetto nativo separato

L'SDK VST3 di Steinberg è distribuito solo in C++ (nessun binding .NET
ufficiale). Per ospitare davvero un plug-in VST3 da un'app WPF/.NET serve:

1. Un modulo nativo (questa DLL) che usa le interfacce VST3 e implementa il
   caricamento del modulo, la creazione di `IComponent`/`IEditController`
   (fatto), l'elaborazione audio (`IAudioProcessor`, fatto) e l'hosting
   della GUI nativa (`IPlugView`, che restituisce un `HWND`, fatto).
2. Un layer di interop P/Invoke lato C# (`Services/Vst3/Vst3HostInterop.cs`)
   che chiama le funzioni esportate da questa DLL con ABI C piatta.
3. Un `HwndHost` WPF (`Vst3EditorHost.cs`) che "adotta" l'`HWND` della GUI
   nativa del plugin dentro una finestra .NET (`Vst3NativeEditorWindow`).

## Le interfacce VST3 usate (licenza MIT, non GPL)

Il repo completo `steinbergmedia/vst3sdk` è GPLv3/commerciale, ma il modulo
`pluginterfaces` (solo dichiarazioni di interfacce astratte, nessuna
implementazione) è mantenuto come repo separato e indipendente,
`steinbergmedia/vst3_pluginterfaces`, sotto **licenza MIT pura** (vedi
`thirdparty/pluginterfaces/LICENSE.txt`). Questo bridge usa **solo** quelle
interfacce (`IPluginFactory`, `IComponent`, `IEditController`,
`IConnectionPoint`, `IBStream`, ecc.), chiamate tramite le loro vtable
COM-style — non è mai stato scaricato né usato codice di `public.sdk`
(licenza GPL/commerciale), e non serve `DEF_CLASS_IID`/altre macro che
richiederebbero file `.cpp` di quel modulo: si usa sempre la forma
header-only `ClassName_iid` (confrontata con `memcmp`).

## Come ricompilare (se modifichi VstHostBridge.cpp)

Qualunque toolchain C++17 con supporto Win32 va bene. Due opzioni verificate:

**Opzione A — Visual Studio / MSVC Build Tools**
```powershell
cd native/VstHostBridge
cmake -B build -A x64
cmake --build build --config Debug
copy build\Debug\VstHostBridge.dll prebuilt\VstHostBridge.dll
```

**Opzione B — MinGW-w64 (GCC) + CMake, nessuna installazione di Visual Studio**
```powershell
cd native/VstHostBridge
cmake -B build -G "MinGW Makefiles"
cmake --build build
copy build\VstHostBridge.dll prebuilt\VstHostBridge.dll
```
(Il `CMakeLists.txt` toglie già il prefisso "lib" che MinGW aggiungerebbe di
default, linka staticamente il runtime C++ e aggiunge `thirdparty` come
include path per gli header `pluginterfaces/...`, così la DLL non ha
dipendenze esterne da distribuire.)

Dopo aver rigenerato `prebuilt\VstHostBridge.dll`, un `dotnet build` normale
del progetto Sonator la copierà automaticamente nella cartella di output.

Se `VstHostBridge.dll` non è presente accanto a `Sonator.exe`, il lato C#
rileva l'assenza (`DllNotFoundException`) e mostra un messaggio informativo
nell'editor del plugin invece di andare in crash.

## Stato attuale / prossimi passi

- [x] ABI C definita (`include/VstHostBridge.h`, v2)
- [x] Module loading reale: risoluzione bundle `.vst3` (`Contents/x86_64-win/<nome>.vst3`),
      `LoadLibraryW` e verifica dell'export `GetPluginFactory`
- [x] Script di build CMake (verificato con MinGW-w64 GCC 16.2.0 + CMake 4.4.2)
- [x] Istanziazione reale `IComponent`/`IEditController` (enumerazione classi,
      `createInstance`, `initialize`, collegamento `IConnectionPoint`,
      sincronizzazione stato `getState`/`setComponentState`)
- [x] Lettura/scrittura parametri reali (`GetParameterCount`, `GetParameterInfo`,
      `GetParameterNormalized`, `SetParameterNormalized`)
- [x] Testato con un plugin VST3 reale installato sulla macchina (TAL-NoiseMaker,
      bundle `Contents/x86_64-win/...`): caricamento riuscito, 2169 parametri
      reali letti correttamente (nomi, default, valori correnti) e scrittura
      verificata; gestione errori su percorso inesistente verificata
- [x] Creazione bus audio e `IAudioProcessor::process` con un motore realtime
      (WASAPI shared mode, thread nativo dedicato) — verificato dal vivo:
      cliccando la tastiera a schermo di TAL-NoiseMaker si sente il suono
- [x] Hosting della GUI nativa (`IPlugView`) in una finestra WPF dedicata
      (`Vst3NativeEditorWindow` + `Vst3EditorHost` come `HwndHost`) —
      verificato dal vivo, incluso il fix del dimensionamento DPI-aware

Vedi anche `docs/VST3_HOSTING.md` nella root del repository per il piano
completo e lo stato del lato C#.
