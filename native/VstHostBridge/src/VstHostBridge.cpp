// VstHostBridge.cpp
//
// Real VST3 module hosting (step 2 of docs/VST3_HOSTING.md):
//   1. Resolve the actual platform binary inside a .vst3 bundle folder
//      (Contents/x86_64-win/<bundle-name>.vst3 on 64-bit Windows), or use
//      the path as-is if it is already a flat .vst3/.dll file.
//   2. LoadLibraryW the resolved binary and call its exported
//      "GetPluginFactory" entry point to obtain a real Steinberg
//      IPluginFactory.
//   3. Enumerate the factory's classes, find the first "Audio Module Class"
//      (the actual VST3 instrument/effect), and instantiate it as an
//      IComponent.
//   4. Obtain the matching IEditController (either the same object via
//      queryInterface, or a separate one created from
//      IComponent::getControllerClassId) and initialize both with a minimal
//      internal IHostApplication implementation.
//   5. Expose the edit controller's real parameter list (id, title, units,
//      step count, default value) to managed code.
//
// This uses ONLY the "pluginterfaces" module of the Steinberg VST3 SDK
// (vendored under native/VstHostBridge/thirdparty/pluginterfaces), which is
// MIT-licensed (see thirdparty/pluginterfaces/LICENSE.txt) and contains pure
// abstract interface declarations only - no GPL/commercial-licensed
// "public.sdk" helper code is used or required for this.
//
// Still NOT implemented: GUI hosting (IPlugView) and real-time audio
// processing (IAudioProcessor). See docs/VST3_HOSTING.md for the roadmap.

#define VSTHOSTBRIDGE_EXPORTS
#include "../include/VstHostBridge.h"

#include <windows.h>

