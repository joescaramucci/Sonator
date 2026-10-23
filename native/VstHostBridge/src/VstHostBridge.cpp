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
#include "SonEffects.h"

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
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"

#include <immintrin.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propidl.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <iomanip>
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

    // Audio load of every running plug-in: how much of each block's time went into rendering it.
    // The audio threads record each block; Sonator reads and clears the window about once a second.
    struct DspMeter
    {
        std::atomic<uint32_t> peakPermille{0};
        std::atomic<uint64_t> busyMicros{0};
        std::atomic<uint64_t> periodMicros{0};
        std::atomic<int64_t> dropouts{0};
    };

    DspMeter g_dspMeter;

    long long QpcNow()
    {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        return now.QuadPart;
    }

    double QpcSeconds(long long ticks)
    {
        static const double frequency = []
        {
            LARGE_INTEGER value;
            QueryPerformanceFrequency(&value);
            return static_cast<double>(value.QuadPart);
        }();
        return static_cast<double>(ticks) / frequency;
    }

    // A block that took longer to render than it lasts could not reach the device in time: a dropout.
    void RecordDspBlock(double busySeconds, double periodSeconds)
    {
        if (periodSeconds <= 0.0)
        {
            return;
        }

        const auto permille = static_cast<uint32_t>((std::min)(busySeconds / periodSeconds, 9.99) * 1000.0);
        auto peak = g_dspMeter.peakPermille.load();
        while (permille > peak && !g_dspMeter.peakPermille.compare_exchange_weak(peak, permille))
        {
        }

        g_dspMeter.busyMicros += static_cast<uint64_t>(busySeconds * 1e6);
        g_dspMeter.periodMicros += static_cast<uint64_t>(periodSeconds * 1e6);
        if (busySeconds > periodSeconds)
        {
            ++g_dspMeter.dropouts;
        }
    }

    void RecordDspDropout()
    {
        ++g_dspMeter.dropouts;
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

        MemoryIBStream() = default;
        MemoryIBStream(const uint8_t* bytes, size_t count) : data_(bytes, bytes + count) {}

        void rewind() { position_ = 0; }
        size_t size() const { return data_.size(); }
        const std::vector<char>& bytes() const { return data_; }

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
    using ModuleEntryProc = bool (PLUGIN_API*)();

    // Frees a plug-in DLL the way the VST3 module rules ask: ExitDll first, when it was initialised.
    void ReleaseLibrary(HMODULE hmodule, ModuleEntryProc exitDll)
    {
        if (exitDll != nullptr)
        {
            exitDll();
        }
        FreeLibrary(hmodule);
    }

    class SonatorPlugFrame; // defined below, needs BridgeModule's full definition
    struct MixerVoice; // defined further below, needs the event list and threading types

    // Everything the bridge needs to keep alive for one loaded plugin module.
    struct BridgeModule
    {
        HMODULE hmodule = nullptr;
        IPluginFactory* factory = nullptr; // owned by the module itself, not released by us
        IComponent* component = nullptr;
        IEditController* controller = nullptr;
        bool controllerIsComponent = false; // true if controller == component (single-object plugin)
        std::string pluginName;
        std::string subCategories; // as the factory lists them, such as "Fx|Reverb" or "Instrument|Synth"; may be empty

        // Native GUI hosting (IPlugView), created lazily by OpenEditor.
        IPlugView* editorView = nullptr;
        SonatorPlugFrame* editorFrame = nullptr;
        HWND editorContainerHwnd = nullptr;
        int32 editorWidth = 0;
        int32 editorHeight = 0;

        // The plug-in's place in the mixer while its audio runs, made by StartAudio. Every running plug-in
        // plays through the one audio output, mixed together. Read under voiceMutex, as notes arrive from
        // other threads than the one that starts and stops the audio.
        std::mutex voiceMutex;
        std::shared_ptr<MixerVoice> voice;

        // The state last taken by VstHostBridge_SaveState, until it is read.
        std::vector<char> savedState;

        // An audio track's recorded clip, played in the mix in place of a plug-in: then there is no
        // component. Interleaved floats, and the point in seconds where playing starts.
        std::vector<float> clipSamples;
        int32 clipChannels = 0;
        double clipSampleRate = 0.0;
        double clipStartSeconds = 0.0;

        // The module's ExitDll, when it exports one: called before the DLL is freed.
        bool (PLUGIN_API* exitDll)() = nullptr;

        // The track's insert effects, run on the plug-in's output. The audio thread takes its own
        // reference under the lock (without waiting), so the chain stays alive while it is used.
        std::mutex effectsMutex;
        std::shared_ptr<son::EffectChain> effects;
    };

    // Runs the module's effects on a block of the plug-in's planar output.
    void ApplyEffects(BridgeModule* module, float** channels, int numChannels, int frames, double sampleRate)
    {
        std::shared_ptr<son::EffectChain> chain;
        {
            std::unique_lock<std::mutex> lock(module->effectsMutex, std::try_to_lock);
            if (lock.owns_lock())
            {
                chain = module->effects;
            }
        }
        if (chain != nullptr)
        {
            chain->Process(channels, numChannels, frames, sampleRate);
        }
    }

    // A VST3 effect plug-in in a track's chain: the track's sound goes in on its first input and comes back
    // from its first output. Any other buses (a side-chain, extra outputs) get silence and are ignored. The
    // module stays the caller's; this only runs its processor while it is in a chain.
    class PluginInsert final : public son::Effect
    {
    public:
        explicit PluginInsert(BridgeModule* module) : module_(module)
        {
            if (module_->component != nullptr)
            {
                module_->component->queryInterface(IAudioProcessor_iid, reinterpret_cast<void**>(&processor_));
            }
        }

        ~PluginInsert() override
        {
            Stop();
            if (processor_ != nullptr)
            {
                processor_->release();
            }
        }

        const void* Identity() const override { return module_; }

        void Prepare(double sampleRate, int maxBlock) override
        {
            Stop();
            if (processor_ == nullptr || maxBlock <= 0)
            {
                return;
            }

            auto* component = module_->component;
            const int32 inputBuses = component->getBusCount(kAudio, kInput);
            const int32 outputBuses = component->getBusCount(kAudio, kOutput);
            if (inputBuses < 1 || outputBuses < 1)
            {
                return; // an instrument, or something else with nothing to put the sound through
            }

            // Stereo in and out where the plug-in takes it; otherwise whatever it offers.
            std::vector<SpeakerArrangement> inArrangements(static_cast<size_t>(inputBuses), SpeakerArr::kStereo);
            std::vector<SpeakerArrangement> outArrangements(static_cast<size_t>(outputBuses), SpeakerArr::kStereo);
            for (int32 i = 1; i < inputBuses; ++i)
            {
                processor_->getBusArrangement(kInput, i, inArrangements[static_cast<size_t>(i)]);
            }
            for (int32 i = 1; i < outputBuses; ++i)
            {
                processor_->getBusArrangement(kOutput, i, outArrangements[static_cast<size_t>(i)]);
            }
            processor_->setBusArrangements(inArrangements.data(), inputBuses, outArrangements.data(), outputBuses);

            component->activateBus(kAudio, kInput, 0, 1);
            component->activateBus(kAudio, kOutput, 0, 1);
            busesActive_ = true;

            ProcessSetup setup{};
            setup.processMode = kRealtime;
            setup.symbolicSampleSize = kSample32;
            setup.maxSamplesPerBlock = maxBlock;
            setup.sampleRate = sampleRate;
            if (processor_->setupProcessing(setup) != kResultOk || component->setActive(1) != kResultOk)
            {
                return;
            }
            active_ = true;
            processor_->setProcessing(1); // some plug-ins answer kNotImplemented, and process all the same
            processing_ = true;

            // One buffer per channel of every bus, each as long as the largest block.
            SetUpBuses(kInput, inputBuses, maxBlock, inputs_, inputChannels_);
            SetUpBuses(kOutput, outputBuses, maxBlock, outputs_, outputChannels_);
            data_ = ProcessData{};
            data_.processMode = kRealtime;
            data_.symbolicSampleSize = kSample32;
            data_.numInputs = inputBuses;
            data_.inputs = inputs_.data();
            data_.numOutputs = outputBuses;
            data_.outputs = outputs_.data();
            maxBlock_ = maxBlock;
        }

        void Process(float** channels, int numChannels, int frames) override
        {
            if (!active_ || frames > maxBlock_ || numChannels <= 0)
            {
                return;
            }

            // The track's sound into the first input, silence into the others.
            for (size_t bus = 0; bus < inputs_.size(); ++bus)
            {
                for (int32 c = 0; c < inputs_[bus].numChannels; ++c)
                {
                    float* target = inputs_[bus].channelBuffers32[c];
                    if (bus == 0)
                    {
                        const float* source = channels[std::min<int>(c, numChannels - 1)];
                        std::copy(source, source + frames, target);
                    }
                    else
                    {
                        std::fill(target, target + frames, 0.0f);
                    }
                }
                inputs_[bus].silenceFlags = 0;
            }

            data_.numSamples = frames;
            if (processor_->process(data_) != kResultOk)
            {
                return; // the sound passes through as it came
            }

            // The first output back into the track; a mono output goes to both sides.
            const auto& output = outputs_[0];
            for (int c = 0; c < numChannels && output.numChannels > 0; ++c)
            {
                const float* source = output.channelBuffers32[std::min<int32>(c, output.numChannels - 1)];
                std::copy(source, source + frames, channels[c]);
            }
        }

    private:
        void SetUpBuses(BusDirection direction, int32 count, int maxBlock,
                        std::vector<AudioBusBuffers>& buses, std::vector<std::vector<float*>>& pointers)
        {
            buses.assign(static_cast<size_t>(count), AudioBusBuffers{});
            pointers.assign(static_cast<size_t>(count), {});
            for (int32 bus = 0; bus < count; ++bus)
            {
                BusInfo info{};
                int32 channelCount = 2;
                if (module_->component->getBusInfo(kAudio, direction, bus, info) == kResultOk && info.channelCount > 0)
                {
                    channelCount = info.channelCount;
                }
                auto& busPointers = pointers[static_cast<size_t>(bus)];
                for (int32 c = 0; c < channelCount; ++c)
                {
                    storage_.emplace_back(static_cast<size_t>(maxBlock), 0.0f);
                    busPointers.push_back(storage_.back().data());
                }
                buses[static_cast<size_t>(bus)].numChannels = channelCount;
                buses[static_cast<size_t>(bus)].channelBuffers32 = busPointers.data();
            }
        }

        void Stop()
        {
            if (processing_)
            {
                processor_->setProcessing(0);
                processing_ = false;
            }
            if (active_)
            {
                module_->component->setActive(0);
                active_ = false;
            }
            if (busesActive_)
            {
                module_->component->activateBus(kAudio, kInput, 0, 0);
                module_->component->activateBus(kAudio, kOutput, 0, 0);
                busesActive_ = false;
            }
            storage_.clear();
        }

        BridgeModule* module_;
        IAudioProcessor* processor_ = nullptr;
        bool busesActive_ = false;
        bool active_ = false;
        bool processing_ = false;
        int maxBlock_ = 0;
        std::vector<std::vector<float>> storage_;
        std::vector<AudioBusBuffers> inputs_;
        std::vector<AudioBusBuffers> outputs_;
        std::vector<std::vector<float*>> inputChannels_;
        std::vector<std::vector<float*>> outputChannels_;
        ProcessData data_{};
    };

    // Makes the effect for a slot of a chain: a plug-in insert when the slot names a module, else one of
    // Sonator's own.
    std::unique_ptr<son::Effect> MakeEffect(int32_t type, void* module)
    {
        if (type == static_cast<int32_t>(son::EffectType::Plugin))
        {
            return module != nullptr ? std::make_unique<PluginInsert>(static_cast<BridgeModule*>(module)) : nullptr;
        }
        return son::CreateEffect(static_cast<son::EffectType>(type));
    }

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
    // The note events handed to the plug-in with one block. The audio thread owns it; the host fills it per block.
    class BlockEventList final : public IEventList
    {
    public:
        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IEventList_iid))
            {
                *obj = static_cast<IEventList*>(this);
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        // Lifetime is the audio engine's, not reference counted.
        uint32 PLUGIN_API addRef() override { return 1; }
        uint32 PLUGIN_API release() override { return 1; }

        int32 PLUGIN_API getEventCount() override
        {
            return static_cast<int32>(events.size());
        }

        tresult PLUGIN_API getEvent(int32 index, Event& event) override
        {
            if (index < 0 || index >= static_cast<int32>(events.size()))
            {
                return kInvalidArgument;
            }

            event = events[static_cast<size_t>(index)];
            return kResultOk;
        }

        tresult PLUGIN_API addEvent(Event& /*event*/) override
        {
            return kResultFalse; // the plug-in does not write events back to the host yet
        }

        std::vector<Event> events;
    };

    // One parameter's changes within a block, here MIDI controllers mapped to the plug-in's parameters.
    class BlockParamQueue final : public IParamValueQueue
    {
    public:
        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IParamValueQueue_iid))
            {
                *obj = static_cast<IParamValueQueue*>(this);
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        uint32 PLUGIN_API addRef() override { return 1; }
        uint32 PLUGIN_API release() override { return 1; }

        ParamID PLUGIN_API getParameterId() override { return id; }
        int32 PLUGIN_API getPointCount() override { return count; }

        tresult PLUGIN_API getPoint(int32 index, int32& sampleOffset, ParamValue& value) override
        {
            if (index < 0 || index >= count)
            {
                return kInvalidArgument;
            }

            sampleOffset = offsets[index];
            value = values[index];
            return kResultOk;
        }

        tresult PLUGIN_API addPoint(int32 sampleOffset, ParamValue value, int32& index) override
        {
            if (count >= kMaxPoints)
            {
                return kResultFalse;
            }

            offsets[count] = sampleOffset;
            values[count] = value;
            index = count++;
            return kResultOk;
        }

        static constexpr int32 kMaxPoints = 16;
        ParamID id = 0;
        int32 count = 0;
        int32 offsets[kMaxPoints]{};
        ParamValue values[kMaxPoints]{};
    };

    // The parameter changes handed to a plug-in with one block. Its queues are made once, so the audio thread
    // never allocates.
    class BlockParameterChanges final : public IParameterChanges
    {
    public:
        tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
        {
            if (obj == nullptr)
            {
                return kInvalidArgument;
            }

            if (TuidEquals(iid, FUnknown_iid) || TuidEquals(iid, IParameterChanges_iid))
            {
                *obj = static_cast<IParameterChanges*>(this);
                return kResultOk;
            }

            *obj = nullptr;
            return kNoInterface;
        }

        uint32 PLUGIN_API addRef() override { return 1; }
        uint32 PLUGIN_API release() override { return 1; }

        int32 PLUGIN_API getParameterCount() override { return used; }

        IParamValueQueue* PLUGIN_API getParameterData(int32 index) override
        {
            return index >= 0 && index < used ? &queues[static_cast<size_t>(index)] : nullptr;
        }

        IParamValueQueue* PLUGIN_API addParameterData(const ParamID& id, int32& index) override
        {
            for (int32 i = 0; i < used; ++i)
            {
                if (queues[static_cast<size_t>(i)].id == id)
                {
                    index = i;
                    return &queues[static_cast<size_t>(i)];
                }
            }

            if (used >= kMaxQueues)
            {
                return nullptr;
            }

            auto& queue = queues[static_cast<size_t>(used)];
            queue.id = id;
            queue.count = 0;
            index = used++;
            return &queue;
        }

        void Clear() { used = 0; }

        static constexpr int32 kMaxQueues = 32;
        std::array<BlockParamQueue, kMaxQueues> queues{};
        int32 used = 0;
    };

    // A MIDI controller (or pitch bend, as controller 129) mapped to a plug-in parameter, waiting for the next block.
    struct ControllerChange
    {
        ParamID id;
        ParamValue value;
    };

    // One running plug-in in the mixer: its processor, the notes waiting for its next block, and the
    // buffers it renders into before its sound is added to the mix.
    struct MixerVoice
    {
        // Controllers waiting for the next block, guarded by midiMutex, and the block's changes. The plug-in's
        // own mapping of MIDI controllers to its parameters, by channel and controller (kNoParamId when none),
        // is read once when it starts.
        std::vector<ControllerChange> pendingControllers;
        std::vector<ControllerChange> blockControllers;
        BlockParameterChanges parameterChanges;
        std::vector<ParamID> controllerParams; // 16 channels x kCountCtrlNumber
        bool sustainHeld[16]{};

        void ReadControllerMapping()
        {
            controllerParams.assign(16 * kCountCtrlNumber, kNoParamId);
            IMidiMapping* mapping = nullptr;
            if (module->controller == nullptr
                || module->controller->queryInterface(IMidiMapping_iid, reinterpret_cast<void**>(&mapping)) != kResultOk
                || mapping == nullptr)
            {
                return;
            }

            for (int16 channel = 0; channel < 16; ++channel)
            {
                for (int16 controller = 0; controller < kCountCtrlNumber; ++controller)
                {
                    ParamID id = kNoParamId;
                    if (mapping->getMidiControllerAssignment(0, channel, static_cast<CtrlNumber>(controller), id) == kResultTrue)
                    {
                        controllerParams[static_cast<size_t>(channel * kCountCtrlNumber + controller)] = id;
                    }
                }
            }
            mapping->release();
        }

        // Hands the block's controllers to the plug-in as parameter changes at its start. Audio thread only.
        void PrepareControllers()
        {
            parameterChanges.Clear();
            for (const auto& change : blockControllers)
            {
                int32 index = 0;
                if (auto* queue = parameterChanges.addParameterData(change.id, index))
                {
                    int32 point = 0;
                    queue->addPoint(0, change.value, point);
                }
            }
            processData.inputParameterChanges = parameterChanges.used > 0 ? &parameterChanges : nullptr;
        }

        BridgeModule* module = nullptr;
        IAudioProcessor* processor = nullptr; // addRef'd; released by Deactivate

        // Note events queued by VstHostBridge_SendNoteEvent. The next block the audio thread processes takes them.
        std::mutex midiMutex;
        std::vector<Event> pendingMidi;
        BlockEventList blockEvents;

        // How many times each note is held now, by channel and pitch, so every one can be released at once,
        // and how many are held in all. Guarded by midiMutex.
        uint8_t held[16][128]{};
        int32 heldNotes = 0;

        // A plug-in with no note held whose sound has been silent for half a second is not run, as Cubase
        // does: it is asleep until a note or any other event arrives for it. Audio thread only.
        int64 silentFrames = 0;
        bool asleep = false;

        int32 channels = 2;
        std::vector<std::vector<float>> planar;
        std::vector<Sample32*> pointers;
        AudioBusBuffers outputBus{};
        ProcessData processData{};

        bool activatedOutputBus = false;
        bool activatedComponent = false;
        bool startedProcessing = false;

        // For an audio track's clip: where in the clip the next block starts, in the clip's frames, and how
        // much of the effects' tail is left to play once the clip has ended.
        bool isClip = false;
        double clipPosition = 0.0;
        int64 clipTailFrames = 0;

        // Prepares the plug-in, or the clip, to play blocks of up to maxBlock frames at sampleRate.
        bool Activate(int32 maxBlock, double sampleRate, std::string& error)
        {
            if (isClip)
            {
                channels = 2;
                clipPosition = module->clipStartSeconds * module->clipSampleRate;
                clipTailFrames = static_cast<int64>(sampleRate * 4.0); // reverb and echo ring out after the clip
                planar.assign(2, std::vector<float>(static_cast<size_t>(maxBlock)));
                pointers.assign(2, nullptr);
                return true;
            }

            BusInfo outBusInfo{};
            channels = 2;
            if (module->component->getBusInfo(kAudio, kOutput, 0, outBusInfo) == kResultOk && outBusInfo.channelCount > 0)
            {
                channels = outBusInfo.channelCount;
            }

            if (module->component->activateBus(kAudio, kOutput, 0, 1) == kResultOk)
            {
                activatedOutputBus = true;
            }

            // Instruments take note events on their event input bus.
            if (module->component->getBusCount(kEvent, kInput) > 0)
            {
                module->component->activateBus(kEvent, kInput, 0, 1);
            }

            ProcessSetup setup{};
            setup.processMode = kRealtime;
            setup.symbolicSampleSize = kSample32;
            setup.maxSamplesPerBlock = maxBlock;
            setup.sampleRate = sampleRate;
            if (processor->setupProcessing(setup) != kResultOk)
            {
                error = "IAudioProcessor::setupProcessing() fallita.";
                return false;
            }
            if (module->component->setActive(1) != kResultOk)
            {
                error = "IComponent::setActive(true) fallita.";
                return false;
            }
            activatedComponent = true;
            if (processor->setProcessing(1) != kResultOk)
            {
                error = "IAudioProcessor::setProcessing(true) fallita.";
                return false;
            }
            startedProcessing = true;
            ReadControllerMapping();
            pendingControllers.reserve(256);
            blockControllers.reserve(256);

            planar.assign(static_cast<size_t>(channels), std::vector<float>(static_cast<size_t>(maxBlock)));
            pointers.assign(static_cast<size_t>(channels), nullptr);
            outputBus.numChannels = channels;
            outputBus.silenceFlags = 0;
            processData.processMode = kRealtime;
            processData.symbolicSampleSize = kSample32;
            processData.numInputs = 0;
            processData.inputs = nullptr;
            processData.numOutputs = 1;
            processData.outputs = &outputBus;
            return true;
        }

        // Undoes Activate, as far as it got. The voice must be out of the mix.
        void Deactivate()
        {
            if (startedProcessing)
            {
                processor->setProcessing(0);
                startedProcessing = false;
            }
            if (activatedComponent)
            {
                module->component->setActive(0);
                activatedComponent = false;
            }
            if (activatedOutputBus)
            {
                module->component->activateBus(kAudio, kOutput, 0, 0);
                activatedOutputBus = false;
            }
        }

        ~MixerVoice()
        {
            Deactivate();
            if (processor != nullptr)
            {
                processor->release();
            }
        }

        // Renders one block of the plug-in through its effects into its own buffers. One thread at a time.
        void Render(int32 frames, double sampleRate)
        {
            bool notesHeld = false;
            {
                std::lock_guard<std::mutex> lock(midiMutex);
                blockEvents.events.swap(pendingMidi);
                pendingMidi.clear();
                blockControllers.swap(pendingControllers);
                pendingControllers.clear();
                notesHeld = heldNotes > 0 || std::any_of(std::begin(sustainHeld), std::end(sustainHeld), [](bool held) { return held; });
            }
            processData.inputEvents = &blockEvents;

            if (isClip)
            {
                RenderClip(frames, sampleRate);
                return;
            }

            PrepareControllers();
            const bool hasEvents = !blockEvents.events.empty() || !blockControllers.empty();
            if (asleep && !hasEvents)
            {
                return; // silent: AddTo leaves it out of the mix
            }
            asleep = false;


            for (int32 ch = 0; ch < channels; ++ch)
            {
                pointers[static_cast<size_t>(ch)] = planar[static_cast<size_t>(ch)].data();
            }
            outputBus.channelBuffers32 = pointers.data();
            processData.numSamples = frames;
            processor->process(processData);
            ApplyEffects(module, pointers.data(), channels, frames, sampleRate);

            // Silent after its effects, with no note held and none just played: it may go to sleep. About
            // -100 dBFS counts as silence, well below anything heard.
            constexpr float kSilence = 1.0e-5f;
            bool silent = !notesHeld && !hasEvents;
            for (int32 ch = 0; silent && ch < channels; ++ch)
            {
                const float* samples = planar[static_cast<size_t>(ch)].data();
                for (int32 i = 0; i < frames; ++i)
                {
                    if (std::fabs(samples[i]) > kSilence)
                    {
                        silent = false;
                        break;
                    }
                }
            }

            silentFrames = silent ? silentFrames + frames : 0;
            if (silentFrames >= static_cast<int64>(sampleRate * 0.5))
            {
                asleep = true;
                silentFrames = 0;
            }
        }

        // Reads the next block of the clip, at the output's sample rate (reading between the clip's samples when
        // its rate is another), through the track's effects. After the end, the effects' tail plays out, then
        // the clip goes to sleep for good.
        void RenderClip(int32 frames, double sampleRate)
        {
            if (asleep)
            {
                return;
            }

            const auto& samples = module->clipSamples;
            const int32 clipChannels = module->clipChannels;
            const int64 clipFrames = clipChannels > 0 ? static_cast<int64>(samples.size()) / clipChannels : 0;
            const double step = module->clipSampleRate / sampleRate;
            float* left = planar[0].data();
            float* right = planar[1].data();
            for (int32 i = 0; i < frames; ++i)
            {
                const auto frame = static_cast<int64>(clipPosition);
                if (frame + 1 < clipFrames)
                {
                    const auto fraction = static_cast<float>(clipPosition - static_cast<double>(frame));
                    const float* a = &samples[static_cast<size_t>(frame * clipChannels)];
                    const float* b = a + clipChannels;
                    left[i] = a[0] + (b[0] - a[0]) * fraction;
                    right[i] = clipChannels > 1 ? a[1] + (b[1] - a[1]) * fraction : left[i];
                }
                else
                {
                    left[i] = 0.0f;
                    right[i] = 0.0f;
                }
                clipPosition += step;
            }

            pointers[0] = left;
            pointers[1] = right;
            ApplyEffects(module, pointers.data(), 2, frames, sampleRate);

            if (static_cast<int64>(clipPosition) >= clipFrames)
            {
                clipTailFrames -= frames;
                if (clipTailFrames <= 0)
                {
                    asleep = true;
                }
            }
        }

        // Adds the block last rendered to the stereo mix. A plug-in asleep adds nothing.
        void AddTo(float* left, float* right, int32 frames) const
        {
            if (asleep)
            {
                return;
            }

            const float* sourceLeft = planar[0].data();
            const float* sourceRight = channels > 1 ? planar[1].data() : sourceLeft;
            for (int32 i = 0; i < frames; ++i)
            {
                left[i] += sourceLeft[i];
                right[i] += sourceRight[i];
            }
        }
    };

    // The voices playing now. The audio thread takes the list without waiting (see MixVoices), and a voice
    // taken out stays alive until the audio thread has let go of it.
    std::mutex g_voicesMutex;
    std::shared_ptr<const std::vector<std::shared_ptr<MixerVoice>>> g_voices;

    // The audio thread's own copy of the list, so a block never waits for the list to be changed.
    std::shared_ptr<const std::vector<std::shared_ptr<MixerVoice>>> g_audioThreadVoices;

    // Makes the calling thread one the system schedules for audio (the "Pro Audio" class of the multimedia
    // scheduler, where Windows has it), and flushes tiny floating-point values to zero, which would
    // otherwise slow down effects and plug-ins as their sound fades out.
    void PrepareAudioThread()
    {
        _mm_setcsr(_mm_getcsr() | 0x8040); // flush-to-zero and denormals-are-zero
        using AvSetFn = HANDLE(WINAPI*)(LPCWSTR, LPDWORD);
        static const auto avSet = []() -> AvSetFn
        {
            HMODULE avrt = LoadLibraryW(L"avrt.dll");
            return avrt != nullptr
                ? reinterpret_cast<AvSetFn>(reinterpret_cast<void*>(GetProcAddress(avrt, "AvSetMmThreadCharacteristicsW")))
                : nullptr;
        }();
        DWORD taskIndex = 0;
        if (avSet == nullptr || avSet(L"Pro Audio", &taskIndex) == nullptr)
        {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        }
    }

    // Renders the plug-ins of one block on several cores at once. The audio thread hands out the block's
    // voices; worker threads and the audio thread itself take them one at a time until none is left, and the
    // audio thread waits for the last to finish before mixing. The block then takes about as long as its
    // slowest plug-in rather than all of them one after another.
    class RenderPool
    {
    public:
        RenderPool()
        {
            const unsigned cores = std::thread::hardware_concurrency();
            const int workers = static_cast<int>(std::clamp(cores > 1 ? cores - 1 : 1u, 1u, 7u));
            wake_ = CreateSemaphoreW(nullptr, 0, 1 << 20, nullptr);
            for (int i = 0; i < workers; ++i)
            {
                std::thread([this] { WorkerLoop(); }).detach();
            }
            workerCount_ = workers;
        }

        // Renders every voice of the block. Audio thread only.
        void Render(MixerVoice* const* voices, int32 count, int32 frames, double sampleRate)
        {
            tasks_ = voices;
            frames_ = frames;
            sampleRate_ = sampleRate;
            remaining_.store(count, std::memory_order_relaxed);

            // The block's number, its voice count and the next voice to take share one atomic value, so a
            // worker that takes a voice always sees the count of the same block.
            ++epoch_;
            work_.store((epoch_ << 32) | (static_cast<uint64_t>(count) << 16), std::memory_order_release);
            ReleaseSemaphore(wake_, std::min<LONG>(workerCount_, count - 1), nullptr);

            TakeVoices();
            while (remaining_.load(std::memory_order_acquire) > 0)
            {
                _mm_pause();
            }
        }

    private:
        void WorkerLoop()
        {
            PrepareAudioThread();
            for (;;)
            {
                WaitForSingleObject(wake_, INFINITE);
                TakeVoices();
            }
        }

        // Takes voices of the current block until none is left. A voice taken belongs to a block that is still
        // waiting for it, so the block's voices and format stay as they are until it is done.
        void TakeVoices()
        {
            for (;;)
            {
                const uint64_t ticket = work_.fetch_add(1, std::memory_order_acq_rel);
                const auto index = static_cast<int32>(ticket & 0xFFFF);
                const auto count = static_cast<int32>((ticket >> 16) & 0xFFFF);
                if (index >= count)
                {
                    return;
                }

                tasks_[index]->Render(frames_, sampleRate_);
                remaining_.fetch_sub(1, std::memory_order_acq_rel);
            }
        }

        HANDLE wake_ = nullptr;
        int workerCount_ = 0;
        uint64_t epoch_ = 0;
        std::atomic<uint64_t> work_{0};
        std::atomic<int32> remaining_{0};
        MixerVoice* const* tasks_ = nullptr;
        int32 frames_ = 0;
        double sampleRate_ = 0.0;
    };

    // A recording of the mix as it goes out, for exporting the song: a stereo buffer made before it starts,
    // so the audio thread only copies into it, and the frames written so far.
    struct MixCapture
    {
        std::vector<float> samples; // interleaved left and right
        std::atomic<int64> frames{0};
        std::atomic<bool> recording{false};
        std::atomic<double> sampleRate{0.0};
    };

    MixCapture g_capture;

    void CaptureBlock(const float* left, const float* right, int32 frames, double sampleRate)
    {
        if (!g_capture.recording.load(std::memory_order_acquire))
        {
            return;
        }

        g_capture.sampleRate.store(sampleRate, std::memory_order_relaxed);
        const auto start = g_capture.frames.load(std::memory_order_relaxed);
        const auto room = static_cast<int64>(g_capture.samples.size() / 2) - start;
        const auto count = static_cast<int64>(frames) < room ? static_cast<int64>(frames) : room;
        for (int64 i = 0; i < count; ++i)
        {
            g_capture.samples[static_cast<size_t>((start + i) * 2)] = left[i];
            g_capture.samples[static_cast<size_t>((start + i) * 2 + 1)] = right[i];
        }
        g_capture.frames.store(start + (count > 0 ? count : 0), std::memory_order_release);
    }

    void MixVoicesInto(float* left, float* right, int32 frames, double sampleRate);

    // The master's effects, run on the summed mix. The audio thread takes its own reference under the
    // lock (without waiting), as for a track's chain.
    std::mutex g_masterEffectsMutex;
    std::shared_ptr<son::EffectChain> g_masterEffects;

    void ApplyMasterEffects(float* left, float* right, int32 frames, double sampleRate)
    {
        std::shared_ptr<son::EffectChain> chain;
        {
            std::unique_lock<std::mutex> lock(g_masterEffectsMutex, std::try_to_lock);
            if (lock.owns_lock())
            {
                chain = g_masterEffects;
            }
        }
        if (chain != nullptr)
        {
            float* channels[2] = {left, right};
            chain->Process(channels, 2, frames, sampleRate);
        }
    }

    // Sums every running plug-in into one stereo block, runs the master's effects on it, and records it
    // when the song is being exported. Called by the audio thread for each block.
    void MixVoices(float* left, float* right, int32 frames, double sampleRate)
    {
        MixVoicesInto(left, right, frames, sampleRate);
        ApplyMasterEffects(left, right, frames, sampleRate);
        CaptureBlock(left, right, frames, sampleRate);
    }

    void MixVoicesInto(float* left, float* right, int32 frames, double sampleRate)
    {
        {
            std::unique_lock<std::mutex> lock(g_voicesMutex, std::try_to_lock);
            if (lock.owns_lock())
            {
                g_audioThreadVoices = g_voices;
            }
        }

        std::fill(left, left + frames, 0.0f);
        std::fill(right, right + frames, 0.0f);
        if (g_audioThreadVoices == nullptr || g_audioThreadVoices->empty())
        {
            return;
        }

        const auto& voices = *g_audioThreadVoices;
        if (voices.size() == 1)
        {
            voices[0]->Render(frames, sampleRate);
        }
        else
        {
            // Made on the first block that has two plug-ins, and kept: the workers sleep when there is no work.
            static RenderPool pool;
            static std::vector<MixerVoice*> tasks;
            tasks.clear();
            for (const auto& voice : voices)
            {
                tasks.push_back(voice.get());
            }
            pool.Render(tasks.data(), static_cast<int32>(tasks.size()), frames, sampleRate);
        }

        for (const auto& voice : voices)
        {
            voice->AddTo(left, right, frames);
        }
    }

    struct AudioEngineState
    {
        // The largest block and the sample rate the device runs at, set once it has started.
        int32 maxBlock = 0;
        double sampleRate = 0.0;

        // Endpoint ID of the output device to open. Empty means the default device.
        std::wstring deviceId;

        // True when this engine drives an ASIO driver rather than WASAPI.
        bool useAsio = false;

        // ASIO format chosen by the user. 0 means the driver's own choice.
        int32 requestedBufferFrames = 0;
        double requestedSampleRate = 0.0;

        // ASIO timing, set once the driver's buffers exist and cleared when the engine stops.
        std::atomic<long> asioBufferFrames{0};
        std::atomic<long> asioOutputLatencyFrames{0};
        std::atomic<double> asioSampleRate{0.0};
        std::atomic<bool> asioHasInput{false};

        // Loopback measurement. The ASIO callback writes a click on output 1 and waits for it on input 1.
        std::atomic<bool> measureRequested{false};
        std::atomic<bool> measureFinished{false};
        std::atomic<long long> measureResultFrames{-1};
        std::atomic<float> measurePeak{0.0f}; // largest input 1 level during the last measurement
        std::atomic<long long> measurePeakFrames{0}; // frames from the click to that peak

        std::thread thread;
        std::atomic<bool> stopRequested{false};

        // 0 = still starting up, 1 = started OK, -1 = failed to start.
        std::atomic<int> initResult{0};
        std::mutex initErrorMutex;
        std::string initError;

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
        PrepareAudioThread();
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

        const int32 maxBlockSize = static_cast<int32>(bufferFrameCount) > 0 ? static_cast<int32>(bufferFrameCount) : 1024;

        if (engine->initResult == 0 && FAILED(hr = client->Start()))
        {
            fail("IAudioClient::Start() fallita.");
        }

        if (engine->initResult == 0)
        {
            engine->maxBlock = maxBlockSize;
            engine->sampleRate = static_cast<double>(mixFormat->nSamplesPerSec);
            engine->initResult = 1; // signal success to StartAudioDevice
        }

        if (engine->initResult == 1)
        {
            const int32 deviceChannels = mixFormat->nChannels;
            std::vector<float> mixLeft(static_cast<size_t>(maxBlockSize));
            std::vector<float> mixRight(static_cast<size_t>(maxBlockSize));

            const double deviceRate = static_cast<double>(mixFormat->nSamplesPerSec);
            bool deviceFed = false;
            while (!engine->stopRequested.load())
            {
                UINT32 padding = 0;
                if (FAILED(client->GetCurrentPadding(&padding)))
                {
                    std::this_thread::sleep_for(std::chrono::milliseconds(5));
                    continue;
                }

                // The device played everything it had before the next block arrived.
                if (padding == 0 && deviceFed)
                {
                    RecordDspDropout();
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

                const long long blockStart = QpcNow();

                MixVoices(mixLeft.data(), mixRight.data(), samplesThisBlock, deviceRate);

                // A mono device gets the left side; a device with more than two channels hears the mix on the first two.
                auto* deviceSamples = reinterpret_cast<float*>(deviceBuffer);
                for (int32 frame = 0; frame < samplesThisBlock; ++frame)
                {
                    for (int32 devCh = 0; devCh < deviceChannels; ++devCh)
                    {
                        float value = 0.0f;
                        if (devCh == 0)
                        {
                            value = mixLeft[static_cast<size_t>(frame)];
                        }
                        else if (devCh == 1)
                        {
                            value = mixRight[static_cast<size_t>(frame)];
                        }
                        deviceSamples[frame * deviceChannels + devCh] = value;
                    }
                }

                renderClient->ReleaseBuffer(static_cast<UINT32>(samplesThisBlock), 0);
                RecordDspBlock(QpcSeconds(QpcNow() - blockStart), samplesThisBlock / deviceRate);
                deviceFed = true;
            }

            client->Stop();
        }

        // Let go of the voices, so the ones taken out of the mix can be released.
        g_audioThreadVoices.reset();

        if (renderClient != nullptr) renderClient->Release();
        if (client != nullptr) client->Release();
        if (mixFormat != nullptr) CoTaskMemFree(mixFormat);
        if (device != nullptr) device->Release();
        if (enumerator != nullptr) enumerator->Release();

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

    // The one audio output every running plug-in plays through, and the lock held while plug-ins join or
    // leave it. The output opens with the first plug-in and closes after the last.
    std::mutex g_mixerMutex;
    AudioEngineState* g_engine = nullptr;

    std::vector<std::shared_ptr<MixerVoice>> CurrentVoices()
    {
        std::lock_guard<std::mutex> lock(g_voicesMutex);
        return g_voices != nullptr ? *g_voices : std::vector<std::shared_ptr<MixerVoice>>{};
    }

    void PublishVoices(std::vector<std::shared_ptr<MixerVoice>> voices)
    {
        auto published = std::make_shared<const std::vector<std::shared_ptr<MixerVoice>>>(std::move(voices));
        std::lock_guard<std::mutex> lock(g_voicesMutex);
        g_voices = std::move(published);
    }

    // Stops the audio output and waits for its thread to finish. Called under g_mixerMutex.
    void StopAudioDevice()
    {
        if (g_engine == nullptr)
        {
            return;
        }

        g_engine->stopRequested = true;
        if (g_engine->thread.joinable())
        {
            g_engine->thread.join();
        }

        delete g_engine;
        g_engine = nullptr;
    }

    // Waits until the audio thread no longer holds the voice, at most a second.
    void WaitUntilReleased(const std::shared_ptr<MixerVoice>& voice)
    {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
        while (voice.use_count() > 1 && std::chrono::steady_clock::now() < deadline)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    // Takes the module's plug-in out of the mix and stops it, if it is playing. The output closes when no
    // plug-in is left. Blocks until the audio thread has let go of it.
    void StopAudioInternal(BridgeModule* module)
    {
        if (module == nullptr)
        {
            return;
        }

        std::lock_guard<std::mutex> mixerLock(g_mixerMutex);
        std::shared_ptr<MixerVoice> voice;
        {
            std::lock_guard<std::mutex> lock(module->voiceMutex);
            voice.swap(module->voice);
        }
        if (voice == nullptr)
        {
            return;
        }

        auto voices = CurrentVoices();
        voices.erase(std::remove(voices.begin(), voices.end(), voice), voices.end());
        const bool lastOne = voices.empty();
        PublishVoices(std::move(voices));
        if (lastOne)
        {
            StopAudioDevice();
        }

        WaitUntilReleased(voice);
        voice->Deactivate();
    }

    // The output the module's plug-in plays through, or null when it is not playing.
    AudioEngineState* RunningOutputFor(BridgeModule* module)
    {
        {
            std::lock_guard<std::mutex> lock(module->voiceMutex);
            if (module->voice == nullptr)
            {
                return nullptr;
            }
        }

        std::lock_guard<std::mutex> lock(g_mixerMutex);
        return g_engine;
    }
}

int VstHostBridge_GetVersion()
{
    return 2;
}

int VstHostBridge_GetDspLoad(double* outPeakLoad, double* outAverageLoad, int64_t* outDropouts)
{
    const auto peak = g_dspMeter.peakPermille.exchange(0);
    const auto busy = g_dspMeter.busyMicros.exchange(0);
    const auto period = g_dspMeter.periodMicros.exchange(0);
    if (outPeakLoad != nullptr)
    {
        *outPeakLoad = peak / 1000.0;
    }
    if (outAverageLoad != nullptr)
    {
        *outAverageLoad = period > 0 ? static_cast<double>(busy) / static_cast<double>(period) : 0.0;
    }
    if (outDropouts != nullptr)
    {
        *outDropouts = g_dspMeter.dropouts.load();
    }
    return period > 0 ? 1 : 0;
}

void VstHostBridge_ResetDropouts()
{
    g_dspMeter.dropouts = 0;
}

// An effect chain handle is a heap-held shared_ptr, so a module it is attached to keeps the chain alive.
void* VstHostBridge_CreateEffectChain()
{
    return new std::shared_ptr<son::EffectChain>(std::make_shared<son::EffectChain>());
}

void VstHostBridge_DestroyEffectChain(void* chainHandle)
{
    delete static_cast<std::shared_ptr<son::EffectChain>*>(chainHandle);
}

static son::EffectChain* ChainOf(void* chainHandle)
{
    return chainHandle == nullptr ? nullptr : static_cast<std::shared_ptr<son::EffectChain>*>(chainHandle)->get();
}

void VstHostBridge_SetEffectChainLayout(void* chainHandle, const int32_t* types, int32_t count)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->SetLayout(types, count);
    }
}

