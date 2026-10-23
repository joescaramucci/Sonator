#include "SonEffects.h"

#include <algorithm>
#include <array>
#include <initializer_list>
#include <cmath>

namespace son
{
    namespace
    {
        constexpr double Pi = 3.14159265358979323846;
        constexpr int MaxChannels = 8;

        float DbToGain(double db) { return static_cast<float>(std::pow(10.0, db / 20.0)); }

        double GainToDb(double gain) { return 20.0 * std::log10(std::max(gain, 1e-9)); }

        // A coefficient that makes a one-pole smoother reach about 63% of a step in the given time.
        float SmoothingCoefficient(double milliseconds, double sampleRate)
        {
            const double samples = std::max(milliseconds, 0.01) * 0.001 * sampleRate;
            return static_cast<float>(std::exp(-1.0 / samples));
        }

        float Flush(float value) { return std::fabs(value) < 1e-20f ? 0.0f : value; }

        float PeakOf(float** channels, int numChannels, int frames)
        {
            float peak = 0.0f;
            for (int c = 0; c < numChannels; ++c)
            {
                for (int n = 0; n < frames; ++n)
                {
                    peak = std::max(peak, std::fabs(channels[c][n]));
                }
            }
            return peak;
        }

        // Keeps the larger of the stored and the new peak, safely against the UI resetting it.
        void StorePeak(std::atomic<float>& stored, float peak)
        {
            float current = stored.load();
            while (peak > current && !stored.compare_exchange_weak(current, peak))
            {
            }
        }

        void SetDefaults(Effect& effect, std::initializer_list<float> values)
        {
            int i = 0;
            for (float value : values)
            {
                effect.parameters[i++].store(value);
            }
        }