#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/base/ipluginbase.h"
#include "pluginterfaces/vst/vsttypes.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivsthostapplication.h"
#include "pluginterfaces/vst/ivstmessage.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "pluginterfaces/gui/iplugview.h"

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propidl.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace
{
    namespace fs = std::filesystem;

    // thread_local so concurrent callers on different threads don't clobber
    // each other's last-error message.
    thread_local std::string g_lastError;
    thread_local std::string g_lastErrorNarrow; // storage returned by GetLastError()

    void SetBridgeError(const std::string& message)
    {
        g_lastError = message;
    }

    std::string Narrow(const std::wstring& wide)
    {
        if (wide.empty())
        {
            return {};
        }

        int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
        std::string narrow(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), narrow.data(), size, nullptr, nullptr);
        return narrow;
    }

    std::string FormatWin32Error(DWORD errorCode)
    {
        LPWSTR buffer = nullptr;
        DWORD length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            errorCode,
            MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
            reinterpret_cast<LPWSTR>(&buffer),
            0,
            nullptr);

        std::string message = length > 0 ? Narrow(std::wstring(buffer, length)) : "Unknown error";
        if (buffer)
        {
            LocalFree(buffer);
        }

        // Trim trailing CR/LF that FormatMessage tends to append.
        while (!message.empty() && (message.back() == '\n' || message.back() == '\r'))
        {
            message.pop_back();
        }

        std::ostringstream oss;
        oss << message << " (code " << errorCode << ")";
        return oss.str();
    }

    // Resolves the actual loadable binary for a VST3 module path:
    //   - If filePath is a directory ending in ".vst3" (a bundle), looks for
    //     Contents/x86_64-win/<bundle-name>.vst3 inside it.
    //   - Otherwise, assumes filePath is already a loadable binary
    //     (flat .vst3 file, or a legacy VST2 .dll) and returns it unchanged.
    // Returns an empty path if a bundle was detected but no matching binary
    // could be found for this platform.
    fs::path ResolveBinaryPath(const fs::path& filePath)
    {
        std::error_code ec;
        if (fs::is_directory(filePath, ec) && filePath.extension() == L".vst3")
        {
            auto candidate = filePath / L"Contents" / L"x86_64-win" / (filePath.stem().wstring() + L".vst3");
            if (fs::exists(candidate, ec))
            {
                return candidate;
            }

            return {};
        }

        return filePath;
    }

    bool TuidEquals(const TUID a, const TUID b)
    {
        return std::memcmp(a, b, sizeof(TUID)) == 0;
    }

    // Copies a VST3 UTF-16 (char16_t) string into a caller-provided wchar_t
    // buffer. Both are 16-bit UTF-16 code units on Windows.
    void CopyUtf16(const char16* src, size_t srcMax, wchar_t* dst, int32_t capacity)
    {
        int32_t i = 0;
        for (; i < static_cast<int32_t>(srcMax) && src[i] != 0 && i < capacity - 1; ++i)
        {
            dst[i] = static_cast<wchar_t>(src[i]);
        }
        dst[i] = L'\0';
    }

    // Minimal in-memory IBStream, used only to transfer IComponent state to
    // IEditController right after loading a dual-object plugin (standard
    // VST3 host bring-up sequence: getState() -> setComponentState()).
    class MemoryIBStream final : public IBStream
    {
    public:
        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IBStream_iid))
            {
                *obj = static_cast<IBStream*>(this);
                addRef();
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        uint32 PLUGIN_API addRef() override { return ++refCount_; }

        uint32 PLUGIN_API release() override
        {
            uint32 value = --refCount_;
            if (value == 0)
            {
                delete this;
            }
            return value;
        }

        tresult PLUGIN_API read(void* buffer, int32 numBytes, int32* numBytesRead) override
        {
            int32 available = static_cast<int32>(data_.size()) - position_;
            int32 toRead = numBytes < available ? numBytes : available;
            if (toRead < 0)
            {
                toRead = 0;
            }
            if (toRead > 0)
            {
                std::memcpy(buffer, data_.data() + position_, static_cast<size_t>(toRead));
                position_ += toRead;
            }
            if (numBytesRead != nullptr)
            {
                *numBytesRead = toRead;
            }
            return kResultOk;
        }

        tresult PLUGIN_API write(void* buffer, int32 numBytes, int32* numBytesWritten) override
        {
            if (numBytes > 0)
            {
                if (static_cast<size_t>(position_ + numBytes) > data_.size())
                {
                    data_.resize(static_cast<size_t>(position_ + numBytes));
                }
                std::memcpy(data_.data() + position_, buffer, static_cast<size_t>(numBytes));
                position_ += numBytes;
            }
            if (numBytesWritten != nullptr)
            {
                *numBytesWritten = numBytes;
            }
            return kResultOk;
        }

        tresult PLUGIN_API seek(int64 pos, int32 mode, int64* result) override
        {
            int64 newPos = position_;
            switch (mode)
            {
                case kIBSeekSet: newPos = pos; break;
                case kIBSeekCur: newPos = position_ + pos; break;
                case kIBSeekEnd: newPos = static_cast<int64>(data_.size()) + pos; break;
                default: return kInvalidArgument;
            }
            if (newPos < 0)
            {
                return kInvalidArgument;
            }
            position_ = static_cast<int32>(newPos);
            if (result != nullptr)
            {
                *result = position_;
            }
            return kResultOk;
        }

        tresult PLUGIN_API tell(int64* pos) override
        {
            if (pos == nullptr)
            {
                return kInvalidArgument;
            }
            *pos = position_;
            return kResultOk;
        }

        void rewind() { position_ = 0; }
        size_t size() const { return data_.size(); }

    private:
        std::vector<char> data_;
        int32 position_ = 0;
        std::atomic<uint32> refCount_{1};
    };

    // Minimal IHostApplication implementation, just enough to satisfy
    // IPluginBase::initialize (many plugins expect a non-null, working host
    // context and may call methods on it defensively). Lives forever as a
    // process-wide singleton: it is intentionally never destroyed.
    class SonatorHostApplication final : public IHostApplication
    {
    public:
        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IHostApplication_iid))
            {
                *obj = static_cast<IHostApplication*>(this);
                addRef();
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        uint32 PLUGIN_API addRef() override
        {
            return ++refCount_;
        }

        uint32 PLUGIN_API release() override
        {
            // Singleton: never actually deleted, just track the count.
            uint32 value = refCount_.load();
            if (value > 0)
            {
                value = --refCount_;
            }
            return value;
        }

        tresult PLUGIN_API getName(String128 name) override
        {
            static const wchar_t kHostName[] = L"Sonator";
            size_t i = 0;
            for (; kHostName[i] != L'\0' && i < 127; ++i)
            {
                name[i] = static_cast<char16>(kHostName[i]);
            }
            name[i] = 0;
            return kResultOk;
        }

        tresult PLUGIN_API createInstance(TUID /*cid*/, TUID /*_iid*/, void** /*obj*/) override
        {
            return kResultFalse;
        }

    private:
        std::atomic<uint32> refCount_{1};
    };

    SonatorHostApplication& HostApplicationInstance()
    {
        static SonatorHostApplication instance;
        return instance;
    }

    using GetFactoryProc = IPluginFactory* (PLUGIN_API*)();

    class SonatorPlugFrame; // defined below, needs BridgeModule's full definition
    struct AudioEngineState; // defined further below, needs WASAPI + threading types

    // Everything the bridge needs to keep alive for one loaded plugin module.
    struct BridgeModule
    {
        HMODULE hmodule = nullptr;
        IPluginFactory* factory = nullptr; // owned by the module itself, not released by us
        IComponent* component = nullptr;
        IEditController* controller = nullptr;
        bool controllerIsComponent = false; // true if controller == component (single-object plugin)
        std::string pluginName;

        // Native GUI hosting (IPlugView), created lazily by OpenEditor.
        IPlugView* editorView = nullptr;
        SonatorPlugFrame* editorFrame = nullptr;
        HWND editorContainerHwnd = nullptr;
        int32 editorWidth = 0;
        int32 editorHeight = 0;

        // Real-time audio processing (IAudioProcessor + WASAPI), created
        // lazily by StartAudio.
        AudioEngineState* audioEngine = nullptr;
    };

    // Window class used for the small native container window that hosts a
    // plugin's editor view. It has no visible chrome of its own - it just
    // forwards to DefWindowProc and exists purely so the plugin's own child
    // window(s) have a stable HWND to attach to.
    const wchar_t kEditorWindowClassName[] = L"SonatorVstEditorHostWindow";

    void EnsureEditorWindowClassRegistered()
    {
        static bool registered = false;
        if (registered)
        {
            return;
        }

        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = kEditorWindowClassName;
        wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)); // IDC_ARROW
        RegisterClassW(&wc);
        registered = true;
    }

    // Host-side IPlugFrame implementation: lets the plugin tell us it wants
    // its view resized (e.g. the user dragged a resize handle inside the
    // plugin's own GUI, or the plugin switched to a different skin size).
    // We resize our container window to match and then call back into
    // IPlugView::onSize(), as required by the VST3 GUI contract.
    class SonatorPlugFrame final : public IPlugFrame
    {
    public:
        explicit SonatorPlugFrame(BridgeModule* owner) : owner_(owner) {}

        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IPlugFrame_iid))
            {
                *obj = static_cast<IPlugFrame*>(this);
                addRef();
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        uint32 PLUGIN_API addRef() override { return ++refCount_; }

        uint32 PLUGIN_API release() override
        {
            uint32 value = --refCount_;
            if (value == 0)
            {
                delete this;
            }
            return value;
        }

        tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* newSize) override
        {
            if (newSize == nullptr)
            {
                return kInvalidArgument;
            }

            int32 width = newSize->getWidth();
            int32 height = newSize->getHeight();

            if (owner_ != nullptr && owner_->editorContainerHwnd != nullptr)
            {
                SetWindowPos(
                    owner_->editorContainerHwnd, nullptr, 0, 0, width, height,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                owner_->editorWidth = width;
                owner_->editorHeight = height;
            }

            if (view != nullptr)
            {
                view->onSize(newSize);
            }

            return kResultOk;
        }

    private:
        BridgeModule* owner_;
        std::atomic<uint32> refCount_{1};
    };

    // Detaches/destroys any open editor view for the given module. Shared by
    // VstHostBridge_CloseEditor and VstHostBridge_UnloadModule.
    void CloseEditorInternal(BridgeModule* module)
    {
        if (module == nullptr || module->editorView == nullptr)
        {
            return;
        }

        module->editorView->removed();
        module->editorView->setFrame(nullptr);
        module->editorView->release();
        module->editorView = nullptr;

        if (module->editorFrame != nullptr)
        {
            module->editorFrame->release();
            module->editorFrame = nullptr;
        }

        if (module->editorContainerHwnd != nullptr)
        {
            DestroyWindow(module->editorContainerHwnd);
            module->editorContainerHwnd = nullptr;
        }

        module->editorWidth = 0;
        module->editorHeight = 0;
    }

    // Real-time audio engine: hosts IAudioProcessor and pumps its rendered
    // output to the Windows default audio device via WASAPI (shared mode).
    // Everything COM/WASAPI-related (creation, the render loop, and
    // teardown) happens on the engine's own dedicated thread, so we never
    // have to worry about apartment/marshaling mismatches with whichever
    // thread called VstHostBridge_StartAudio (typically the WPF UI thread,
    // which already runs its own STA COM apartment).
    struct AudioEngineState
    {
        BridgeModule* module = nullptr;
        IAudioProcessor* processor = nullptr; // addRef'd; released by the engine thread on stop

        // Endpoint ID of the output device to open. Empty means the default device.
        std::wstring deviceId;

        std::thread thread;
        std::atomic<bool> stopRequested{false};

        // 0 = still starting up, 1 = started OK, -1 = failed to start.
        std::atomic<int> initResult{0};
        std::mutex initErrorMutex;
        std::string initError;

        bool activatedOutputBus = false;
        bool activatedComponent = false;
        bool startedProcessing = false;

        void SetInitError(const std::string& message)
        {
            std::lock_guard<std::mutex> lock(initErrorMutex);
            initError = message;
        }

        std::string GetInitError()
        {
            std::lock_guard<std::mutex> lock(initErrorMutex);
            return initError;
        }
    };

    // Runs entirely on the audio engine's dedicated thread: creates the
    // WASAPI render client, prepares the plugin for processing, then loops
    // calling IAudioProcessor::process() and writing the result to the
    // audio device until AudioEngineState::stopRequested is set.
    void RunAudioEngineThread(AudioEngineState* engine)
    {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        bool comInitializedHere = SUCCEEDED(hr);
        // RPC_E_CHANGED_MODE just means this thread already has a COM
        // apartment (harmless here, nothing to clean up on our part).

        IMMDeviceEnumerator* enumerator = nullptr;
        IMMDevice* device = nullptr;
        IAudioClient* client = nullptr;
        IAudioRenderClient* renderClient = nullptr;
        WAVEFORMATEX* mixFormat = nullptr;
        UINT32 bufferFrameCount = 0;

        auto fail = [&](const std::string& message)
        {
            engine->SetInitError(message);
            engine->initResult = -1;
        };

        hr = CoCreateInstance(
            __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
        if (FAILED(hr))
        {
            fail("Impossibile creare l'enumeratore dei device audio (WASAPI).");
        }
        else if (engine->deviceId.empty()
            ? FAILED(hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device))
            : FAILED(hr = enumerator->GetDevice(engine->deviceId.c_str(), &device)))
        {
            fail(engine->deviceId.empty()
                ? "Nessun device audio di output predefinito trovato."
                : "Il device audio di output selezionato non è disponibile.");
        }
        else if (FAILED(hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client))))
        {
            fail("Impossibile attivare IAudioClient sul device audio predefinito.");
        }
        else if (FAILED(hr = client->GetMixFormat(&mixFormat)))
        {
            fail("Impossibile leggere il formato audio del device (GetMixFormat).");
        }
        else
        {
            bool isFloat = mixFormat->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
            if (mixFormat->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mixFormat->cbSize >= 22)
            {
                // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = {00000003-0000-0010-8000-00AA00389B71}.
                // Defined locally to avoid depending on ksmedia.h/ksguid.lib.
                static const GUID kSubTypeIeeeFloat = {
                    0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}};
                auto* extensible = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mixFormat);
                isFloat = IsEqualGUID(extensible->SubFormat, kSubTypeIeeeFloat) != 0;
            }

            if (!isFloat)
            {
                fail("Il device audio predefinito non usa un formato a virgola mobile (non supportato in questa versione).");
            }
            else
            {
                const REFERENCE_TIME bufferDuration = 2'000'000; // 200 ms, in 100-ns units
                hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, bufferDuration, 0, mixFormat, nullptr);
                if (FAILED(hr))
                {
                    fail("IAudioClient::Initialize (shared mode) fallita: " + FormatWin32Error(static_cast<DWORD>(hr)));
                }
                else if (FAILED(hr = client->GetBufferSize(&bufferFrameCount)) || bufferFrameCount == 0)
                {
                    fail("IAudioClient::GetBufferSize fallita o ha restituito 0.");
                }
                else if (FAILED(hr = client->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(&renderClient))))
                {
                    fail("IAudioClient::GetService(IAudioRenderClient) fallita.");
                }
            }
        }

        BridgeModule* module = engine->module;
        int32 outputChannels = 2;
        int32 maxBlockSize = static_cast<int32>(bufferFrameCount) > 0 ? static_cast<int32>(bufferFrameCount) : 1024;

        if (engine->initResult == 0)
        {
            BusInfo outBusInfo{};
            if (module->component->getBusInfo(kAudio, kOutput, 0, outBusInfo) == kResultOk && outBusInfo.channelCount > 0)
            {
                outputChannels = outBusInfo.channelCount;
            }

            if (module->component->activateBus(kAudio, kOutput, 0, 1) == kResultOk)
            {
                engine->activatedOutputBus = true;
            }

            ProcessSetup setup{};
            setup.processMode = kRealtime;
            setup.symbolicSampleSize = kSample32;
            setup.maxSamplesPerBlock = maxBlockSize;
            setup.sampleRate = static_cast<SampleRate>(mixFormat->nSamplesPerSec);

            if (engine->processor->setupProcessing(setup) != kResultOk)
            {
                fail("IAudioProcessor::setupProcessing() fallita.");
            }
            else if (module->component->setActive(1) != kResultOk)
            {
                fail("IComponent::setActive(true) fallita.");
            }
            else
            {
                engine->activatedComponent = true;
                if (engine->processor->setProcessing(1) != kResultOk)
                {
                    fail("IAudioProcessor::setProcessing(true) fallita.");
                }
                else
                {
                    engine->startedProcessing = true;
                    if (FAILED(hr = client->Start()))
                    {
                        fail("IAudioClient::Start() fallita.");
                    }
                }
            }
        }

        if (engine->initResult == 0)
        {
            engine->initResult = 1; // signal success to VstHostBridge_StartAudio
        }

        if (engine->initResult == 1)
        {
            const int32 deviceChannels = mixFormat->nChannels;
            std::vector<std::vector<float>> planarBuffers(static_cast<size_t>(outputChannels));
            std::vector<Sample32*> channelPointers(static_cast<size_t>(outputChannels));
            for (int32 ch = 0; ch < outputChannels; ++ch)
            {
                planarBuffers[static_cast<size_t>(ch)].resize(static_cast<size_t>(maxBlockSize));
            }

            AudioBusBuffers outputBus{};
            outputBus.numChannels = outputChannels;
            outputBus.silenceFlags = 0;

            ProcessData processData{};
            processData.processMode = kRealtime;
            processData.symbolicSampleSize = kSample32;
            processData.numInputs = 0;
            processData.inputs = nullptr;
            processData.numOutputs = 1;
            processData.outputs = &outputBus;

            while (!engine->stopRequested.load())
            {
                UINT32 padding = 0;
                if (FAILED(client->GetCurrentPadding(&padding)))
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(5));
                    continue;
                }

                UINT32 available = bufferFrameCount > padding ? bufferFrameCount - padding : 0;
                if (available == 0)
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(2));
                    continue;
                }

                int32 samplesThisBlock = static_cast<int32>(available) < maxBlockSize ? static_cast<int32>(available) : maxBlockSize;

                BYTE* deviceBuffer = nullptr;
                if (FAILED(renderClient->GetBuffer(static_cast<UINT32>(samplesThisBlock), &deviceBuffer)))
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(2));
                    continue;
                }

                for (int32 ch = 0; ch < outputChannels; ++ch)
                {
                    channelPointers[static_cast<size_t>(ch)] = planarBuffers[static_cast<size_t>(ch)].data();
                }
                outputBus.channelBuffers32 = channelPointers.data();
                processData.numSamples = samplesThisBlock;

                engine->processor->process(processData);

                auto* deviceSamples = reinterpret_cast<float*>(deviceBuffer);
                for (int32 frame = 0; frame < samplesThisBlock; ++frame)
                {
                    for (int32 devCh = 0; devCh < deviceChannels; ++devCh)
                    {
                        float value = 0.0f;
                        if (devCh < outputChannels)
                        {
                            value = planarBuffers[static_cast<size_t>(devCh)][static_cast<size_t>(frame)];
                        }
                        deviceSamples[frame * deviceChannels + devCh] = value;
                    }
                }

                renderClient->ReleaseBuffer(static_cast<UINT32>(samplesThisBlock), 0);
            }

            client->Stop();
        }

        if (engine->startedProcessing && engine->processor != nullptr)
        {
            engine->processor->setProcessing(0);
        }
        if (engine->activatedComponent && module->component != nullptr)
        {
            module->component->setActive(0);
        }
        if (engine->activatedOutputBus && module->component != nullptr)
        {
            module->component->activateBus(kAudio, kOutput, 0, 0);
        }

        if (renderClient != nullptr) renderClient->Release();
        if (client != nullptr) client->Release();
        if (mixFormat != nullptr) CoTaskMemFree(mixFormat);
        if (device != nullptr) device->Release();
        if (enumerator != nullptr) enumerator->Release();

        if (engine->processor != nullptr)
        {
            engine->processor->release();
            engine->processor = nullptr;
        }

        if (engine->initResult == 0)
        {
            // We bailed out before ever setting a result (shouldn't normally
            // happen) - report failure so StartAudio doesn't hang.
            engine->initResult = -1;
        }

        if (comInitializedHere)
        {
            CoUninitialize();
        }
    }

    // Stops and fully tears down the audio engine for a module, if running.
    // Blocks until the engine thread has finished cleaning up.
    void StopAudioInternal(BridgeModule* module)
    {
        if (module == nullptr || module->audioEngine == nullptr)
        {
            return;
        }

        auto* engine = module->audioEngine;
        engine->stopRequested = true;
        if (engine->thread.joinable())
        {
            engine->thread.join();
        }

        delete engine;
        module->audioEngine = nullptr;
    }
}