void VstHostBridge_SetEffectChainLayoutWithPlugins(void* chainHandle, const int32_t* types, void* const* modules, int32_t count)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->SetLayout(types, modules, count, MakeEffect);
    }
}

int32_t VstHostBridge_GetPluginKind(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    const auto& categories = module->subCategories;
    if (categories.find("Instrument") != std::string::npos)
    {
        return 1;
    }
    if (categories.find("Fx") != std::string::npos)
    {
        return 2;
    }

    // No categories: an instrument takes notes and makes sound; an effect takes sound in.
    auto* component = module->component;
    if (component == nullptr)
    {
        return 0;
    }
    if (component->getBusCount(kEvent, kInput) > 0)
    {
        return 1;
    }
    return component->getBusCount(kAudio, kInput) > 0 ? 2 : 0;
}

void VstHostBridge_SetEffectParameter(void* chainHandle, int32_t slot, int32_t parameter, double value)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->SetParameter(slot, parameter, value);
    }
}

void VstHostBridge_SetEffectEnabled(void* chainHandle, int32_t slot, int32_t enabled)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->SetEnabled(slot, enabled != 0);
    }
}

void VstHostBridge_SetEffectChainGain(void* chainHandle, double gain)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->gain.store(static_cast<float>(gain));
    }
}