        // ---------------------------------------------------------------- SonVerb
        // A Freeverb-style room (Jezar's public-domain design): eight damped combs and four
        // all-passes per side, the right side slightly detuned for width, after a pre-delay.
        // Parameters: 0 room size 0..1, 1 damping 0..1, 2 width 0..1, 3 pre-delay ms, 4 mix %.
        class Verb final : public Effect
        {
        public:
            Verb() { SetDefaults(*this, {0.6f, 0.4f, 1.0f, 10.0f, 25.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                static const int combTuning[8] = {1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617};
                static const int allpassTuning[4] = {556, 441, 341, 225};
                const double scale = sampleRate / 44100.0;
                for (int side = 0; side < 2; ++side)
                {
                    const int spread = side == 0 ? 0 : 23;
                    for (int i = 0; i < 8; ++i)
                    {
                        _combs[side][i].Resize(static_cast<int>((combTuning[i] + spread) * scale));
                    }
                    for (int i = 0; i < 4; ++i)
                    {
                        _allpasses[side][i].Resize(static_cast<int>((allpassTuning[i] + spread) * scale));
                    }
                }
                _preDelay.assign(static_cast<size_t>(sampleRate * 0.25) + 1, 0.0f);
                _preDelayIndex = 0;
                _sampleRate = sampleRate;
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                const float room = std::clamp(parameters[0].load(), 0.0f, 1.0f) * 0.28f + 0.7f;
                const float damp = std::clamp(parameters[1].load(), 0.0f, 1.0f) * 0.4f;
                const float width = std::clamp(parameters[2].load(), 0.0f, 1.0f);
                const int preDelay = std::clamp(static_cast<int>(parameters[3].load() * 0.001 * _sampleRate), 0, static_cast<int>(_preDelay.size()) - 1);
                const float mix = std::clamp(parameters[4].load() / 100.0f, 0.0f, 1.0f);
                const float wet1 = 3.0f * (width / 2.0f + 0.5f);
                const float wet2 = 3.0f * ((1.0f - width) / 2.0f);
                const size_t size = _preDelay.size();

                float* left = channels[0];
                float* right = numChannels > 1 ? channels[1] : channels[0];
                for (int n = 0; n < frames; ++n)
                {
                    const float input = (left[n] + right[n]) * 0.015f;
                    _preDelay[_preDelayIndex] = input;
                    const float delayed = _preDelay[(_preDelayIndex + size - static_cast<size_t>(preDelay)) % size];
                    _preDelayIndex = (_preDelayIndex + 1) % size;

                    float outLeft = 0.0f;
                    float outRight = 0.0f;
                    for (int i = 0; i < 8; ++i)
                    {
                        outLeft += _combs[0][i].Process(delayed, room, damp);
                        outRight += _combs[1][i].Process(delayed, room, damp);
                    }
                    for (int i = 0; i < 4; ++i)
                    {
                        outLeft = _allpasses[0][i].Process(outLeft);
                        outRight = _allpasses[1][i].Process(outRight);
                    }

                    const float wetLeft = outLeft * wet1 + outRight * wet2;
                    const float wetRight = outRight * wet1 + outLeft * wet2;
                    if (numChannels > 1)
                    {
                        left[n] = left[n] * (1.0f - mix) + wetLeft * mix;
                        right[n] = right[n] * (1.0f - mix) + wetRight * mix;
                    }
                    else
                    {
                        left[n] = left[n] * (1.0f - mix) + (wetLeft + wetRight) * 0.5f * mix;
                    }
                }
            }

        private:
            struct Comb
            {
                std::vector<float> buffer;
                size_t index = 0;
                float store = 0.0f;

                void Resize(int length)
                {
                    buffer.assign(static_cast<size_t>(std::max(length, 1)), 0.0f);
                    index = 0;
                    store = 0.0f;
                }

                float Process(float input, float feedback, float damp)
                {
                    const float output = buffer[index];
                    store = Flush(output * (1.0f - damp) + store * damp);
                    buffer[index] = input + store * feedback;
                    index = (index + 1) % buffer.size();
                    return output;
                }
            };

            struct Allpass
            {
                std::vector<float> buffer;
                size_t index = 0;

                void Resize(int length)
                {
                    buffer.assign(static_cast<size_t>(std::max(length, 1)), 0.0f);
                    index = 0;
                }

                float Process(float input)
                {
                    const float buffered = buffer[index];
                    buffer[index] = Flush(input + buffered * 0.5f);
                    index = (index + 1) % buffer.size();
                    return buffered - input;
                }
            };

            Comb _combs[2][8];
            Allpass _allpasses[2][4];
            std::vector<float> _preDelay;
            size_t _preDelayIndex = 0;
            double _sampleRate = 44100.0;
        };

        // ---------------------------------------------------------------- SonDelay
        // A stereo echo with feedback darkened by a low-pass, optionally bouncing between the sides.
        // Parameters: 0 time ms, 1 feedback %, 2 mix %, 3 ping-pong (0 or 1), 4 high cut Hz.
        class Delay final : public Effect
        {
        public:
            Delay() { SetDefaults(*this, {375.0f, 35.0f, 25.0f, 0.0f, 8000.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                const size_t length = static_cast<size_t>(sampleRate * 2.1) + 4;
                for (auto& buffer : _buffers)
                {
                    buffer.assign(length, 0.0f);
                }
                _write = 0;
                _sampleRate = sampleRate;
                _currentDelay = parameters[0].load() * 0.001 * sampleRate;
                _filter[0] = _filter[1] = 0.0f;
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                const double target = std::clamp(static_cast<double>(parameters[0].load()), 1.0, 2000.0) * 0.001 * _sampleRate;
                const float feedback = std::clamp(parameters[1].load() / 100.0f, 0.0f, 0.95f);
                const float mix = std::clamp(parameters[2].load() / 100.0f, 0.0f, 1.0f);
                const bool pingPong = parameters[3].load() > 0.5f && numChannels > 1;
                const double cutoff = std::clamp(static_cast<double>(parameters[4].load()), 500.0, 20000.0);
                const float lowPass = static_cast<float>(1.0 - std::exp(-2.0 * Pi * cutoff / _sampleRate));
                const float glide = SmoothingCoefficient(60.0, _sampleRate);
                const size_t length = _buffers[0].size();
                const int sides = std::min(numChannels, 2);

                for (int n = 0; n < frames; ++n)
                {
                    // The delay time glides to a new setting instead of jumping, so turning the knob does not crackle.
                    _currentDelay = target + (_currentDelay - target) * glide;
                    const double readPosition = static_cast<double>(_write) - _currentDelay + static_cast<double>(length);
                    const size_t base = static_cast<size_t>(readPosition);
                    const float fraction = static_cast<float>(readPosition - static_cast<double>(base));

                    float echo[2];
                    for (int c = 0; c < sides; ++c)
                    {
                        const auto& buffer = _buffers[c];
                        const float a = buffer[base % length];
                        const float b = buffer[(base + 1) % length];
                        echo[c] = a + (b - a) * fraction;
                        _filter[c] = Flush(_filter[c] + (echo[c] - _filter[c]) * lowPass);
                    }

                    if (pingPong)
                    {
                        const float mono = (channels[0][n] + channels[1][n]) * 0.5f;
                        _buffers[0][_write] = mono + _filter[1] * feedback;
                        _buffers[1][_write] = _filter[0] * feedback;
                    }
                    else
                    {
                        for (int c = 0; c < sides; ++c)
                        {
                            _buffers[c][_write] = channels[c][n] + _filter[c] * feedback;
                        }
                    }

                    for (int c = 0; c < sides; ++c)
                    {
                        channels[c][n] = channels[c][n] * (1.0f - mix * 0.5f) + echo[c] * mix;
                    }
                    _write = (_write + 1) % length;
                }
            }

        private:
            std::vector<float> _buffers[2];
            size_t _write = 0;
            double _sampleRate = 44100.0;
            double _currentDelay = 0.0;
            float _filter[2] = {0.0f, 0.0f};
        };

        // ---------------------------------------------------------------- SonCompress
        // A stereo-linked peak compressor with a soft knee.
        // Parameters: 0 threshold dB, 1 ratio, 2 attack ms, 3 release ms, 4 makeup dB, 5 knee dB.
        class Compress final : public Effect
        {
        public:
            Compress() { SetDefaults(*this, {-18.0f, 4.0f, 10.0f, 120.0f, 0.0f, 6.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                _envelope = 0.0f;
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                const float threshold = parameters[0].load();
                const float ratio = std::max(parameters[1].load(), 1.0f);
                const float attack = SmoothingCoefficient(parameters[2].load(), _sampleRate);
                const float release = SmoothingCoefficient(parameters[3].load(), _sampleRate);
                const float makeup = parameters[4].load();
                const float knee = std::max(parameters[5].load(), 0.0f);
                const float slope = 1.0f / ratio - 1.0f;
                float deepest = 0.0f;

                for (int n = 0; n < frames; ++n)
                {
                    float peak = 0.0f;
                    for (int c = 0; c < numChannels; ++c)
                    {
                        peak = std::max(peak, std::fabs(channels[c][n]));
                    }

                    const float over = static_cast<float>(GainToDb(peak)) - threshold;
                    float target;
                    if (knee > 0.0f && std::fabs(over) <= knee / 2.0f)
                    {
                        const float into = over + knee / 2.0f;
                        target = slope * into * into / (2.0f * knee);
                    }
                    else
                    {
                        target = over > 0.0f ? slope * over : 0.0f;
                    }

                    // Gain reduction is negative: a deeper value is more reduction, reached at the attack speed.
                    const float coefficient = target < _envelope ? attack : release;
                    _envelope = target + (_envelope - target) * coefficient;
                    deepest = std::min(deepest, _envelope);

                    const float gain = DbToGain(_envelope + makeup);
                    for (int c = 0; c < numChannels; ++c)
                    {
                        channels[c][n] *= gain;
                    }
                }

                _meter.store(-deepest);
            }

            double Meter() const override { return _meter.load(); }

        private:
            double _sampleRate = 44100.0;
            float _envelope = 0.0f;
            std::atomic<float> _meter{0.0f};
        };

        // ---------------------------------------------------------------- SonEQ
        // Three bands from the RBJ cookbook: a low shelf, a bell in the middle and a high shelf.
        // Parameters: 0 low gain dB, 1 low Hz, 2 mid gain dB, 3 mid Hz, 4 mid Q, 5 high gain dB, 6 high Hz.
        class Equalizer final : public Effect
        {
        public:
            Equalizer() { SetDefaults(*this, {0.0f, 120.0f, 0.0f, 1000.0f, 1.0f, 0.0f, 8000.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                for (auto& band : _bands)
                {
                    for (auto& state : band.state)
                    {
                        state = {};
                    }
                }
                _designed.fill(std::nanf(""));
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                Design();
                for (auto& band : _bands)
                {
                    if (band.flat)
                    {
                        continue;
                    }

                    for (int c = 0; c < std::min(numChannels, MaxChannels); ++c)
                    {
                        auto& s = band.state[c];
                        float* samples = channels[c];
                        for (int n = 0; n < frames; ++n)
                        {
                            const double x = samples[n];
                            const double y = band.b0 * x + band.b1 * s.x1 + band.b2 * s.x2 - band.a1 * s.y1 - band.a2 * s.y2;
                            s.x2 = s.x1;
                            s.x1 = x;
                            s.y2 = s.y1;
                            s.y1 = std::fabs(y) < 1e-20 ? 0.0 : y;
                            samples[n] = static_cast<float>(y);
                        }
                    }
                }
            }

        private:
            struct State
            {
                double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            };

            struct Band
            {
                double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
                bool flat = true;
                State state[MaxChannels];
            };

            enum class Shape { LowShelf, Peak, HighShelf };

            // Recomputes the filters only when a setting changed since the last block.
            void Design()
            {
                std::array<float, 7> now{};
                for (int i = 0; i < 7; ++i)
                {
                    now[static_cast<size_t>(i)] = parameters[i].load();
                }
                if (now == _designed)
                {
                    return;
                }
                _designed = now;

                SetBand(_bands[0], Shape::LowShelf, now[0], now[1], 0.7071);
                SetBand(_bands[1], Shape::Peak, now[2], now[3], std::max(0.1f, now[4]));
                SetBand(_bands[2], Shape::HighShelf, now[5], now[6], 0.7071);
            }

            void SetBand(Band& band, Shape shape, double gainDb, double frequency, double q)
            {
                band.flat = std::fabs(gainDb) < 0.01;
                if (band.flat)
                {
                    return;
                }

                const double a = std::pow(10.0, gainDb / 40.0);
                const double w0 = 2.0 * Pi * std::clamp(frequency, 10.0, _sampleRate * 0.45) / _sampleRate;
                const double cosw = std::cos(w0);
                const double alpha = std::sin(w0) / (2.0 * q);
                double b0, b1, b2, a0, a1, a2;
                if (shape == Shape::Peak)
                {
                    b0 = 1 + alpha * a;
                    b1 = -2 * cosw;
                    b2 = 1 - alpha * a;
                    a0 = 1 + alpha / a;
                    a1 = -2 * cosw;
                    a2 = 1 - alpha / a;
                }
                else
                {
                    const double rootA = 2 * std::sqrt(a) * alpha;
                    if (shape == Shape::LowShelf)
                    {
                        b0 = a * ((a + 1) - (a - 1) * cosw + rootA);
                        b1 = 2 * a * ((a - 1) - (a + 1) * cosw);
                        b2 = a * ((a + 1) - (a - 1) * cosw - rootA);
                        a0 = (a + 1) + (a - 1) * cosw + rootA;
                        a1 = -2 * ((a - 1) + (a + 1) * cosw);
                        a2 = (a + 1) + (a - 1) * cosw - rootA;
                    }
                    else
                    {
                        b0 = a * ((a + 1) + (a - 1) * cosw + rootA);
                        b1 = -2 * a * ((a - 1) + (a + 1) * cosw);
                        b2 = a * ((a + 1) + (a - 1) * cosw - rootA);
                        a0 = (a + 1) - (a - 1) * cosw + rootA;
                        a1 = 2 * ((a - 1) - (a + 1) * cosw);
                        a2 = (a + 1) - (a - 1) * cosw - rootA;
                    }
                }

                band.b0 = b0 / a0;
                band.b1 = b1 / a0;
                band.b2 = b2 / a0;
                band.a1 = a1 / a0;
                band.a2 = a2 / a0;
            }

            double _sampleRate = 44100.0;
            Band _bands[3];
            std::array<float, 7> _designed{};
        };

        // ---------------------------------------------------------------- SonLimiter
        // Keeps the peaks under a ceiling: instant attack, smooth release, and a hard clip as a last guard.
        // Parameters: 0 ceiling dB, 1 release ms, 2 input gain dB.
        class Limiter final : public Effect
        {
        public:
            Limiter() { SetDefaults(*this, {-0.3f, 50.0f, 0.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                _gain = 1.0f;
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                const float ceiling = DbToGain(std::min(parameters[0].load(), 0.0f));
                const float release = SmoothingCoefficient(parameters[1].load(), _sampleRate);
                const float drive = DbToGain(parameters[2].load());
                float lowest = 1.0f;

                for (int n = 0; n < frames; ++n)
                {
                    float peak = 0.0f;
                    for (int c = 0; c < numChannels; ++c)
                    {
                        peak = std::max(peak, std::fabs(channels[c][n] * drive));
                    }

                    const float needed = peak > ceiling ? ceiling / peak : 1.0f;
                    _gain = needed < _gain ? needed : needed + (_gain - needed) * release;
                    lowest = std::min(lowest, _gain);

                    for (int c = 0; c < numChannels; ++c)
                    {
                        channels[c][n] = std::clamp(channels[c][n] * drive * _gain, -ceiling, ceiling);
                    }
                }

                _meter.store(static_cast<float>(-GainToDb(lowest)));
            }

            double Meter() const override { return _meter.load(); }

        private:
            double _sampleRate = 44100.0;
            float _gain = 1.0f;
            std::atomic<float> _meter{0.0f};
        };
    }

    std::unique_ptr<Effect> CreateEffect(EffectType type)
    {
        switch (type)
        {
        case EffectType::Verb:
            return std::make_unique<Verb>();
        case EffectType::Delay:
            return std::make_unique<Delay>();
        case EffectType::Compress:
            return std::make_unique<Compress>();
        case EffectType::EQ:
            return std::make_unique<Equalizer>();
        case EffectType::Limiter:
            return std::make_unique<Limiter>();
        }
        return nullptr;
    }

    void EffectChain::SetLayout(const int32_t* types, int32_t count)
    {
        std::vector<std::unique_ptr<Effect>> effects;
        for (int32_t i = 0; i < count; ++i)
        {
            auto effect = CreateEffect(static_cast<EffectType>(types[i]));
            if (effect != nullptr)
            {
                effects.push_back(std::move(effect));
            }
        }

        {
            std::lock_guard<std::mutex> lock(_mutex);
            // Prepared here, on the caller's thread, so the audio thread does not allocate.
            if (_sampleRate > 0.0)
            {
                for (auto& effect : effects)
                {
                    effect->Prepare(_sampleRate, _maxBlock);
                }
            }
            _effects.swap(effects);
        }
        // The replaced effects are freed here, outside the lock.
    }

    // The parameters and switches are atomics, and only the caller's thread ever replaces the effects,
    // so these need no lock and never hold up the audio thread.
    void EffectChain::SetParameter(int32_t slot, int32_t parameter, double value)
    {
        if (slot >= 0 && slot < static_cast<int32_t>(_effects.size()) && parameter >= 0 && parameter < MaxParameters)
        {
            _effects[static_cast<size_t>(slot)]->parameters[parameter].store(static_cast<float>(value));
        }
    }

    void EffectChain::SetEnabled(int32_t slot, bool enabled)
    {
        if (slot >= 0 && slot < static_cast<int32_t>(_effects.size()))
        {
            _effects[static_cast<size_t>(slot)]->enabled.store(enabled);
        }
    }

    void EffectChain::TakeLevels(int32_t slot, float& input, float& output)
    {
        input = output = 0.0f;
        if (slot >= 0 && slot < static_cast<int32_t>(_effects.size()))
        {
            auto& effect = *_effects[static_cast<size_t>(slot)];
            input = effect.inputPeak.exchange(0.0f);
            output = effect.outputPeak.exchange(0.0f);
        }
    }

    double EffectChain::Meter(int32_t slot)
    {
        if (slot >= 0 && slot < static_cast<int32_t>(_effects.size()))
        {
            return _effects[static_cast<size_t>(slot)]->Meter();
        }
        return 0.0;
    }

    void EffectChain::Process(float** channels, int numChannels, int frames, double sampleRate)
    {
        if (numChannels <= 0 || frames <= 0)
        {
            return;
        }

        std::unique_lock<std::mutex> lock(_mutex, std::try_to_lock);
        if (lock.owns_lock())
        {
            if (sampleRate != _sampleRate || frames > _maxBlock)
            {
                _sampleRate = sampleRate;
                _maxBlock = std::max(frames, _maxBlock);
                for (auto& effect : _effects)
                {
                    effect->Prepare(_sampleRate, _maxBlock);
                }
            }

            // Each effect's level is measured going in and coming out, for its IN and OUT meters.
            for (auto& effect : _effects)
            {
                StorePeak(effect->inputPeak, PeakOf(channels, numChannels, frames));
                if (effect->enabled.load())
                {
                    effect->Process(channels, numChannels, frames);
                }
                StorePeak(effect->outputPeak, PeakOf(channels, numChannels, frames));
            }
        }

        const float gain = this->gain.load();
        const float pan = std::clamp(this->pan.load(), -1.0f, 1.0f);
        float peak = 0.0f;
        for (int c = 0; c < numChannels; ++c)
        {
            float* samples = channels[c];
            float channelGain = gain;
            if (numChannels >= 2 && c == 0 && pan > 0.0f)
            {
                channelGain *= 1.0f - pan;
            }
            else if (numChannels >= 2 && c == 1 && pan < 0.0f)
            {
                channelGain *= 1.0f + pan;
            }

            for (int n = 0; n < frames; ++n)
            {
                samples[n] *= channelGain;
                peak = std::max(peak, std::fabs(samples[n]));
            }
        }

        float stored = _peak.load();
        while (peak > stored && !_peak.compare_exchange_weak(stored, peak))
        {
        }
    }

    void EffectChain::ProcessInterleaved(float* samples, int frames, int numChannels, double sampleRate)
    {
        if (numChannels <= 0 || frames <= 0)
        {
            return;
        }

        if (static_cast<int>(_scratch.size()) < numChannels || static_cast<int>(_scratch[0].size()) < frames)
        {
            _scratch.assign(static_cast<size_t>(numChannels), std::vector<float>(static_cast<size_t>(frames)));
            _scratchPointers.resize(static_cast<size_t>(numChannels));
        }
        for (int c = 0; c < numChannels; ++c)
        {
            _scratchPointers[static_cast<size_t>(c)] = _scratch[static_cast<size_t>(c)].data();
            for (int n = 0; n < frames; ++n)
            {
                _scratch[static_cast<size_t>(c)][static_cast<size_t>(n)] = samples[n * numChannels + c];
            }
        }

        Process(_scratchPointers.data(), numChannels, frames, sampleRate);

        for (int c = 0; c < numChannels; ++c)
        {
            for (int n = 0; n < frames; ++n)
            {
                samples[n * numChannels + c] = _scratch[static_cast<size_t>(c)][static_cast<size_t>(n)];
            }
        }
    }

    float EffectChain::TakePeak()
    {
        return _peak.exchange(0.0f);
    }
}