int VstHostBridge_GetVersion()
{
    return 2;
}

void* VstHostBridge_LoadModule(const wchar_t* filePath)
{
    if (filePath == nullptr || *filePath == L'\0')
    {
        SetBridgeError("Percorso del plug-in non valido.");
        return nullptr;
    }

    fs::path requestedPath(filePath);
    std::error_code existsError;
    if (!fs::exists(requestedPath, existsError))
    {
        SetBridgeError("Il file/cartella del plug-in non esiste: " + Narrow(requestedPath.wstring()));
        return nullptr;
    }

    fs::path binaryPath = ResolveBinaryPath(requestedPath);
    if (binaryPath.empty())
    {
        SetBridgeError(
            "Bundle VST3 riconosciuto ma nessun binario Windows x86_64 trovato in "
            "Contents/x86_64-win dentro: " + Narrow(requestedPath.wstring()));
        return nullptr;
    }

    HMODULE hmodule = LoadLibraryW(binaryPath.c_str());
    if (hmodule == nullptr)
    {
        SetBridgeError("LoadLibrary fallita per " + Narrow(binaryPath.wstring()) + ": " + FormatWin32Error(GetLastError()));
        return nullptr;
    }

    auto getFactoryProc = reinterpret_cast<GetFactoryProc>(GetProcAddress(hmodule, "GetPluginFactory"));
    if (getFactoryProc == nullptr)
    {
        SetBridgeError(
            "Il modulo si carica ma non esporta \"GetPluginFactory\": non e' un plug-in VST3 valido ("
            + Narrow(binaryPath.wstring()) + ").");
        FreeLibrary(hmodule);
        return nullptr;
    }

    IPluginFactory* factory = getFactoryProc();
    if (factory == nullptr)
    {
        SetBridgeError("GetPluginFactory() ha restituito nullptr.");
        FreeLibrary(hmodule);
        return nullptr;
    }

    // Find the first "Audio Module Class" (the actual instrument/effect).
    TUID componentCid{};
    std::string pluginName;
    bool foundComponentClass = false;
    int32 classCount = factory->countClasses();
    for (int32 i = 0; i < classCount; ++i)
    {
        PClassInfo info{};
        if (factory->getClassInfo(i, &info) != kResultOk)
        {
            continue;
        }

        if (std::strcmp(info.category, "Audio Module Class") == 0 && !foundComponentClass)
        {
            std::memcpy(componentCid, info.cid, sizeof(TUID));
            pluginName.assign(info.name, strnlen(info.name, sizeof(info.name)));
            foundComponentClass = true;
        }
    }

    if (!foundComponentClass)
    {
        SetBridgeError("Nessuna classe \"Audio Module Class\" trovata nel factory VST3 di " + Narrow(binaryPath.wstring()) + ".");
        FreeLibrary(hmodule);
        return nullptr;
    }

    IComponent* component = nullptr;
    if (factory->createInstance(componentCid, IComponent_iid, reinterpret_cast<void**>(&component)) != kResultOk || component == nullptr)
    {
        SetBridgeError("createInstance(IComponent) fallita per la classe audio effect del plug-in.");
        FreeLibrary(hmodule);
        return nullptr;
    }

    if (component->initialize(&HostApplicationInstance()) != kResultOk)
    {
        SetBridgeError("IComponent::initialize() fallita.");
        component->release();
        FreeLibrary(hmodule);
        return nullptr;
    }

    // The edit controller (parameters) may live on the same object as the
    // component (single-component plugins) or as a separate class.
    IEditController* controller = nullptr;
    bool controllerIsComponent = false;
    if (component->queryInterface(IEditController_iid, reinterpret_cast<void**>(&controller)) == kResultOk && controller != nullptr)
    {
        controllerIsComponent = true;
    }
    else
    {
        TUID controllerCid{};
        if (component->getControllerClassId(controllerCid) == kResultOk)
        {
            IEditController* separateController = nullptr;
            if (factory->createInstance(controllerCid, IEditController_iid, reinterpret_cast<void**>(&separateController)) == kResultOk
                && separateController != nullptr)
            {
                if (separateController->initialize(&HostApplicationInstance()) == kResultOk)
                {
                    controller = separateController;
                }
                else
                {
                    separateController->release();
                }
            }
        }
    }

    // For dual-object plugins, connect the component and controller via
    // IConnectionPoint (standard VST3 host bring-up sequence), then
    // transfer the component's current state to the controller. Most
    // plugins only build their final, real parameter list once this
    // handshake has happened (verified against TAL-NoiseMaker.vst3: 0
    // parameters before connect(), 2169 real parameters after).
    if (controller != nullptr && !controllerIsComponent)
    {
        Steinberg::Vst::IConnectionPoint* componentCp = nullptr;
        Steinberg::Vst::IConnectionPoint* controllerCp = nullptr;
        component->queryInterface(IConnectionPoint_iid, reinterpret_cast<void**>(&componentCp));
        controller->queryInterface(IConnectionPoint_iid, reinterpret_cast<void**>(&controllerCp));
        if (componentCp != nullptr && controllerCp != nullptr)
        {
            componentCp->connect(controllerCp);
            controllerCp->connect(componentCp);
        }
        if (componentCp != nullptr)
        {
            componentCp->release();
        }
        if (controllerCp != nullptr)
        {
            controllerCp->release();
        }

        MemoryIBStream stateStream;
        if (component->getState(&stateStream) == kResultOk)
        {
            stateStream.rewind();
            controller->setComponentState(&stateStream);
        }
    }

    // A missing controller is not fatal for module loading itself (we still
    // proved the module loads and instantiates), it just means no parameter
    // list will be available.
    auto* module = new BridgeModule();
    module->hmodule = hmodule;
    module->factory = factory;
    module->component = component;
    module->controller = controller;
    module->controllerIsComponent = controllerIsComponent;
    module->pluginName = pluginName;
    return module;
}