void VstHostBridge_SetEffectChainPan(void* chainHandle, double pan)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->pan.store(static_cast<float>(pan));
    }
}

void VstHostBridge_SetMasterEffectChain(void* chainHandle)
{
    std::shared_ptr<son::EffectChain> chain;
    if (chainHandle != nullptr)
    {
        chain = *static_cast<std::shared_ptr<son::EffectChain>*>(chainHandle);
    }

    std::lock_guard<std::mutex> lock(g_masterEffectsMutex);
    g_masterEffects.swap(chain);
}

double VstHostBridge_GetEffectMeter(void* chainHandle, int32_t slot)
{
    auto* chain = ChainOf(chainHandle);
    return chain == nullptr ? 0.0 : chain->Meter(slot);
}

int VstHostBridge_TakeEffectLevels(void* chainHandle, int32_t slot, double* outInputPeak, double* outOutputPeak)
{
    float input = 0.0f;
    float output = 0.0f;
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->TakeLevels(slot, input, output);
    }
    if (outInputPeak != nullptr)
    {
        *outInputPeak = input;
    }
    if (outOutputPeak != nullptr)
    {
        *outOutputPeak = output;
    }
    return 1;
}

double VstHostBridge_TakeEffectChainPeak(void* chainHandle)
{
    auto* chain = ChainOf(chainHandle);
    return chain == nullptr ? 0.0 : chain->TakePeak();
}

