# VstHostBridge

This folder contains the native C++ bridge used by Sonator through P/Invoke to
host real VST3 plug-ins.

The bridge implements:

- `.vst3` bundle resolution and `LoadLibraryW`;
- `GetPluginFactory` discovery;
- real `IComponent` and `IEditController` creation;
- component/controller connection and state synchronization;
- parameter enumeration, reading, and writing;
- native `IPlugView` GUI hosting;
- real-time `IAudioProcessor` processing through WASAPI shared mode.

It uses only Steinberg's MIT-licensed `pluginterfaces` module, vendored under
`thirdparty/pluginterfaces/`. No GPL or commercial `public.sdk` implementation
is used or required.

The managed implementation under `Services/Vst3/` detects a missing or
incompatible bridge and reports a controlled error instead of terminating the
application.

## Prebuilt DLL

A prebuilt DLL is stored at:

`native/VstHostBridge/prebuilt/VstHostBridge.dll`

`Sonator.csproj` copies it beside `Sonator.exe` during every .NET build. You
only need to rebuild the bridge after changing its C++ source.

## Why a native project is required

Steinberg's VST3 interfaces are C++. A WPF/.NET host therefore needs:

1. a native module that implements VST3 module loading, component creation,
   processing, and native GUI attachment;
2. a stable C ABI consumed by `Services/Vst3/Vst3HostInterop.cs`;
3. a WPF `HwndHost` that adopts the native editor HWND.

## Rebuilding

Any Windows C++17 toolchain can build the bridge.

### Visual Studio / MSVC

```powershell
cd native\VstHostBridge
cmake -B build -A x64
cmake --build build --config Debug
Copy-Item build\Debug\VstHostBridge.dll prebuilt\VstHostBridge.dll
```

### MinGW-w64

```powershell
cd native\VstHostBridge
cmake -B build -G "MinGW Makefiles"
cmake --build build
Copy-Item build\VstHostBridge.dll prebuilt\VstHostBridge.dll
```

The CMake project removes MinGW's default `lib` prefix, links the C++ runtime
statically, and includes the vendored `pluginterfaces` headers.

After replacing the prebuilt DLL, run a normal Sonator build:

```powershell
dotnet build Sonator.csproj
```

### ASIO backend (optional)

The bridge can also play plug-in audio through ASIO drivers. This needs the
Steinberg ASIO SDK, which is not part of this repository. Download it from
Steinberg, extract it outside the repository, and point CMake at its folder:

```powershell
cmake -B build -A x64 -DSONATOR_ASIO_SDK_DIR="C:\SDK\ASIOSDK"
```

The folder must contain `common\asio.h`. Without it, the bridge builds with
WASAPI only, and the ASIO option in Sonator is disabled. The SDK is available
under a dual license (proprietary or GPL v3); the bridge only uses its headers.

## Verified capabilities

- [x] Real VST3 bundle and module loading
- [x] `IComponent` and `IEditController` creation
- [x] `IConnectionPoint` connection and state synchronization
- [x] Real parameter enumeration, reading, and writing
- [x] `IAudioProcessor::process` through a dedicated WASAPI thread
- [x] Native `IPlugView` GUI hosting in a DPI-aware WPF window
- [x] TAL-NoiseMaker loading, 2,169 parameters, native GUI, and audible output

See `docs/VST3_HOSTING.md` for the complete architecture.