void VstHostBridge_UnloadModule(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);

    StopAudioInternal(module);
    CloseEditorInternal(module);

    if (module->controller != nullptr)
    {
        if (!module->controllerIsComponent)
        {
            module->controller->terminate();
        }
        module->controller->release();
    }

    if (module->component != nullptr)
    {
        module->component->terminate();
        module->component->release();
    }

    // The factory is owned by the module itself (freed automatically when
    // the DLL is unloaded), so it must not be released here.

    if (module->hmodule != nullptr)
    {
        FreeLibrary(module->hmodule);
    }

    delete module;
}

const char* VstHostBridge_GetLastError()
{
    g_lastErrorNarrow = g_lastError.empty() ? "Nessun errore." : g_lastError;
    return g_lastErrorNarrow.c_str();
}

int VstHostBridge_GetPluginDisplayName(void* moduleHandle, wchar_t* outName, int32_t nameCapacity)
{
    if (moduleHandle == nullptr || outName == nullptr || nameCapacity <= 0)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    int32_t i = 0;
    for (; i < static_cast<int32_t>(module->pluginName.size()) && i < nameCapacity - 1; ++i)
    {
        outName[i] = static_cast<wchar_t>(static_cast<unsigned char>(module->pluginName[i]));
    }
    outName[i] = L'\0';
    return 1;
}

