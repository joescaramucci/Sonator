// Sonator's own insert effects (SonVerb, SonDelay, SonCompress, SonEQ, SonLimiter) and the
// per-track chain that runs them. Everything here is written for the audio thread: parameters
// are atomics the UI can change at any time, and processing never blocks on the UI.
#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <vector>

namespace son
{
    // The effect types, as numbered across the bridge's C API and the .NET side.
    enum class EffectType : int32_t
    {
        Verb = 1,
        Delay = 2,
        Compress = 3,
        EQ = 4,
        Limiter = 5,
        Plugin = 100, // a VST3 effect, made by the bridge (VstHostBridge.cpp), not by CreateEffect
    };

    constexpr int MaxParameters = 8;

    class Effect
    {
    public:
        virtual ~Effect() = default;

        // Called before the first block, and again when the sample rate or the largest block grows.
        virtual void Prepare(double sampleRate, int maxBlock) = 0;

        // Processes planar audio in place: channels[c][i], numChannels of them (1 or more), frames samples each.
        virtual void Process(float** channels, int numChannels, int frames) = 0;

        // A value for the UI, such as the compressor's gain reduction in dB. 0 when the effect has none.
        virtual double Meter() const { return 0.0; }

        // What the effect runs, when it is a plug-in: an effect with the same identity in a new layout is
        // kept as it is rather than made again. Null for Sonator's own effects.
        virtual const void* Identity() const { return nullptr; }

        std::atomic<float> parameters[MaxParameters]{};
        std::atomic<bool> enabled{true};

        // The highest absolute sample going in and coming out since the UI last read them.
        std::atomic<float> inputPeak{0.0f};
        std::atomic<float> outputPeak{0.0f};
    };

    std::unique_ptr<Effect> CreateEffect(EffectType type);

    // The effects of one track, in order, then the track's gain. Also measures the output peak.
    class EffectChain
    {
    public:
        // Replaces the effects with new ones of the given types, at their default settings.
        void SetLayout(const int32_t* types, int32_t count);

        // Makes the effect for a slot: its type, and its identity (a plug-in) or null.
        using EffectFactory = std::function<std::unique_ptr<Effect>(int32_t type, void* identity)>;

        // As above, but a slot with an identity already in the chain keeps that effect as it is.
        void SetLayout(const int32_t* types, void* const* identities, int32_t count, const EffectFactory& make);

        void SetParameter(int32_t slot, int32_t parameter, double value);
        void SetEnabled(int32_t slot, bool enabled);
        double Meter(int32_t slot);

        // The effect's input and output peaks since the previous call, then starts measuring afresh.
        void TakeLevels(int32_t slot, float& input, float& output);

        // Linear gain applied after the effects.
        std::atomic<float> gain{1.0f};

        // Stereo balance applied with the gain: -1 hard left, 0 centre, +1 hard right. The side the
        // sound moves to stays as it is and the other side is turned down, so the centre loses nothing.
        std::atomic<float> pan{0.0f};

        // Processes planar audio in place. Called on the audio thread; if the UI is replacing the
        // effects at that moment the block passes through untouched rather than waiting.
        void Process(float** channels, int numChannels, int frames, double sampleRate);

        // Interleaved version, for audio the .NET side plays itself.
        void ProcessInterleaved(float* samples, int frames, int numChannels, double sampleRate);

        // The highest absolute sample since the last call, after the effects and the gain.
        float TakePeak();

    private:
        std::mutex _mutex;
        std::vector<std::unique_ptr<Effect>> _effects;
        double _sampleRate = 0.0;
        int _maxBlock = 0;
        std::atomic<float> _peak{0.0f};

        // The gain each channel had at the end of the last block, for gliding to a new one. Negative
        // until the first block.
        std::array<float, 8> _appliedGains{-1.0f, -1.0f, -1.0f, -1.0f, -1.0f, -1.0f, -1.0f, -1.0f};
        std::vector<std::vector<float>> _scratch;
        std::vector<float*> _scratchPointers;
    };
}