void VstHostBridge_ProcessEffectChain(void* chainHandle, float* interleaved, int32_t frames, int32_t channels, double sampleRate)
{
    if (auto* chain = ChainOf(chainHandle))
    {
        chain->ProcessInterleaved(interleaved, frames, channels, sampleRate);
    }
}

int VstHostBridge_AttachEffectChain(void* moduleHandle, void* chainHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    std::shared_ptr<son::EffectChain> chain;
    if (chainHandle != nullptr)
    {
        chain = *static_cast<std::shared_ptr<son::EffectChain>*>(chainHandle);
    }

    std::lock_guard<std::mutex> lock(module->effectsMutex);
    module->effects.swap(chain);
    return 1;
}

int32_t VstHostBridge_SaveState(void* moduleHandle, int32_t part)
{
    if (moduleHandle == nullptr)
    {
        return -1;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    module->savedState.clear();
    MemoryIBStream stream;
    tresult result = kResultFalse;
    if (part == 0 && module->component != nullptr)
    {
        result = module->component->getState(&stream);
    }
    else if (part == 1 && module->controller != nullptr && !module->controllerIsComponent)
    {
        result = module->controller->getState(&stream);
    }

    if (result != kResultOk)
    {
        return -1;
    }

    module->savedState = stream.bytes();
    return static_cast<int32_t>(module->savedState.size());
}

int VstHostBridge_ReadSavedState(void* moduleHandle, uint8_t* buffer, int32_t capacity)
{
    if (moduleHandle == nullptr || buffer == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    if (capacity < static_cast<int32_t>(module->savedState.size()))
    {
        return 0;
    }

    std::memcpy(buffer, module->savedState.data(), module->savedState.size());
    module->savedState.clear();
    module->savedState.shrink_to_fit();
    return 1;
}

int VstHostBridge_LoadState(void* moduleHandle, int32_t part, const uint8_t* data, int32_t size)
{
    if (moduleHandle == nullptr || data == nullptr || size <= 0)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    MemoryIBStream stream(data, static_cast<size_t>(size));
    if (part == 0 && module->component != nullptr)
    {
        if (module->component->setState(&stream) != kResultOk)
        {
            return 0;
        }

        // The controller follows the processor's state, as when the plug-in was loaded.
        if (module->controller != nullptr && !module->controllerIsComponent)
        {
            stream.rewind();
            module->controller->setComponentState(&stream);
        }
        return 1;
    }

    if (part == 1 && module->controller != nullptr && !module->controllerIsComponent)
    {
        return module->controller->setState(&stream) == kResultOk ? 1 : 0;
    }

    return 0;
}

void* VstHostBridge_CreateClipModule(const float* interleaved, int32_t frames, int32_t channels, double sampleRate, double startSeconds)
{
    if (interleaved == nullptr || frames <= 0 || channels <= 0 || sampleRate <= 0.0)
    {
        SetBridgeError("Clip audio non valido.");
        return nullptr;
    }

    auto* module = new BridgeModule();
    module->pluginName = "Audio clip";
    module->clipSamples.assign(interleaved, interleaved + static_cast<size_t>(frames) * static_cast<size_t>(channels));
    module->clipChannels = channels;
    module->clipSampleRate = sampleRate;
    module->clipStartSeconds = startSeconds > 0.0 ? startSeconds : 0.0;
    return module;
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

    // VST3 modules on Windows may export InitDll and ExitDll. The host must call InitDll right after
    // loading, before anything else: plug-ins built on the Steinberg SDK set up their module there and
    // crash on their first call without it.
    auto initDll = reinterpret_cast<ModuleEntryProc>(GetProcAddress(hmodule, "InitDll"));
    auto exitDll = reinterpret_cast<ModuleEntryProc>(GetProcAddress(hmodule, "ExitDll"));
    if (initDll != nullptr && !initDll())
    {
        SetBridgeError("InitDll() del plug-in non e' riuscita (" + Narrow(binaryPath.wstring()) + ").");
        FreeLibrary(hmodule);
        return nullptr;
    }
    if (initDll == nullptr)
    {
        exitDll = nullptr; // ExitDll only pairs with a successful InitDll
    }

    auto getFactoryProc = reinterpret_cast<GetFactoryProc>(GetProcAddress(hmodule, "GetPluginFactory"));
    if (getFactoryProc == nullptr)
    {
        SetBridgeError(
            "Il modulo si carica ma non esporta \"GetPluginFactory\": non e' un plug-in VST3 valido ("
            + Narrow(binaryPath.wstring()) + ").");
        ReleaseLibrary(hmodule, exitDll);
        return nullptr;
    }

    IPluginFactory* factory = getFactoryProc();
    if (factory == nullptr)
    {
        SetBridgeError("GetPluginFactory() ha restituito nullptr.");
        ReleaseLibrary(hmodule, exitDll);
        return nullptr;
    }

    // Find the first "Audio Module Class" (the actual instrument/effect).
    TUID componentCid{};
    std::string pluginName;
    std::string subCategories;
    bool foundComponentClass = false;
    IPluginFactory2* factory2 = nullptr;
    factory->queryInterface(IPluginFactory2_iid, reinterpret_cast<void**>(&factory2));
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

            PClassInfo2 info2{};
            if (factory2 != nullptr && factory2->getClassInfo2(i, &info2) == kResultOk)
            {
                subCategories.assign(info2.subCategories, strnlen(info2.subCategories, sizeof(info2.subCategories)));
            }
        }
    }
    if (factory2 != nullptr)
    {
        factory2->release();
    }

    if (!foundComponentClass)
    {
        SetBridgeError("Nessuna classe \"Audio Module Class\" trovata nel factory VST3 di " + Narrow(binaryPath.wstring()) + ".");
        factory->release();
        ReleaseLibrary(hmodule, exitDll);
        return nullptr;
    }

    IComponent* component = nullptr;
    if (factory->createInstance(componentCid, IComponent_iid, reinterpret_cast<void**>(&component)) != kResultOk || component == nullptr)
    {
        SetBridgeError("createInstance(IComponent) fallita per la classe audio effect del plug-in.");
        factory->release();
        ReleaseLibrary(hmodule, exitDll);
        return nullptr;
    }

    if (component->initialize(&HostApplicationInstance()) != kResultOk)
    {
        SetBridgeError("IComponent::initialize() fallita.");
        component->release();
        factory->release();
        ReleaseLibrary(hmodule, exitDll);
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
    module->subCategories = subCategories;
    module->exitDll = exitDll;
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

    // GetPluginFactory hands the host a reference to the factory; it is released before the module
    // is told to exit, then the DLL is freed.
    if (module->factory != nullptr)
    {
        module->factory->release();
    }

    if (module->hmodule != nullptr)
    {
        ReleaseLibrary(module->hmodule, module->exitDll);
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

// The DPI a window is drawn at: GetDpiForWindow where Windows has it (10, version 1607 on), else the
// screen's DPI.
static UINT WindowDpi(HWND window)
{
    using GetDpiForWindowFn = UINT(WINAPI*)(HWND);
    static const auto getDpiForWindow = reinterpret_cast<GetDpiForWindowFn>(
        reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow")));
    if (getDpiForWindow != nullptr)
    {
        const UINT dpi = getDpiForWindow(window);
        if (dpi != 0)
        {
            return dpi;
        }
    }

    HDC screen = GetDC(nullptr);
    const int dpi = screen != nullptr ? GetDeviceCaps(screen, LOGPIXELSY) : USER_DEFAULT_SCREEN_DPI;
    if (screen != nullptr)
    {
        ReleaseDC(nullptr, screen);
    }
    return dpi > 0 ? static_cast<UINT>(dpi) : USER_DEFAULT_SCREEN_DPI;
}

// Tells the editor the display's scale. False when the plug-in cannot be told it.
static bool TellContentScale(IPlugView* view, double scale)
{
    IPlugViewContentScaleSupport* scaleSupport = nullptr;
    if (view->queryInterface(IPlugViewContentScaleSupport_iid, reinterpret_cast<void**>(&scaleSupport)) != kResultOk
        || scaleSupport == nullptr)
    {
        return false;
    }

    scaleSupport->setContentScaleFactor(static_cast<IPlugViewContentScaleSupport::ScaleFactor>(scale));
    scaleSupport->release();
    return true;
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

    // On a scaled display (such as 200% on a 4K screen) the editor would draw at its 100% size in real
    // pixels and look tiny. Plug-ins that can scale themselves are told the display's scale before they
    // report their size, so they draw at the same size as the rest of Sonator.
    const UINT parentDpi = WindowDpi(static_cast<HWND>(parentHwnd));
    const double displayScale = static_cast<double>(parentDpi) / USER_DEFAULT_SCREEN_DPI;
    const bool scaledByPlugin = displayScale > 1.0 && TellContentScale(view, displayScale);

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

    // Known before attached(): a plug-in may ask to resize itself from inside it, through the frame.
    module->editorContainerHwnd = container;
    module->editorWidth = width;
    module->editorHeight = height;

    if (view->attached(container, kPlatformTypeHWND) != kResultOk)
    {
        SetBridgeError("IPlugView::attached() fallita: il plug-in non ha potuto agganciarsi alla finestra host.");
        module->editorContainerHwnd = nullptr;
        module->editorWidth = 0;
        module->editorHeight = 0;
        view->setFrame(nullptr);
        frame->release();
        DestroyWindow(container);
        view->release();
        return 0;
    }

    module->editorView = view;
    module->editorFrame = frame;

    if (scaledByPlugin)
    {
        // Some plug-ins only take the scale once they are attached: tell them again.
        TellContentScale(view, displayScale);
    }

    // Some plug-ins only know their real size once attached: take it again, and fit the container to it.
    ViewRect attachedRect{};
    if (view->getSize(&attachedRect) == kResultOk && attachedRect.getWidth() > 0 && attachedRect.getHeight() > 0
        && (attachedRect.getWidth() != module->editorWidth || attachedRect.getHeight() != module->editorHeight))
    {
        module->editorWidth = attachedRect.getWidth();
        module->editorHeight = attachedRect.getHeight();
        SetWindowPos(container, nullptr, 0, 0, module->editorWidth, module->editorHeight, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
    width = module->editorWidth;
    height = module->editorHeight;

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
        int32 pluginChannels = 2; // channels of the mix: always stereo
        std::vector<ASIOBufferInfo> bufferInfos;
        std::vector<std::vector<float>> planar;
        std::vector<float> silence;

        // Input channel 0, read for the loopback measurement only.
        bool hasInput = false;
        ASIOSampleType inputSampleType = ASIOSTFloat32LSB;
        std::vector<float> inputScratch;
        long long frame = 0;          // absolute frame of the first sample of the next block
        long long clickFrame = -1;    // frame where the measurement click is written
        long long detectedFrame = -1; // frame where the click came back on input 0
        float inputPeak = 0.0f;       // largest input 0 level seen during the current measurement
        long long inputPeakFrame = 0; // absolute frame where that level was seen
        float noisePeak = 0.0f;       // input 0 level just before the burst went out
        double sampleRate = 48000.0;
        long long lastSwitchTicks = 0; // when the driver last asked for a block, for spotting missed blocks
    };

    float ClampUnit(float value)
    {
        return value < -1.0f ? -1.0f : (value > 1.0f ? 1.0f : value);
    }

    bool IsSupportedSampleType(ASIOSampleType type)
    {
        return type == ASIOSTFloat32LSB || type == ASIOSTInt32LSB || type == ASIOSTInt16LSB || type == ASIOSTInt24LSB;
    }

    // Reads one block of driver samples into floats. This is the reverse of WriteFloatSamples.
    void ReadFloatSamples(const void* source, float* destination, long count, ASIOSampleType type)
    {
        switch (type)
        {
        case ASIOSTFloat32LSB:
            std::memcpy(destination, source, sizeof(float) * static_cast<size_t>(count));
            break;
        case ASIOSTInt32LSB:
        {
            const auto* in = static_cast<const int32*>(source);
            for (long i = 0; i < count; ++i)
            {
                destination[i] = static_cast<float>(static_cast<double>(in[i]) / 2147483648.0);
            }
            break;
        }
        case ASIOSTInt16LSB:
        {
            const auto* in = static_cast<const int16_t*>(source);
            for (long i = 0; i < count; ++i)
            {
                destination[i] = static_cast<float>(in[i]) / 32768.0f;
            }
            break;
        }
        case ASIOSTInt24LSB:
        {
            const auto* in = static_cast<const uint8_t*>(source);
            for (long i = 0; i < count; ++i)
            {
                int32 value = static_cast<int32>(in[i * 3]) | (static_cast<int32>(in[i * 3 + 1]) << 8) | (static_cast<int32>(in[i * 3 + 2]) << 16);
                if (value & 0x800000)
                {
                    value |= ~0xFFFFFF; // sign-extend the 24-bit value
                }
                destination[i] = static_cast<float>(static_cast<double>(value) / 8388608.0);
            }
            break;
        }
        default:
            std::fill(destination, destination + count, 0.0f);
            break;
        }
    }

    // A click must return at least this many frames after it was written, so that
    // crosstalk or a late reflection in the converters is not taken for the echo.
    constexpr long long kMinimumEchoFrames = 64;
    constexpr float kClickThreshold = 0.002f; // about -54 dBFS, well above the quiet input noise floor

    constexpr double kBurstHz = 1000.0;
    constexpr double kBurstSeconds = 0.01;

    // Writes the test burst into the block that starts at frame session.frame, and looks for it on input 0.
    void MeasureClickBlock(AsioSession& session, long doubleBufferIndex)
    {
        const long count = session.bufferSize;
        const long long burstFrames = static_cast<long long>(session.sampleRate * kBurstSeconds);
        const bool firstBlock = session.clickFrame < 0;
        if (firstBlock)
        {
            session.clickFrame = session.frame + count; // the click goes out at the start of the next block
            session.inputPeak = 0.0f;
            session.inputPeakFrame = 0;
        }

        ReadFloatSamples(session.bufferInfos[static_cast<size_t>(session.deviceChannels)].buffers[doubleBufferIndex],
            session.inputScratch.data(), count, session.inputSampleType);
        if (firstBlock)
        {
            // This block was captured before the burst went out, so its level is the input's noise floor.
            session.noisePeak = 0.0f;
            for (long i = 0; i < count; ++i)
            {
                session.noisePeak = (std::max)(session.noisePeak, std::fabs(session.inputScratch[static_cast<size_t>(i)]));
            }
        }
        // A burst counts only when it stands well above the noise floor, so a noisy input cannot fake an echo.
        const float threshold = (std::max)(kClickThreshold, 4.0f * session.noisePeak);

        for (long i = 0; i < count; ++i)
        {
            const long long absolute = session.frame + i;
            if (absolute >= session.clickFrame && absolute < session.clickFrame + burstFrames)
            {
                // A short sine burst: a single full-scale sample carries too little energy to survive the converters.
                // Written to every output so that the one wired to input 1 is found whatever its channel number.
                const double t = static_cast<double>(absolute - session.clickFrame) / session.sampleRate;
                const float sample = static_cast<float>(0.5 * std::sin(2.0 * 3.14159265358979323846 * kBurstHz * t));
                for (auto& channel : session.planar)
                {
                    channel[static_cast<size_t>(i)] = sample;
                }
            }
            const float level = std::fabs(session.inputScratch[static_cast<size_t>(i)]);
            if (absolute >= session.clickFrame && level > session.inputPeak)
            {
                session.inputPeak = level;
                session.inputPeakFrame = absolute;
            }
            if (session.detectedFrame < 0 && absolute >= session.clickFrame + kMinimumEchoFrames && level > threshold)
            {
                session.detectedFrame = absolute;
            }
        }
        session.engine->measurePeak = session.inputPeak;
        session.engine->measurePeakFrames = session.inputPeakFrame - session.clickFrame;
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

    // Runs on the driver's own thread: renders one block of every running plug-in, mixed, and writes it to the
    // output buffers.
    void AsioBufferSwitch(long doubleBufferIndex, ASIOBool /*directProcess*/)
    {
        AsioSession* session = g_asioSession.load();
        if (session == nullptr || session->engine->stopRequested.load())
        {
            return;
        }

        // A block asked for much later than one block after the previous one means the driver skipped some.
        const long long blockStart = QpcNow();
        const double period = session->bufferSize / session->sampleRate;
        if (session->lastSwitchTicks != 0 && QpcSeconds(blockStart - session->lastSwitchTicks) > period * 1.8)
        {
            RecordDspDropout();
        }
        session->lastSwitchTicks = blockStart;
        _mm_setcsr(_mm_getcsr() | 0x8040); // the driver's thread: flush tiny values to zero, as the workers do

        MixVoices(session->planar[0].data(), session->planar[1].data(), static_cast<int32>(session->bufferSize), session->sampleRate);

        if (session->hasInput)
        {
            if (session->engine->measureRequested.load() && !session->engine->measureFinished.load())
            {
                MeasureClickBlock(*session, doubleBufferIndex);
                if (session->detectedFrame >= 0)
                {
                    session->engine->measureResultFrames = session->detectedFrame - session->clickFrame;
                    session->engine->measureFinished = true;
                    session->engine->measureRequested = false;
                }
            }
            else if (session->clickFrame >= 0)
            {
                // The request ended without a result (timeout or cancel): the next measurement starts fresh.
                session->clickFrame = -1;
                session->detectedFrame = -1;
            }
        }

        for (int32 devCh = 0; devCh < session->deviceChannels; ++devCh)
        {
            const float* source = devCh < session->pluginChannels
                ? session->planar[static_cast<size_t>(devCh)].data()
                : session->silence.data();
            void* destination = session->bufferInfos[static_cast<size_t>(devCh)].buffers[doubleBufferIndex];
            WriteFloatSamples(source, destination, session->bufferSize, session->sampleType);
        }
        session->frame += session->bufferSize;

        if (session->postOutput)
        {
            session->driver->outputReady();
        }

        RecordDspBlock(QpcSeconds(QpcNow() - blockStart), period);
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
    bool ConfigureAsioSession(AsioSession& session, double& sampleRate, std::string& error)
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
        // The user's buffer size is used when it lies within the driver's range, otherwise the driver's preferred size.
        session.bufferSize = preferredSize;
        if (session.engine->requestedBufferFrames > 0)
        {
            const long requested = session.engine->requestedBufferFrames;
            if (requested < minSize || requested > maxSize)
            {
                error = "La dimensione del buffer scelta non rientra nei limiti del driver (" + std::to_string(minSize) + " - " + std::to_string(maxSize) + " campioni).";
                return false;
            }
            session.bufferSize = requested;
        }

        sampleRate = 0.0;
        if (session.engine->requestedSampleRate > 0.0)
        {
            if (session.driver->canSampleRate(session.engine->requestedSampleRate) != ASE_OK
                || session.driver->setSampleRate(session.engine->requestedSampleRate) != ASE_OK)
            {
                error = "Il driver ASIO non accetta la frequenza di campionamento scelta.";
                return false;
            }
        }
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
        const size_t frames = static_cast<size_t>(session.bufferSize);
        session.planar.assign(static_cast<size_t>(session.pluginChannels), std::vector<float>(frames));
        session.silence.assign(frames, 0.0f);

        session.postOutput = session.driver->outputReady() == ASE_OK;
        session.sampleRate = sampleRate;

        // Input 0 is needed only for the loopback measurement. Drivers without it still stream normally.
        session.hasInput = false;
        if (inputChannels > 0)
        {
            ASIOChannelInfo inputInfo{};
            inputInfo.channel = 0;
            inputInfo.isInput = ASIOTrue;
            if (session.driver->getChannelInfo(&inputInfo) == ASE_OK && IsSupportedSampleType(inputInfo.type))
            {
                session.hasInput = true;
                session.inputSampleType = inputInfo.type;
                session.inputScratch.assign(frames, 0.0f);
            }
        }
        return true;
    }

    void RunAsioEngineThread(AudioEngineState* engine)
    {
        const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        const bool comInitializedHere = SUCCEEDED(comResult);

        AsioSession session;
        session.engine = engine;
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
            if (!ConfigureAsioSession(session, sampleRate, error))
            {
                fail(error);
            }
            else
            {
                {
                    {
                        engine->maxBlock = static_cast<int32>(session.bufferSize);
                        engine->sampleRate = sampleRate;

                        ASIOCallbacks callbacks{};
                        callbacks.bufferSwitch = AsioBufferSwitch;
                        callbacks.sampleRateDidChange = AsioSampleRateChanged;
                        callbacks.asioMessage = AsioMessage;
                        callbacks.bufferSwitchTimeInfo = AsioBufferSwitchTimeInfo;

                        // Output buffers first, then input 0 when the driver has a usable input.
                        auto prepareBufferInfos = [&session]()
                        {
                            session.bufferInfos.assign(static_cast<size_t>(session.deviceChannels) + (session.hasInput ? 1 : 0), ASIOBufferInfo{});
                            for (int32 ch = 0; ch < session.deviceChannels; ++ch)
                            {
                                session.bufferInfos[static_cast<size_t>(ch)].isInput = ASIOFalse;
                                session.bufferInfos[static_cast<size_t>(ch)].channelNum = ch;
                            }
                            if (session.hasInput)
                            {
                                session.bufferInfos[static_cast<size_t>(session.deviceChannels)].isInput = ASIOTrue;
                                session.bufferInfos[static_cast<size_t>(session.deviceChannels)].channelNum = 0;
                            }
                        };

                        // Only one ASIO session can own the callbacks. A second engine must not take them over.
                        AsioSession* noSession = nullptr;
                        if (!g_asioSession.compare_exchange_strong(noSession, &session))
                        {
                            fail("Un'altra uscita ASIO è già aperta.");
                        }
                        else
                        {
                            prepareBufferInfos();
                            bool created = driver->createBuffers(session.bufferInfos.data(),
                                static_cast<long>(session.bufferInfos.size()), session.bufferSize, &callbacks) == ASE_OK;
                            if (!created && session.hasInput)
                            {
                                // Some drivers refuse the input channel: stream without it, so measurement is unavailable.
                                session.hasInput = false;
                                prepareBufferInfos();
                                created = driver->createBuffers(session.bufferInfos.data(),
                                    static_cast<long>(session.bufferInfos.size()), session.bufferSize, &callbacks) == ASE_OK;
                            }

                            if (!created)
                            {
                                fail("IASIO::createBuffers fallita.");
                            }
                            else
                            {
                            buffersCreated = true;
                            long inputLatency = 0;
                            long outputLatency = 0;
                            if (driver->getLatencies(&inputLatency, &outputLatency) == ASE_OK)
                            {
                                engine->asioOutputLatencyFrames = outputLatency;
                            }
                            engine->asioBufferFrames = session.bufferSize;
                            engine->asioSampleRate = sampleRate;
                            engine->asioHasInput = session.hasInput;
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
        engine->asioBufferFrames = 0;
        engine->asioOutputLatencyFrames = 0;
        engine->asioSampleRate = 0.0;
        if (buffersCreated)
        {
            driver->disposeBuffers();
        }
        // The driver has stopped calling back: the voices it held can be released.
        g_audioThreadVoices.reset();
        if (driver != nullptr)
        {
            driver->Release();
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
static int StartAudioEngine(void* moduleHandle, const wchar_t* deviceId, bool useAsio, int32 bufferFrames, double sampleRate)
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
    const bool isClip = module->component == nullptr && module->clipChannels > 0;
    if (module->component == nullptr && !isClip)
    {
        SetBridgeError("Nessun IComponent disponibile: impossibile avviare l'audio.");
        return 0;
    }

    std::lock_guard<std::mutex> mixerLock(g_mixerMutex);
    {
        std::lock_guard<std::mutex> lock(module->voiceMutex);
        if (module->voice != nullptr)
        {
            return 1; // already playing
        }
    }

    auto voice = std::make_shared<MixerVoice>();
    voice->module = module;
    voice->isClip = isClip;
    if (!isClip
        && (module->component->queryInterface(IAudioProcessor_iid, reinterpret_cast<void**>(&voice->processor)) != kResultOk
            || voice->processor == nullptr))
    {
        voice->processor = nullptr;
        SetBridgeError("Il plug-in non espone IAudioProcessor: impossibile avviare l'audio.");
        return 0;
    }

    const std::wstring requestedDevice = deviceId != nullptr ? deviceId : L"";
    auto voices = CurrentVoices();
    const bool otherOutput = g_engine != nullptr
        && (g_engine->deviceId != requestedDevice || g_engine->useAsio != useAsio
            || g_engine->requestedBufferFrames != bufferFrames || g_engine->requestedSampleRate != sampleRate);
    if (otherOutput)
    {
        // The audio settings changed: the output reopens with them, and the plug-ins already playing are
        // prepared again for its block size and sample rate.
        PublishVoices({});
        StopAudioDevice();
        for (const auto& playing : voices)
        {
            WaitUntilReleased(playing);
            playing->Deactivate();
        }
    }

    if (g_engine == nullptr)
    {
        auto* engine = new AudioEngineState();
        engine->deviceId = requestedDevice;
        engine->useAsio = useAsio;
        engine->requestedBufferFrames = bufferFrames;
        engine->requestedSampleRate = sampleRate;
        engine->thread = useAsio ? std::thread(RunAsioEngineThread, engine) : std::thread(RunAudioEngineThread, engine);
        g_engine = engine;

        // Wait (with a generous timeout) for the engine thread to finish its startup sequence, so the caller
        // gets an accurate success/failure result instead of a false "started" positive.
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (engine->initResult.load() == 0 && std::chrono::steady_clock::now() < deadline)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }

        if (engine->initResult.load() != 1)
        {
            std::string error = engine->GetInitError();
            SetBridgeError(error.empty() ? "Avvio del motore audio fallito (timeout o errore sconosciuto)." : error);
            StopAudioDevice();
            for (const auto& playing : voices)
            {
                std::lock_guard<std::mutex> lock(playing->module->voiceMutex);
                playing->module->voice.reset();
            }
            PublishVoices({});
            return 0;
        }

        if (otherOutput)
        {
            for (const auto& playing : voices)
            {
                std::string error;
                playing->Activate(g_engine->maxBlock, g_engine->sampleRate, error);
            }
        }
    }

    std::string error;
    if (!voice->Activate(g_engine->maxBlock, g_engine->sampleRate, error))
    {
        SetBridgeError(error);
        voice->Deactivate();
        PublishVoices(voices);
        if (voices.empty())
        {
            StopAudioDevice();
        }
        return 0;
    }

    voices.push_back(voice);
    PublishVoices(std::move(voices));
    {
        std::lock_guard<std::mutex> lock(module->voiceMutex);
        module->voice = std::move(voice);
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
    return StartAudioEngine(moduleHandle, deviceId, false, 0, 0.0);
}

int VstHostBridge_StartAudioAsio(void* moduleHandle, const wchar_t* driverClsid)
{
    return StartAudioEngine(moduleHandle, driverClsid, true, 0, 0.0);
}

int VstHostBridge_GetAsioTiming(void* moduleHandle, int32_t* outBufferFrames, int32_t* outOutputLatencyFrames, double* outSampleRate)
{
    if (moduleHandle == nullptr || outBufferFrames == nullptr || outOutputLatencyFrames == nullptr || outSampleRate == nullptr)
    {
        return 0;
    }

    auto* engine = RunningOutputFor(static_cast<BridgeModule*>(moduleHandle));
    if (engine == nullptr || !engine->useAsio || engine->asioBufferFrames.load() == 0)
    {
        return 0;
    }

    *outBufferFrames = static_cast<int32_t>(engine->asioBufferFrames.load());
    *outOutputLatencyFrames = static_cast<int32_t>(engine->asioOutputLatencyFrames.load());
    *outSampleRate = engine->asioSampleRate.load();
    return 1;
}

int VstHostBridge_MeasureAsioLatency(void* moduleHandle, double* outMilliseconds)
{
    if (moduleHandle == nullptr || outMilliseconds == nullptr)
    {
        SetBridgeError("Argomenti non validi.");
        return 0;
    }

    auto* engine = RunningOutputFor(static_cast<BridgeModule*>(moduleHandle));
    if (engine == nullptr || !engine->useAsio || engine->asioBufferFrames.load() == 0)
    {
        SetBridgeError("La misura richiede una sessione ASIO in corso.");
        return 0;
    }

    if (!engine->asioHasInput.load())
    {
        SetBridgeError("Il driver non espone un ingresso utilizzabile per la misura.");
        return 0;
    }

    engine->measureResultFrames = -1;
    engine->measurePeak = 0.0f;
    engine->measurePeakFrames = 0;
    engine->measureFinished = false;
    engine->measureRequested = true;

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
    while (!engine->measureFinished.load() && std::chrono::steady_clock::now() < deadline)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    engine->measureRequested = false;

    const long long frames = engine->measureResultFrames.load();
    if (!engine->measureFinished.load() || frames < 0)
    {
        const float peak = engine->measurePeak.load();
        const double peakDb = peak > 0.0f ? 20.0 * std::log10(static_cast<double>(peak)) : -120.0;
        std::ostringstream message;
        const double peakMs = static_cast<double>(engine->measurePeakFrames.load()) * 1000.0 / engine->asioSampleRate.load();
        message << "Nessun segnale rilevato sull'ingresso 1 (picco " << std::fixed << std::setprecision(1)
                << peakDb << " dBFS a " << peakMs << " ms dal click). Collega l'uscita 1 all'ingresso 1 con un cavo di loopback e alza il gain dell'ingresso.";
        SetBridgeError(message.str());
        return 0;
    }

    // The measured frames already match the driver's declared latencies: the same click at 512 and at 256
    // samples gives round trips 2 x 256 frames apart, as the declared input plus output latencies do.
    // The remaining gap to the declared sum is the converters and driver overhead, a few milliseconds.
    const long long roundTripFrames = frames;
    *outMilliseconds = static_cast<double>(roundTripFrames) * 1000.0 / engine->asioSampleRate.load();
    return 1;
}

#if SONATOR_WITH_ASIO
namespace
{
    struct AsioDriverProbe
    {
        bool ok = false;
        std::string error;
        long minSize = 0;
        long maxSize = 0;
        long preferredSize = 0;
        long granularity = 0;
        double currentRate = 0.0;
        std::vector<double> rates;
    };

    // Sample rates checked against the driver. Only rates the driver accepts are reported.
    constexpr double kStandardRates[] = { 8000.0, 11025.0, 16000.0, 22050.0, 32000.0, 44100.0, 48000.0, 88200.0, 96000.0, 176400.0, 192000.0 };

    // Opens the driver on its own STA thread, so the call works from any thread of the host.
    void RunAsioProbe(const std::wstring& clsidText, bool openControlPanel, AsioDriverProbe& probe)
    {
        std::thread worker([&clsidText, openControlPanel, &probe]()
        {
            const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            const bool comInitializedHere = SUCCEEDED(comResult);

            CLSID clsid{};
            IASIO* driver = nullptr;
            HRESULT hr = CLSIDFromString(clsidText.c_str(), &clsid);
            if (FAILED(hr))
            {
                probe.error = "Identificatore del driver ASIO non valido.";
            }
            else if (FAILED(hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, clsid, reinterpret_cast<void**>(&driver))) || driver == nullptr)
            {
                probe.error = "Impossibile aprire il driver ASIO: " + FormatWin32Error(static_cast<DWORD>(hr));
            }
            else if (!driver->init(GetDesktopWindow()))
            {
                char message[256]{};
                driver->getErrorMessage(message);
                probe.error = std::string("Il driver ASIO non si è inizializzato: ") + message;
            }
            else if (openControlPanel)
            {
                driver->controlPanel();
                probe.ok = true;
            }
            else if (driver->getBufferSize(&probe.minSize, &probe.maxSize, &probe.preferredSize, &probe.granularity) != ASE_OK)
            {
                probe.error = "Il driver ASIO non riporta i limiti del buffer.";
            }
            else
            {
                driver->getSampleRate(&probe.currentRate);
                for (double rate : kStandardRates)
                {
                    if (driver->canSampleRate(rate) == ASE_OK)
                    {
                        probe.rates.push_back(rate);
                    }
                }
                probe.ok = true;
            }

            if (driver != nullptr)
            {
                driver->Release();
            }
            if (comInitializedHere)
            {
                CoUninitialize();
            }
        });
        worker.join();
    }
}

int VstHostBridge_GetAsioCapabilities(const wchar_t* driverClsid, int32_t* outMinBuffer, int32_t* outMaxBuffer,
    int32_t* outPreferredBuffer, int32_t* outGranularity, double* outCurrentRate, double* outRates,
    int32_t rateCapacity, int32_t* outRateCount)
{
    if (driverClsid == nullptr || outMinBuffer == nullptr || outMaxBuffer == nullptr || outPreferredBuffer == nullptr
        || outGranularity == nullptr || outCurrentRate == nullptr || outRateCount == nullptr || (outRates == nullptr && rateCapacity > 0))
    {
        SetBridgeError("Argomenti non validi.");
        return 0;
    }

    AsioDriverProbe probe;
    RunAsioProbe(driverClsid, false, probe);
    if (!probe.ok)
    {
        SetBridgeError(probe.error);
        return 0;
    }

    *outMinBuffer = static_cast<int32_t>(probe.minSize);
    *outMaxBuffer = static_cast<int32_t>(probe.maxSize);
    *outPreferredBuffer = static_cast<int32_t>(probe.preferredSize);
    *outGranularity = static_cast<int32_t>(probe.granularity);
    *outCurrentRate = probe.currentRate;

    const int32_t count = (std::min)(rateCapacity, static_cast<int32_t>(probe.rates.size()));
    for (int32_t i = 0; i < count; ++i)
    {
        outRates[i] = probe.rates[static_cast<size_t>(i)];
    }
    *outRateCount = count;
    return 1;
}

int VstHostBridge_OpenAsioControlPanel(const wchar_t* driverClsid)
{
    if (driverClsid == nullptr)
    {
        SetBridgeError("Argomenti non validi.");
        return 0;
    }

    AsioDriverProbe probe;
    RunAsioProbe(driverClsid, true, probe);
    if (!probe.ok)
    {
        SetBridgeError(probe.error);
        return 0;
    }
    return 1;
}

int VstHostBridge_StartAudioAsioWithFormat(void* moduleHandle, const wchar_t* driverClsid, int32_t bufferFrames, double sampleRate)
{
    return StartAudioEngine(moduleHandle, driverClsid, true, bufferFrames, sampleRate);
}
#else
int VstHostBridge_GetAsioCapabilities(const wchar_t*, int32_t*, int32_t*, int32_t*, int32_t*, double*, double*, int32_t, int32_t*)
{
    SetBridgeError("Questa build di VstHostBridge non include il supporto ASIO.");
    return 0;
}

int VstHostBridge_OpenAsioControlPanel(const wchar_t*)
{
    SetBridgeError("Questa build di VstHostBridge non include il supporto ASIO.");
    return 0;
}

int VstHostBridge_StartAudioAsioWithFormat(void* moduleHandle, const wchar_t* driverClsid, int32_t bufferFrames, double sampleRate)
{
    return StartAudioEngine(moduleHandle, driverClsid, true, bufferFrames, sampleRate);
}
#endif

#if SONATOR_WITH_ASIO
namespace
{
    struct AsioLatencyProbe
    {
        bool ok = false;
        std::string error;
        long inputLatency = 0;
        long outputLatency = 0;
    };

    // Creates buffers at the requested size on a short-lived driver session and reads the latencies the driver declares.
    // No audio runs: the callbacks are the same no-op ones used outside a session.
    void RunAsioLatencyProbe(const std::wstring& clsidText, int32 bufferFrames, double sampleRate, AsioLatencyProbe& probe)
    {
        std::thread worker([&clsidText, bufferFrames, sampleRate, &probe]()
        {
            const HRESULT comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            const bool comInitializedHere = SUCCEEDED(comResult);

            CLSID clsid{};
            IASIO* driver = nullptr;
            HRESULT hr = CLSIDFromString(clsidText.c_str(), &clsid);
            bool buffersCreated = false;
            if (FAILED(hr))
            {
                probe.error = "Identificatore del driver ASIO non valido.";
            }
            else if (FAILED(hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, clsid, reinterpret_cast<void**>(&driver))) || driver == nullptr)
            {
                probe.error = "Impossibile aprire il driver ASIO: " + FormatWin32Error(static_cast<DWORD>(hr));
            }
            else if (!driver->init(GetDesktopWindow()))
            {
                char message[256]{};
                driver->getErrorMessage(message);
                probe.error = std::string("Il driver ASIO non si è inizializzato: ") + message;
            }
            else
            {
                long inputChannels = 0;
                long outputChannels = 0;
                long minSize = 0;
                long maxSize = 0;
                long preferredSize = 0;
                long granularity = 0;
                if (driver->getChannels(&inputChannels, &outputChannels) != ASE_OK || outputChannels <= 0)
                {
                    probe.error = "Il driver ASIO non espone canali di uscita.";
                }
                else if (driver->getBufferSize(&minSize, &maxSize, &preferredSize, &granularity) != ASE_OK)
                {
                    probe.error = "Il driver ASIO non riporta i limiti del buffer.";
                }
                else if (bufferFrames > 0 && (bufferFrames < minSize || bufferFrames > maxSize))
                {
                    probe.error = "La dimensione del buffer scelta non rientra nei limiti del driver.";
                }
                else
                {
                    if (sampleRate > 0.0)
                    {
                        driver->setSampleRate(sampleRate);
                    }

                    const long size = bufferFrames > 0 ? static_cast<long>(bufferFrames) : preferredSize;
                    const int32 deviceChannels = outputChannels < 2 ? static_cast<int32>(outputChannels) : 2;
                    std::vector<ASIOBufferInfo> infos(static_cast<size_t>(deviceChannels) + (inputChannels > 0 ? 1 : 0), ASIOBufferInfo{});
                    for (int32 ch = 0; ch < deviceChannels; ++ch)
                    {
                        infos[static_cast<size_t>(ch)].isInput = ASIOFalse;
                        infos[static_cast<size_t>(ch)].channelNum = ch;
                    }
                    if (inputChannels > 0)
                    {
                        infos.back().isInput = ASIOTrue;
                        infos.back().channelNum = 0;
                    }

                    ASIOCallbacks callbacks{};
                    callbacks.bufferSwitch = AsioBufferSwitch;
                    callbacks.sampleRateDidChange = AsioSampleRateChanged;
                    callbacks.asioMessage = AsioMessage;
                    callbacks.bufferSwitchTimeInfo = AsioBufferSwitchTimeInfo;

                    if (driver->createBuffers(infos.data(), static_cast<long>(infos.size()), size, &callbacks) != ASE_OK)
                    {
                        probe.error = "Il driver ASIO non accetta questa dimensione di buffer.";
                    }
                    else
                    {
                        buffersCreated = true;
                        if (driver->getLatencies(&probe.inputLatency, &probe.outputLatency) == ASE_OK)
                        {
                            probe.ok = true;
                        }
                        else
                        {
                            probe.error = "Il driver ASIO non riporta le latenze.";
                        }
                    }
                }
            }

            if (buffersCreated)
            {
                driver->disposeBuffers();
            }
            if (driver != nullptr)
            {
                driver->Release();
            }
            if (comInitializedHere)
            {
                CoUninitialize();
            }
        });
        worker.join();
    }
}

int VstHostBridge_GetAsioLatency(const wchar_t* driverClsid, int32_t bufferFrames, double sampleRate, int32_t* outInputLatency, int32_t* outOutputLatency)
{
    if (driverClsid == nullptr || outInputLatency == nullptr || outOutputLatency == nullptr)
    {
        SetBridgeError("Argomenti non validi.");
        return 0;
    }

    AsioLatencyProbe probe;
    RunAsioLatencyProbe(driverClsid, bufferFrames, sampleRate, probe);
    if (!probe.ok)
    {
        SetBridgeError(probe.error);
        return 0;
    }

    *outInputLatency = static_cast<int32_t>(probe.inputLatency);
    *outOutputLatency = static_cast<int32_t>(probe.outputLatency);
    return 1;
}
#else
int VstHostBridge_GetAsioLatency(const wchar_t*, int32_t, double, int32_t*, int32_t*)
{
    SetBridgeError("Questa build di VstHostBridge non include il supporto ASIO.");
    return 0;
}
#endif

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

int VstHostBridge_SendNoteEvent(void* moduleHandle, int32_t channel, int32_t pitch, int32_t velocity, int32_t isOn)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    std::shared_ptr<MixerVoice> voice;
    {
        std::lock_guard<std::mutex> lock(module->voiceMutex);
        voice = module->voice;
    }
    if (voice == nullptr)
    {
        return 0;
    }

    Event event{};
    event.busIndex = 0;
    event.sampleOffset = 0;
    event.flags = Event::kIsLive;
    if (isOn != 0 && velocity > 0)
    {
        event.type = Event::kNoteOnEvent;
        event.noteOn.channel = channel;
        event.noteOn.pitch = static_cast<int16>(pitch);
        event.noteOn.velocity = static_cast<float>(velocity) / 127.0f;
        event.noteOn.noteId = -1;
    }
    else
    {
        event.type = Event::kNoteOffEvent;
        event.noteOff.channel = channel;
        event.noteOff.pitch = static_cast<int16>(pitch);
        event.noteOff.velocity = 0.0f;
        event.noteOff.noteId = -1;
    }

    std::lock_guard<std::mutex> lock(voice->midiMutex);
    voice->pendingMidi.push_back(event);
    if (channel >= 0 && channel < 16 && pitch >= 0 && pitch < 128)
    {
        auto& count = voice->held[channel][pitch];
        if (event.type == Event::kNoteOnEvent)
        {
            if (count < 255)
            {
                ++count;
                ++voice->heldNotes;
            }
        }
        else
        {
            voice->heldNotes -= count;
            count = 0; // one note-off releases the pitch, however many times it was struck
        }
    }
    return 1;
}

int VstHostBridge_StartMixCapture(double maxSeconds)
{
    if (maxSeconds <= 0.0)
    {
        return 0;
    }

    g_capture.recording.store(false, std::memory_order_release);
    // Room at the output's sample rate (96 kHz when no output is open yet: it may open with the playback);
    // the audio thread never grows the buffer.
    double rate = 96000.0;
    {
        std::lock_guard<std::mutex> lock(g_mixerMutex);
        if (g_engine != nullptr && g_engine->sampleRate > 0.0)
        {
            rate = g_engine->sampleRate;
        }
    }
    g_capture.samples.assign(static_cast<size_t>(maxSeconds * rate) * 2, 0.0f);
    g_capture.frames.store(0, std::memory_order_relaxed);
    g_capture.sampleRate.store(0.0, std::memory_order_relaxed);
    g_capture.recording.store(true, std::memory_order_release);
    return 1;
}

int64_t VstHostBridge_StopMixCapture(double* outSampleRate)
{
    g_capture.recording.store(false, std::memory_order_release);
    // A block being copied right now finishes within a block's time.
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    if (outSampleRate != nullptr)
    {
        *outSampleRate = g_capture.sampleRate.load();
    }
    return g_capture.frames.load(std::memory_order_acquire);
}

int VstHostBridge_ReadMixCapture(float* interleaved, int64_t frames)
{
    if (interleaved == nullptr || frames < 0 || frames > g_capture.frames.load())
    {
        return 0;
    }

    std::memcpy(interleaved, g_capture.samples.data(), static_cast<size_t>(frames) * 2 * sizeof(float));
    g_capture.samples.clear();
    g_capture.samples.shrink_to_fit();
    g_capture.frames.store(0);
    return 1;
}

int VstHostBridge_SendController(void* moduleHandle, int32_t channel, int32_t controller, double value)
{
    if (moduleHandle == nullptr || channel < 0 || channel >= 16 || controller < 0 || controller >= kCountCtrlNumber)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    std::shared_ptr<MixerVoice> voice;
    {
        std::lock_guard<std::mutex> lock(module->voiceMutex);
        voice = module->voice;
    }
    if (voice == nullptr || voice->isClip || voice->controllerParams.empty())
    {
        return 0;
    }

    const auto id = voice->controllerParams[static_cast<size_t>(channel * kCountCtrlNumber + controller)];
    if (id == kNoParamId)
    {
        return 0; // the plug-in has no parameter for this controller
    }

    std::lock_guard<std::mutex> lock(voice->midiMutex);
    voice->pendingControllers.push_back({ id, std::clamp(value, 0.0, 1.0) });
    if (controller == kCtrlSustainOnOff)
    {
        voice->sustainHeld[channel] = value >= 0.5;
    }
    return 1;
}

int VstHostBridge_ReleaseAllNotes(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    std::shared_ptr<MixerVoice> voice;
    {
        std::lock_guard<std::mutex> lock(module->voiceMutex);
        voice = module->voice;
    }
    if (voice == nullptr)
    {
        return 0;
    }

    // A note-off for every note still held, so nothing is left sounding.
    std::lock_guard<std::mutex> lock(voice->midiMutex);
    for (int16 channel = 0; channel < 16; ++channel)
    {
        for (int16 pitch = 0; pitch < 128; ++pitch)
        {
            if (voice->held[channel][pitch] == 0)
            {
                continue;
            }

            Event event{};
            event.busIndex = 0;
            event.sampleOffset = 0;
            event.flags = Event::kIsLive;
            event.type = Event::kNoteOffEvent;
            event.noteOff.channel = channel;
            event.noteOff.pitch = pitch;
            event.noteOff.velocity = 0.0f;
            event.noteOff.noteId = -1;
            voice->pendingMidi.push_back(event);
            voice->held[channel][pitch] = 0;
        }
    }
    voice->heldNotes = 0;

    // A sustain pedal left down would keep the notes sounding: it comes up too.
    for (int32 channel = 0; channel < 16; ++channel)
    {
        if (!voice->sustainHeld[channel] || voice->controllerParams.empty())
        {
            continue;
        }

        const auto id = voice->controllerParams[static_cast<size_t>(channel * kCountCtrlNumber + kCtrlSustainOnOff)];
        if (id != kNoParamId)
        {
            voice->pendingControllers.push_back({ id, 0.0 });
        }
        voice->sustainHeld[channel] = false;
    }
    return 1;
}

int VstHostBridge_IsAudioRunning(void* moduleHandle)
{
    if (moduleHandle == nullptr)
    {
        return 0;
    }

    auto* module = static_cast<BridgeModule*>(moduleHandle);
    std::lock_guard<std::mutex> lock(module->voiceMutex);
    return module->voice != nullptr ? 1 : 0;
}