int32_t VstHostBridge_GetParameterCount(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return -1;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        return -1;
    }

    return module->controller->getParameterCount();
}

int VstHostBridge_GetParameterInfo(
    void* moduleHandle,
    int32_t index,
    uint32_t* outId,
    wchar_t* outTitle,
    int32_t titleCapacity,
    wchar_t* outUnits,
    int32_t unitsCapacity,
    int32_t* outStepCount,
    double* outDefaultNormalized)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        return 0;
    }

    ParameterInfo info{};
    if (module->controller->getParameterInfo(index, info) != kResultOk)
    {
        return 0;
    }

    if (outId != nullptr)
    {
        *outId = info.id;
    }
    if (outStepCount != nullptr)
    {
        *outStepCount = info.stepCount;
    }
    if (outDefaultNormalized != nullptr)
    {
        *outDefaultNormalized = info.defaultNormalizedValue;
    }
    if (outTitle != nullptr && titleCapacity > 0)
    {
        CopyUtf16(info.title, 128, outTitle, titleCapacity);
    }
    if (outUnits != nullptr && unitsCapacity > 0)
    {
        CopyUtf16(info.units, 128, outUnits, unitsCapacity);
    }

    return 1;
}

double VstHostBridge_GetParameterNormalized(void* moduleHandle, uint32_t paramId)
{
    if (moduleHandle == nullptr)
    {
        return -1.0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        return -1.0;
    }

    return module->controller->getParamNormalized(paramId);
}

int VstHostBridge_SetParameterNormalized(void* moduleHandle, uint32_t paramId, double value)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        return 0;
    }

    return module->controller->setParamNormalized(paramId, value) == kResultOk ? 1 : 0;
}

int VstHostBridge_HasEditor(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        return 0;
    }

    if (module->editorView != nullptr)
    {
        // Already open: we know it works.
        return 1;
    }

    IPlugView* view = module->controller->createView(ViewType::kEditor);
    if (view == nullptr)
    {
        return 0;
    }

    bool supported = view->isPlatformTypeSupported(kPlatformTypeHWND) == kResultTrue;
    view->release();
    return supported ? 1 : 0;
}

int VstHostBridge_OpenEditor(void* moduleHandle, void* parentHwnd, void** outChildHwnd, int32_t* outWidth, int32_t* outHeight)
{
    if (moduleHandle == nullptr || parentHwnd == nullptr)
    {
        SetBridgeError("Handle del modulo o finestra padre non validi.");
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->controller == nullptr)
    {
        SetBridgeError("Nessun edit controller disponibile: impossibile creare l'editor.");
        return 0;
    }

    if (module->editorView != nullptr)
    {
        // Already open: just report the existing handle/size (no-op).
        if (outChildHwnd != nullptr)
        {
            *outChildHwnd = module->editorContainerHwnd;
        }
        if (outWidth != nullptr)
        {
            *outWidth = module->editorWidth;
        }
        if (outHeight != nullptr)
        {
            *outHeight = module->editorHeight;
        }
        return 1;
    }

    IPlugView* view = module->controller->createView(ViewType::kEditor);
    if (view == nullptr)
    {
        SetBridgeError("Il plug-in non espone una vista editor (createView ha restituito nullptr).");
        return 0;
    }

    if (view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue)
    {
        SetBridgeError("La vista dell'editor non supporta l'incorporamento HWND su questa piattaforma.");
        view->release();
        return 0;
    }

    ViewRect rect{};
    view->getSize(&rect);
    int32 width = rect.getWidth() > 0 ? rect.getWidth() : 400;
    int32 height = rect.getHeight() > 0 ? rect.getHeight() : 300;

    EnsureEditorWindowClassRegistered();
    HWND container = CreateWindowExW(
        0,
        kEditorWindowClassName,
        L"",
        WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        0, 0, width, height,
        static_cast<HWND>(parentHwnd),
        nullptr,
        GetModuleHandleW(nullptr),
        nullptr);
    if (container == nullptr)
    {
        SetBridgeError("CreateWindowEx fallita per il contenitore dell'editor: " + FormatWin32Error(GetLastError()));
        view->release();
        return 0;
    }

    auto* frame = new SonatorPlugFrame(module);
    view->setFrame(frame);

    if (view->attached(container, kPlatformTypeHWND) != kResultOk)
    {
        SetBridgeError("IPlugView::attached() fallita: il plug-in non ha potuto agganciarsi alla finestra host.");
        view->setFrame(nullptr);
        frame->release();
        DestroyWindow(container);
        view->release();
        return 0;
    }

    module->editorView = view;
    module->editorFrame = frame;
    module->editorContainerHwnd = container;
    module->editorWidth = width;
    module->editorHeight = height;

    if (outChildHwnd != nullptr)
    {
        *outChildHwnd = container;
    }
    if (outWidth != nullptr)
    {
        *outWidth = width;
    }
    if (outHeight != nullptr)
    {
        *outHeight = height;
    }
    return 1;
}

