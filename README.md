# Sonator

Sonator is a Windows MIDI sequencer and lightweight DAW inspired by the
pattern-based workflow of classic music software such as Notator.

Built with .NET 8 and WPF, it combines MIDI composition tools, song
arrangement, project management, and native VST3 instrument hosting in a
single desktop application.

## Features

- Pattern-based song arrangement with editable tracks and blocks
- Piano roll and step-sequencer editing
- MIDI note, velocity, duration, transpose, and quantize controls
- MIDI output device selection and rescanning
- Transport controls and synchronized playheads
- Copy, paste, undo, and redo workflows
- Song open, save, and **Save As** support
- Configurable VST3 scan paths, including the standard Windows directory
  `C:\Program Files\Common Files\VST3`
- VST3 plug-in browser with thumbnails
- Real VST3 module loading and parameter editing
- Native VST3 editor windows through `IPlugView`, with DPI-aware sizing
- Real-time VST3 instrument audio through `IAudioProcessor` and WASAPI

## VST3 support

Sonator includes a native C++ bridge that loads real VST3 plug-ins, creates
their `IComponent` and `IEditController` instances, exposes plug-in
parameters, hosts native editor windows, and sends generated audio to the
Windows audio device.

The bridge uses only Steinberg's MIT-licensed `pluginterfaces` module. A
prebuilt Windows DLL is included and copied beside the application during a
normal .NET build. Native editor and real-time audio operation have been
verified with TAL-NoiseMaker.

See [VST3 hosting architecture](docs/VST3_HOSTING.md) and the
[native bridge README](native/VstHostBridge/README.md) for implementation and
build details.

## Requirements

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A MIDI output device or virtual MIDI port for external MIDI playback
- Optional VST3 instruments for plug-in hosting

## Build and run

```powershell
git clone https://github.com/joescaramucci/Sonator.git
cd Sonator
dotnet restore
dotnet run --project Sonator.csproj
```

The included prebuilt `VstHostBridge.dll` is copied automatically to the
application output directory. Rebuilding the native bridge is only necessary
when changing its C++ source.

## Technology

- C# / .NET 8
- WPF
- DryWetMIDI
- C++17 native VST3 bridge
- WASAPI shared-mode audio output
