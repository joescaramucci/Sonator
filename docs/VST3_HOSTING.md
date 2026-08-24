# VST3 integration in Sonator

This document describes Sonator's native VST3 hosting architecture.

## Current implementation

The VST3 roadmap is implemented end to end:

- real module loading;
- real `IComponent` and `IEditController` creation;
- parameter reading and writing;
- native `IPlugView` GUI hosting in WPF;
- real-time `IAudioProcessor` output through WASAPI;
- isolated native-editor thumbnail generation.

### Native bridge

`native/VstHostBridge/` contains the C++ bridge built with CMake.
`VstHostBridge_LoadModule` resolves the executable inside a `.vst3` bundle,
loads it with `LoadLibraryW`, calls `GetPluginFactory`, finds the Audio Module
Class, and creates its `IComponent`.

The bridge obtains the related `IEditController`, either through
`queryInterface` or `getControllerClassId` plus `createInstance`. It connects
the component and controller through `IConnectionPoint` and synchronizes their
state. Some plug-ins create their parameter list only after this handshake.
For example, TAL-NoiseMaker exposes 2,169 parameters after connection.

The exported ABI includes:

- `VstHostBridge_GetPluginDisplayName`;
- `VstHostBridge_GetParameterCount`;
- `VstHostBridge_GetParameterInfo`;
- `VstHostBridge_GetParameterNormalized`;
- `VstHostBridge_SetParameterNormalized`;
- native editor attachment and sizing functions;
- real-time audio start, stop, and status functions.

The bridge vendors only Steinberg's MIT-licensed `pluginterfaces` module.
It does not include the GPL or commercial `public.sdk` implementation.

### Managed host

- `Services/Vst3/Vst3HostInterop.cs` declares the native ABI through P/Invoke.
- `Services/Vst3/IVst3PluginHost.cs` defines the managed hosting contract.
- `Services/Vst3/Vst3PluginHost.cs` owns module handles, parameters, editor
  attachment, audio state, and controlled bridge error reporting.
- `VstPluginEditorDialog` displays real parameters and writes changes through
  `setParamNormalized`.
- `Vst3NativeEditorWindow` hosts the plug-in's native editor.
- `Vst3EditorHost` is the WPF `HwndHost` that adopts the native child window.

### Native GUI hosting

The bridge creates a lightweight Win32 container window, attaches the
plug-in's `IPlugView`, and implements `IPlugFrame::resizeView`. The managed
side adopts that HWND through `HwndHost`. Window dimensions are converted
using the current WPF DPI scale.

This path has been verified with TAL-NoiseMaker, including correct editor
sizing on scaled displays.

### Real-time audio

A dedicated native thread initializes COM, opens the default WASAPI output in
shared mode, activates the plug-in output bus, configures processing, and
repeatedly calls `IAudioProcessor::process`.

The generated floating-point samples are written to the WASAPI buffer until
the host stops processing. Audio output has been verified with TAL-NoiseMaker.

### Thumbnail isolation

Native plug-ins can fail with process-level faults that managed exception
handling cannot safely recover from. Sonator therefore generates each native
editor thumbnail in `Sonator.VstThumbnailHost.exe`.

A helper crash affects only that capture. Sonator uses a branded fallback
thumbnail and continues scanning the remaining plug-ins.

## Build status

The bridge has been built and tested with MinGW-w64 and CMake. The repository
contains a prebuilt DLL at:

`native/VstHostBridge/prebuilt/VstHostBridge.dll`

`Sonator.csproj` copies this DLL beside `Sonator.exe` during a normal .NET
build. Rebuild the native bridge only when changing its C++ source; see
`native/VstHostBridge/README.md`.

## Future extensions

Potential future work includes:

- forwarding sequencer and external MIDI events to hosted instruments through
  `IEventList`;
- effect plug-ins with audio input buses;
- selectable WASAPI output devices.

## Related files

| File | Purpose |
|---|---|
| `native/VstHostBridge/CMakeLists.txt` | Native bridge build |
| `native/VstHostBridge/include/VstHostBridge.h` | Exported C ABI |
| `native/VstHostBridge/src/VstHostBridge.cpp` | Component, GUI, and audio host |
| `native/VstHostBridge/thirdparty/pluginterfaces/` | Vendored MIT VST3 interfaces |
| `Services/Vst3/Vst3HostInterop.cs` | P/Invoke declarations |
| `Services/Vst3/IVst3PluginHost.cs` | Managed host interface |
| `Services/Vst3/Vst3PluginHost.cs` | Managed host implementation |
| `Services/Vst3/Vst3ThumbnailService.cs` | Isolated thumbnail orchestration |
| `tools/VstThumbnailHost/` | Out-of-process thumbnail helper |
| `Vst3EditorHost.cs` | WPF native editor host |
| `Vst3NativeEditorWindow.xaml(.cs)` | Native editor window |
| `VstPluginEditorDialog.xaml(.cs)` | Parameter and audio UI |