void VstHostBridge_CloseEditor(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return;
    }

    CloseEditorInternal(static_cast<BridgeModule*>(moduleHandle));
}

int VstHostBridge_GetEditorSize(void* moduleHandle, int32_t* outWidth, int32_t* outHeight)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->editorView == nullptr)
    {
        return 0;
    }

    if (outWidth != nullptr)
    {
        *outWidth = module->editorWidth;
    }
    if (outHeight != nullptr)
    {
        *outHeight = module->editorHeight;
    }
    return 1;
}

int VstHostBridge_HasAudioProcessor(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->component == nullptr)
    {
        return 0;
    }

    IAudioProcessor* processor = nullptr;
    if (module->component->queryInterface(IAudioProcessor_iid, reinterpret_cast<void**>(&processor)) != kResultOk || processor == nullptr)
    {
        return 0;
    }

    int32 outputBuses = module->component->getBusCount(kAudio, kOutput);
    processor->release();
    return outputBuses > 0 ? 1 : 0;
}

int VstHostBridge_StartAudio(void* moduleHandle)
{
    return VstHostBridge_StartAudioOnDevice(moduleHandle, nullptr);
}

#if SONATOR_WITH_ASIO
// Windows hosts use the native IEEE 754 double layout for sample rates.
#define IEEE754_64FLOAT 1
#include "asio.h"
#include "iasiodrv.h"
// ASIO backend. Only the SDK's public headers are used (common/asio.h and
// common/iasiodrv.h, from the Steinberg ASIO SDK, GPL-3.0 option). Drivers are
// opened the same way the SDK's own host does it: CoCreateInstance on the
// driver CLSID, with the CLSID also used as the requested interface ID.

namespace
{
    // ASIO callbacks have no user-data argument, so the streaming session is
    // reached through this pointer. Only one ASIO session runs at a time.
    struct AsioSession;
    std::atomic<AsioSession*> g_asioSession{nullptr};

    struct AsioSession
    {
        AudioEngineState* engine = nullptr;
        IASIO* driver = nullptr;
        ASIOSampleType sampleType = ASIOSTFloat32LSB;
        long bufferSize = 0;
        bool postOutput = false;
        int32 deviceChannels = 2; // driver output channels written, at most 2
        int32 pluginChannels = 2; // channels produced by the plug-in output bus
        std::vector<ASIOBufferInfo> bufferInfos;
        std::vector<std::vector<float>> planar;
        std::vector<float> silence;
        std::vector<Sample32*> channelPointers;
        AudioBusBuffers outputBus{};
        ProcessData processData{};
    };

    float ClampUnit(float value)
    {
        return value < -1.0f ? -1.0f : (value > 1.0f ? 1.0f : value);
    }

    // Converts float samples to the driver's sample format and writes them to a driver buffer.
    void WriteFloatSamples(const float* source, void* destination, long count, ASIOSampleType type)
    {
        switch (type)
        {
        case ASIOSTFloat32LSB:
            std::memcpy(destination, source, sizeof(float) * static_cast<size_t>(count));
            break;
        case ASIOSTInt32LSB:
        {
            auto* out = static_cast<int32*>(destination);
            for (long i = 0; i < count; ++i)
            {
                out[i] = static_cast<int32>(static_cast<double>(ClampUnit(source[i])) * 2147483647.0);
            }
            break;
        }
        case ASIOSTInt16LSB:
        {
            auto* out = static_cast<int16_t*>(destination);
            for (long i = 0; i < count; ++i)
            {
                out[i] = static_cast<int16_t>(ClampUnit(source[i]) * 32767.0f);
            }
            break;
        }
        case ASIOSTInt24LSB:
        {
            auto* out = static_cast<uint8_t*>(destination);
            for (long i = 0; i < count; ++i)
            {
                const auto value = static_cast<int32>(static_cast<double>(ClampUnit(source[i])) * 8388607.0);
                out[i * 3] = static_cast<uint8_t>(value & 0xFF);
                out[i * 3 + 1] = static_cast<uint8_t>((value >> 8) & 0xFF);
                out[i * 3 + 2] = static_cast<uint8_t>((value >> 16) & 0xFF);
            }
            break;
        }
        default:
            break;
        }
    }

    // Runs on the driver's own thread: renders one block of the plug-in and writes it to the output buffers.
    void AsioBufferSwitch(long doubleBufferIndex, ASIOBool /*directProcess*/)
    {
        AsioSession* session = g_asioSession.load();
        if (session == nullptr || session->engine->processor == nullptr || session->engine->stopRequested.load())
        {
            return;
        }

        for (size_t ch = 0; ch < session->channelPointers.size(); ++ch)
        {
            session->channelPointers[ch] = session->planar[ch].data();
        }
        session->outputBus.channelBuffers32 = session->channelPointers.data();
        session->processData.numSamples = session->bufferSize;
        session->engine->processor->process(session->processData);

        for (int32 devCh = 0; devCh < session->deviceChannels; ++devCh)
        {
            const float* source = devCh < session->pluginChannels
                ? session->planar[static_cast<size_t>(devCh)].data()
                : session->silence.data();
            void* destination = session->bufferInfos[static_cast<size_t>(devCh)].buffers[doubleBufferIndex];
            WriteFloatSamples(source, destination, session->bufferSize, session->sampleType);
        }

        if (session->postOutput)
        {
            session->driver->outputReady();
        }
    }

    void AsioSampleRateChanged(ASIOSampleRate /*sampleRate*/)
    {
    }

    long AsioMessage(long selector, long value, void* /*message*/, double* /*opt*/)
    {
        switch (selector)
        {
        case kAsioSelectorSupported:
            return value == kAsioEngineVersion ? 1 : 0;
        case kAsioEngineVersion:
            return 2;
        default:
            // Time-info callbacks and driver resets are not handled: the host keeps using plain buffer switches.
            return 0;
        }
    }

    ASIOTime* AsioBufferSwitchTimeInfo(ASIOTime* /*params*/, long /*doubleBufferIndex*/, ASIOBool /*directProcess*/)
    {
        return nullptr;
    }

    // Reads the driver's channel count, buffer size, sample rate and sample format, and prepares the render buffers.
    bool ConfigureAsioSession(AsioSession& session, BridgeModule* module, double& sampleRate, std::string& error)
    {
        long inputChannels = 0;
        long outputChannels = 0;
        if (session.driver->getChannels(&inputChannels, &outputChannels) != ASE_OK || outputChannels <= 0)
        {
            error = "Il driver ASIO non espone canali di uscita.";
            return false;
        }
        session.deviceChannels = outputChannels < 2 ? outputChannels : 2;

        long minSize = 0;
        long maxSize = 0;
        long preferredSize = 0;
        long granularity = 0;
        if (session.driver->getBufferSize(&minSize, &maxSize, &preferredSize, &granularity) != ASE_OK || preferredSize <= 0)
        {
            error = "Il driver ASIO non riporta una dimensione di buffer valida.";
            return false;
        }
        session.bufferSize = preferredSize;

        sampleRate = 0.0;
        if (session.driver->getSampleRate(&sampleRate) != ASE_OK || sampleRate <= 0.0)
        {
            session.driver->setSampleRate(48000.0);
            if (session.driver->getSampleRate(&sampleRate) != ASE_OK || sampleRate <= 0.0)
            {
                error = "Il driver ASIO non ha una frequenza di campionamento valida.";
                return false;
            }
        }

        ASIOChannelInfo channelInfo{};
        channelInfo.channel = 0;
        channelInfo.isInput = ASIOFalse;
        if (session.driver->getChannelInfo(&channelInfo) != ASE_OK)
        {
            error = "Impossibile leggere il formato dei campioni del driver ASIO.";
            return false;
        }
        switch (channelInfo.type)
        {
        case ASIOSTFloat32LSB:
        case ASIOSTInt32LSB:
        case ASIOSTInt16LSB:
        case ASIOSTInt24LSB:
            session.sampleType = channelInfo.type;
            break;
        default:
            error = "Formato dei campioni del driver ASIO non supportato in questa versione.";
            return false;
        }

        session.pluginChannels = 2;
        BusInfo pluginBus{};
        if (module->component->getBusInfo(kAudio, kOutput, 0, pluginBus) == kResultOk && pluginBus.channelCount > 0)
        {
            session.pluginChannels = pluginBus.channelCount;
        }

        const size_t frames = static_cast<size_t>(preferredSize);
        session.planar.assign(static_cast<size_t>(session.pluginChannels), std::vector<float>(frames));
        session.silence.assign(frames, 0.0f);
        session.channelPointers.assign(static_cast<size_t>(session.pluginChannels), nullptr);
        session.outputBus.numChannels = session.pluginChannels;
        session.outputBus.silenceFlags = 0;
        session.processData.processMode = kRealtime;
        session.processData.symbolicSampleSize = kSample32;
        session.processData.numInputs = 0;
        session.processData.inputs = nullptr;
        session.processData.numOutputs = 1;
        session.processData.outputs = &session.outputBus;

        session.postOutput = session.driver->outputReady() == ASE_OK;
        return true;
    }

    void RunAsioEngineThread(AudioEngineState* engine)
    {
        const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        const bool comInitializedHere = SUCCEEDED(comResult);

        AsioSession session;
        session.engine = engine;
        BridgeModule* module = engine->module;
        bool activatedOutputBus = false;
        bool activatedComponent = false;
        bool startedProcessing = false;
        bool buffersCreated = false;
        bool driverStarted = false;

        auto fail = [&](const std::string& message)
        {
            engine->SetInitError(message);
            engine->initResult = -1;
        };

        CLSID driverClsid{};
        IASIO* driver = nullptr;
        HRESULT hr = CLSIDFromString(engine->deviceId.c_str(), &driverClsid);
        if (FAILED(hr))
        {
            fail("Identificatore del driver ASIO non valido.");
        }
        else if (FAILED(hr = CoCreateInstance(driverClsid, nullptr, CLSCTX_INPROC_SERVER, driverClsid, reinterpret_cast<void**>(&driver))) || driver == nullptr)
        {
            fail("Impossibile aprire il driver ASIO: " + FormatWin32Error(static_cast<DWORD>(hr)));
        }
        else if (!driver->init(GetDesktopWindow()))
        {
            char message[256]{};
            driver->getErrorMessage(message);
            fail(std::string("Il driver ASIO non si è inizializzato: ") + message);
        }
        else
        {
            session.driver = driver;
            double sampleRate = 0.0;
            std::string error;
            if (!ConfigureAsioSession(session, module, sampleRate, error))
            {
                fail(error);
            }
            else
            {
                if (module->component->activateBus(kAudio, kOutput, 0, 1) == kResultOk)
                {
                    activatedOutputBus = true;
                }

                ProcessSetup setup{};
                setup.processMode = kRealtime;
                setup.symbolicSampleSize = kSample32;
                setup.maxSamplesPerBlock = session.bufferSize;
                setup.sampleRate = sampleRate;
                if (engine->processor->setupProcessing(setup) != kResultOk)
                {
                    fail("IAudioProcessor::setupProcessing() fallita.");
                }
                else if (module->component->setActive(1) != kResultOk)
                {
                    fail("IComponent::setActive(true) fallita.");
                }
                else
                {
                    activatedComponent = true;
                    if (engine->processor->setProcessing(1) != kResultOk)
                    {
                        fail("IAudioProcessor::setProcessing(true) fallita.");
                    }
                    else
                    {
                        startedProcessing = true;

                        ASIOCallbacks callbacks{};
                        callbacks.bufferSwitch = AsioBufferSwitch;
                        callbacks.sampleRateDidChange = AsioSampleRateChanged;
                        callbacks.asioMessage = AsioMessage;
                        callbacks.bufferSwitchTimeInfo = AsioBufferSwitchTimeInfo;

                        session.bufferInfos.assign(static_cast<size_t>(session.deviceChannels), ASIOBufferInfo{});
                        for (int32 ch = 0; ch < session.deviceChannels; ++ch)
                        {
                            session.bufferInfos[static_cast<size_t>(ch)].isInput = ASIOFalse;
                            session.bufferInfos[static_cast<size_t>(ch)].channelNum = ch;
                        }

                        // Only one ASIO session can own the callbacks. A second engine must not take them over.
                        AsioSession* noSession = nullptr;
                        if (!g_asioSession.compare_exchange_strong(noSession, &session))
                        {
                            fail("Un altro plug-in sta già usando un driver ASIO. Ferma prima l'audio di quel plug-in.");
                        }
                        else if (driver->createBuffers(session.bufferInfos.data(), session.deviceChannels, session.bufferSize, &callbacks) != ASE_OK)
                        {
                            fail("IASIO::createBuffers fallita.");
                        }
                        else
                        {
                            buffersCreated = true;
                            if (driver->start() != ASE_OK)
                            {
                                fail("IASIO::start fallita.");
                            }
                            else
                            {
                                driverStarted = true;
                                engine->initResult = 1; // signal success to VstHostBridge_StartAudio
                            }
                        }
                    }
                }
            }
        }

        if (engine->initResult.load() == 1)
        {
            while (!engine->stopRequested.load())
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        }

        if (driverStarted)
        {
            driver->stop();
        }
        // Clear the pointer only if this session still owns it.
        AsioSession* owned = &session;
        g_asioSession.compare_exchange_strong(owned, nullptr);
        if (buffersCreated)
        {
            driver->disposeBuffers();
        }
        if (startedProcessing && engine->processor != nullptr)
        {
            engine->processor->setProcessing(0);
        }
        if (activatedComponent && module->component != nullptr)
        {
            module->component->setActive(0);
        }
        if (activatedOutputBus && module->component != nullptr)
        {
            module->component->activateBus(kAudio, kOutput, 0, 0);
        }
        if (driver != nullptr)
        {
            driver->Release();
        }
        if (engine->processor != nullptr)
        {
            engine->processor->release();
            engine->processor = nullptr;
        }
        if (engine->initResult.load() == 0)
        {
            engine->initResult = -1;
        }
        if (comInitializedHere)
        {
            CoUninitialize();
        }
    }
}

bool AsioBuiltIn()
{
    return true;
}
#else
namespace
{
    void RunAsioEngineThread(AudioEngineState* engine)
    {
        engine->SetInitError("Questa build di VstHostBridge non include il supporto ASIO.");
        engine->initResult = -1;
    }
}

bool AsioBuiltIn()
{
    return false;
}
#endif

// Starts the audio engine on the WASAPI endpoint deviceId, or on the ASIO driver whose CLSID is
// deviceId when useAsio is true. Both backends share the same start and stop path.
static int StartAudioEngine(void* moduleHandle, const wchar_t* deviceId, bool useAsio)
{
    if (moduleHandle == nullptr)
    {
        SetBridgeError("Handle del modulo non valido.");
        return 0;
    }

    if (useAsio && !AsioBuiltIn())
    {
        SetBridgeError("Questa build di VstHostBridge non include il supporto ASIO.");
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (module->component == nullptr)
    {
        SetBridgeError("Nessun IComponent disponibile: impossibile avviare l'audio.");
        return 0;
    }

    if (module->audioEngine != nullptr)
    {
        // Already running (or starting): treat as success.
        return 1;
    }

    IAudioProcessor* processor = nullptr;
    if (module->component->queryInterface(IAudioProcessor_iid, reinterpret_cast<void**>(&processor)) != kResultOk || processor == nullptr)
    {
        SetBridgeError("Il plug-in non espone IAudioProcessor: impossibile avviare l'audio.");
        return 0;
    }

    auto* engine = new AudioEngineState();
    engine->module = module;
    engine->processor = processor; // ownership transferred; released by the engine thread
    engine->deviceId = deviceId != nullptr ? deviceId : L"";

    module->audioEngine = engine;
    engine->thread = useAsio ? std::thread(RunAsioEngineThread, engine) : std::thread(RunAudioEngineThread, engine);

    // Wait (with a generous timeout) for the engine thread to finish its
    // startup sequence, so the caller gets an accurate success/failure
    // result instead of a false "started" positive.
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (engine->initResult.load() == 0 && std::chrono::steady_clock::now() < deadline)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    if (engine->initResult.load() != 1)
    {
        std::string error = engine->GetInitError();
        SetBridgeError(error.empty() ? "Avvio del motore audio fallito (timeout o errore sconosciuto)." : error);
        StopAudioInternal(module);
        return 0;
    }

    return 1;
}

// Builds the "<id>\t<name>\n" listing of active render endpoints.
static bool BuildAudioOutputList(std::wstring& text)
{
    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool comInitializedHere = SUCCEEDED(comResult);

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDeviceCollection* collection = nullptr;
    HRESULT hr = CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    bool ok = SUCCEEDED(hr);
    if (!ok)
    {
        SetBridgeError("Impossibile creare l'enumeratore dei device audio (WASAPI).");
    }
    else if (FAILED(hr = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &collection)))
    {
        ok = false;
        SetBridgeError("Impossibile elencare i device audio di output: " + FormatWin32Error(static_cast<DWORD>(hr)));
    }
    else
    {
        UINT count = 0;
        collection->GetCount(&count);
        for (UINT index = 0; index < count; index++)
        {
            IMMDevice* device = nullptr;
            LPWSTR id = nullptr;
            if (FAILED(collection->Item(index, &device)) || FAILED(device->GetId(&id)))
            {
                if (device != nullptr)
                {
                    device->Release();
                }
                continue;
            }

            std::wstring friendlyName = id;
            IPropertyStore* properties = nullptr;
            PROPVARIANT name;
            PropVariantInit(&name);
            if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &properties)) &&
                SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &name)) &&
                name.vt == VT_LPWSTR && name.pwszVal != nullptr)
            {
                friendlyName = name.pwszVal;
            }

            text += id;
            text += L'\t';
            text += friendlyName;
            text += L'\n';

            PropVariantClear(&name);
            if (properties != nullptr)
            {
                properties->Release();
            }
            CoTaskMemFree(id);
            device->Release();
        }
    }

    if (collection != nullptr)
    {
        collection->Release();
    }
    if (enumerator != nullptr)
    {
        enumerator->Release();
    }
    if (comInitializedHere)
    {
        CoUninitialize();
    }
    return ok;
}

int VstHostBridge_ListAudioOutputs(wchar_t* buffer, int capacity)
{
    std::wstring text;
    if (!BuildAudioOutputList(text))
    {
        return -1;
    }

    const int required = static_cast<int>(text.size()) + 1; // include the terminating null
    if (buffer != nullptr && capacity >= required)
    {
        std::wmemcpy(buffer, text.c_str(), text.size());
        buffer[text.size()] = L'\0';
    }
    return required;
}

int VstHostBridge_StartAudioOnDevice(void* moduleHandle, const wchar_t* deviceId)
{
    return StartAudioEngine(moduleHandle, deviceId, false);
}

int VstHostBridge_StartAudioAsio(void* moduleHandle, const wchar_t* driverClsid)
{
    return StartAudioEngine(moduleHandle, driverClsid, true);
}

int VstHostBridge_IsAsioSupported(void)
{
    return AsioBuiltIn() ? 1 : 0;
}

void VstHostBridge_StopAudio(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return;
    }

    StopAudioInternal(static_cast<BridgeModule*>(moduleHandle));
}

int VstHostBridge_IsAudioRunning(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    return (module->audioEngine != nullptr && module->audioEngine->initResult.load() == 1) ? 1 : 0;
}
