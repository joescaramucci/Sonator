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

                // With a sidechain, the other track's level sets how much this one is turned down.
                float* const* detector = sidechainChannels > 0 ? sidechain : channels;
                const int detectorChannels = sidechainChannels > 0 ? sidechainChannels : numChannels;
                for (int n = 0; n < frames; ++n)
                {
                    float peak = 0.0f;
                    for (int c = 0; c < detectorChannels; ++c)
                    {
                        peak = std::max(peak, std::fabs(detector[c][n]));
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

        // ---------------------------------------------------------------- Voice tools (SonArmo, SonCoro)

        // Hears the pitch of a single voice every 10 ms: the voice brought down to about 12 kHz, its last 40 ms
        // looked at by the YIN method. Keeps the last note heard clearly, and the voice's period at the full rate.
        class PitchTracker
        {
        public:
            void Prepare(double sampleRate)
            {
                _sampleRate = sampleRate;
                _decimate = std::max(1, static_cast<int>(std::lround(sampleRate / 12000.0)));
                _lowRate = sampleRate / _decimate;
                _window = static_cast<int>(_lowRate * 0.040);
                _maxTau = std::min(_window - 1, static_cast<int>(_lowRate / 70.0));
                _minTau = std::max(2, static_cast<int>(_lowRate / 1050.0));
                _ring.assign(static_cast<size_t>(_window + _maxTau + 1), 0.0f);
                _linear.assign(_ring.size(), 0.0f);
                _d.assign(static_cast<size_t>(_maxTau + 1), 0.0);
                _ringPosition = 0;
                _ringFilled = 0;
                _sum = 0.0f;
                _summed = 0;
                _hop = std::max(1, static_cast<int>(_lowRate * 0.010));
                _sinceAnalysis = 0;
                Sung = -1.0;
                Period = 0.0;
            }

            // Takes one sample of the voice; true when a new note has just been heard.
            bool Push(float sample)
            {
                _sum += sample;
                if (++_summed < _decimate)
                {
                    return false;
                }

                _ring[_ringPosition] = _sum / static_cast<float>(_decimate);
                _ringPosition = (_ringPosition + 1) % _ring.size();
                _ringFilled = std::min(_ringFilled + 1, _ring.size());
                _sum = 0.0f;
                _summed = 0;
                if (++_sinceAnalysis < _hop || _ringFilled < _ring.size())
                {
                    return false;
                }

                _sinceAnalysis = 0;
                const double heard = Detect();
                if (heard <= 0)
                {
                    return false;
                }

                Sung = heard;
                Period = _sampleRate / (440.0 * std::exp2((heard - 69.0) / 12.0));
                return true;
            }

            // The last note heard, in MIDI notes with fractions (-1 until one is), and its period in samples (0).
            double Sung = -1.0;
            double Period = 0.0;

        private:
            // The pitch of the last 40 ms, by YIN; -1 when it is not clearly pitched or too quiet.
            double Detect()
            {
                const size_t size = _ring.size();
                for (size_t i = 0; i < size; ++i)
                {
                    _linear[i] = _ring[(_ringPosition + i) % size];
                }

                double energy = 0.0;
                for (int j = 0; j < _window; ++j)
                {
                    energy += static_cast<double>(_linear[static_cast<size_t>(j)]) * _linear[static_cast<size_t>(j)];
                }

                if (std::sqrt(energy / _window) < 0.004)
                {
                    return -1.0;
                }

                _d[0] = 1.0;
                double running = 0.0;
                for (int tau = 1; tau <= _maxTau; ++tau)
                {
                    double sum = 0.0;
                    for (int j = 0; j < _window; ++j)
                    {
                        const double delta = static_cast<double>(_linear[static_cast<size_t>(j)]) - _linear[static_cast<size_t>(j + tau)];
                        sum += delta * delta;
                    }

                    running += sum;
                    _d[static_cast<size_t>(tau)] = running > 0.0 ? sum * tau / running : 1.0;
                }

                int best = -1;
                for (int tau = _minTau; tau <= _maxTau; ++tau)
                {
                    if (_d[static_cast<size_t>(tau)] < 0.15)
                    {
                        while (tau + 1 <= _maxTau && _d[static_cast<size_t>(tau + 1)] < _d[static_cast<size_t>(tau)])
                        {
                            ++tau;
                        }

                        best = tau;
                        break;
                    }
                }

                if (best < 0)
                {
                    return -1.0;
                }

                double refined = best;
                if (best > _minTau && best < _maxTau)
                {
                    const double a = _d[static_cast<size_t>(best - 1)];
                    const double b = _d[static_cast<size_t>(best)];
                    const double c = _d[static_cast<size_t>(best + 1)];
                    const double denominator = a - 2.0 * b + c;
                    if (std::fabs(denominator) > 1e-12)
                    {
                        refined = best + 0.5 * (a - c) / denominator;
                    }
                }

                return 69.0 + 12.0 * std::log2(_lowRate / refined / 440.0);
            }

            double _sampleRate = 44100.0;
            int _decimate = 4;
            double _lowRate = 11025.0;
            int _window = 441;
            int _minTau = 10;
            int _maxTau = 157;
            int _hop = 110;
            int _sinceAnalysis = 0;
            std::vector<float> _ring;
            std::vector<float> _linear;
            std::vector<double> _d;
            size_t _ringPosition = 0;
            size_t _ringFilled = 0;
            float _sum = 0.0f;
            int _summed = 0;
        };

        // The voice's last moments, to be read back at any delay.
        class VoiceLine
        {
        public:
            void Prepare(size_t length)
            {
                _line.assign(length, 0.0f);
                _write = 0;
            }

            void Write(float sample)
            {
                _line[_write] = sample;
            }

            void Advance()
            {
                _write = (_write + 1) % _line.size();
            }

            float Read(double delay) const
            {
                const size_t length = _line.size();
                const double position = static_cast<double>(_write) - delay + static_cast<double>(length);
                const size_t base = static_cast<size_t>(position);
                const float fraction = static_cast<float>(position - static_cast<double>(base));
                const float a = _line[base % length];
                const float b = _line[(base + 1) % length];
                return a + (b - a) * fraction;
            }

        private:
            std::vector<float> _line;
            size_t _write = 0;
        };

        // Moves a voice's pitch: two read heads, each sliding through its span of the line at the new speed and faded
        // in and out across it. A head that runs off its span starts again half a span from the other, the span an
        // even number of the voice's periods, so the two read the same wave in step and never cancel as they cross
        // (pitch-synchronous, as PSOLA). It reads Delay later than the heads' own place.
        struct PitchShifter
        {
            static constexpr double MinDelay = 8.0;

            double delay[2] = {MinDelay, MinDelay};
            double span[2] = {1764.0, 1764.0};

            void Reset(int grain)
            {
                span[0] = span[1] = grain;
                delay[0] = MinDelay + grain * 0.25;
                delay[1] = MinDelay + grain * 0.75;
            }

            // A head's span for the voice's period (in samples, 0 when not known) and the grain.
            static double IdealSpan(double period, int grain)
            {
                if (period <= 0.0)
                {
                    return grain;
                }

                const double periods = std::max(1.0, std::ceil(grain / (2.0 * period)));
                return std::min(2.0 * periods * period, 2.5 * grain);
            }

            // The next sample of the voice moved by semitones, read extra samples later.
            float Next(const VoiceLine& line, double semitones, double period, int grain, double extra)
            {
                const double speed = std::exp2(semitones / 12.0) - 1.0;
                float sum = 0.0f;
                float weights = 0.0f;
                for (int head = 0; head < 2; ++head)
                {
                    double& at = delay[head];
                    double& width = span[head];
                    at -= speed;
                    if (at < MinDelay || at > MinDelay + width)
                    {
                        const double other = delay[1 - head];
                        width = IdealSpan(period, grain);
                        at = speed > 0 ? other + width * 0.5 : other - width * 0.5;
                        at = std::clamp(at, MinDelay, MinDelay + width);
                    }

                    const double place = std::clamp((at - MinDelay) / width, 0.0, 1.0);
                    const float weight = static_cast<float>(std::pow(std::sin(Pi * place), 2.0));
                    sum += line.Read(at + extra) * weight;
                    weights += weight;
                }

                return weights > 1e-3f ? sum / weights : 0.0f;
            }
        };

        // ---------------------------------------------------------------- SonArmo
        // A harmonizer for a voice: two harmony voices made from the sung one, each an interval away in the key's
        // scale (the sung note is heard every 10 ms, so a third is major or minor as the scale has it there) or, with
        // no key, a fixed interval; each at its own level, spread left and right. The singer's own intonation is
        // kept: the harmony is as far from its scale note as the voice is from its. The pitch is moved by a
        // PitchShifter, about 40 ms behind the voice.
        // Parameters: 0 key (0 none, 1..12 C..B), 1 minor (0 or 1), 2 voice 1 and 3 voice 2 in scale steps (-7..7,
        // 0 none), 4 and 5 their levels %, 6 width %, 7 dry %.
        class Harmonizer final : public Effect
        {
        public:
            Harmonizer() { SetDefaults(*this, {0.0f, 0.0f, 2.0f, 0.0f, 70.0f, 60.0f, 60.0f, 100.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                _grain = std::max(256, static_cast<int>(sampleRate * 0.040));
                _line.Prepare(static_cast<size_t>(_grain * 3 + 64));
                _tracker.Prepare(sampleRate);
                _meter.store(-1.0f);
                for (auto& voice : _voices)
                {
                    voice = Voice{};
                    voice.shifter.Reset(_grain);
                }
            }

            double Meter() const override { return _meter.load(); }

            void Process(float** channels, int numChannels, int frames) override
            {
                const int key = static_cast<int>(std::lround(parameters[0].load()));
                const bool minor = parameters[1].load() > 0.5f;
                const int steps[2] = {static_cast<int>(std::lround(parameters[2].load())), static_cast<int>(std::lround(parameters[3].load()))};
                const float levels[2] = {std::clamp(parameters[4].load() / 100.0f, 0.0f, 1.0f), std::clamp(parameters[5].load() / 100.0f, 0.0f, 1.0f)};
                const float width = std::clamp(parameters[6].load() / 100.0f, 0.0f, 1.0f);
                const float dry = std::clamp(parameters[7].load() / 100.0f, 0.0f, 1.0f);
                const float glide = SmoothingCoefficient(12.0, _sampleRate);

                // Each voice's place across the stereo field: the first to the left, the second to the right.
                float panLeft[2];
                float panRight[2];
                for (int v = 0; v < 2; ++v)
                {
                    const float pan = (v == 0 ? -1.0f : 1.0f) * width;
                    panLeft[v] = static_cast<float>(std::sqrt(0.5 * (1.0 - pan)));
                    panRight[v] = static_cast<float>(std::sqrt(0.5 * (1.0 + pan)));
                }

                for (int n = 0; n < frames; ++n)
                {
                    const float mono = numChannels > 1 ? (channels[0][n] + channels[1][n]) * 0.5f : channels[0][n];
                    if (_tracker.Push(mono))
                    {
                        _meter.store(static_cast<float>(_tracker.Sung));
                    }

                    for (int v = 0; v < 2; ++v)
                    {
                        auto& voice = _voices[v];
                        if (steps[v] == 0)
                        {
                            voice.gate = 0.0f;
                            continue;
                        }

                        // The shift the voice is gliding to; in a key, from the note last sung (silent until one is).
                        double target = voice.target;
                        bool ready = true;
                        if (key <= 0)
                        {
                            target = ChromaticShift(steps[v]);
                        }
                        else if (_tracker.Sung > 0)
                        {
                            target = ScaleShift(_tracker.Sung, steps[v], key - 1, minor);
                        }
                        else
                        {
                            ready = false;
                        }

                        voice.target = target;
                        voice.shift = target + (voice.shift - target) * glide;
                        voice.gate = (ready ? 1.0f : 0.0f) + (voice.gate - (ready ? 1.0f : 0.0f)) * glide;
                    }

                    _line.Write(mono);
                    float left = 0.0f;
                    float right = 0.0f;
                    for (int v = 0; v < 2; ++v)
                    {
                        auto& voice = _voices[v];
                        if (steps[v] == 0 && voice.gate < 1e-4f)
                        {
                            continue;
                        }

                        const float sample = voice.shifter.Next(_line, voice.shift, _tracker.Period, _grain, 0.0) * levels[v] * voice.gate;
                        left += sample * panLeft[v];
                        right += sample * panRight[v];
                    }

                    _line.Advance();
                    if (numChannels > 1)
                    {
                        channels[0][n] = channels[0][n] * dry + left;
                        channels[1][n] = channels[1][n] * dry + right;
                    }
                    else
                    {
                        channels[0][n] = channels[0][n] * dry + (left + right) * 0.7071f;
                    }
                }
            }

            // The shift for a number of steps of the major scale, with no key: a third is four semitones.
            static double ChromaticShift(int steps)
            {
                static const int major[] = {0, 2, 4, 5, 7, 9, 11, 12};
                const int size = std::min(std::abs(steps), 7);
                return steps < 0 ? -major[size] : major[size];
            }

            // The shift that takes the note sung (in MIDI notes with fractions) a number of steps along the scale of
            // the key, keeping how far off its scale note the singer is.
            static double ScaleShift(double sung, int steps, int tonic, bool minor)
            {
                static const int majorScale[] = {0, 2, 4, 5, 7, 9, 11};
                static const int minorScale[] = {0, 2, 3, 5, 7, 8, 10};
                const int* scale = minor ? minorScale : majorScale;

                // The scale note nearest the voice.
                int best = 0;
                int bestDegree = 0;
                double bestDistance = 1e9;
                const int rounded = static_cast<int>(std::lround(sung));
                for (int note = rounded - 2; note <= rounded + 2; ++note)
                {
                    const int pitchClass = ((note - tonic) % 12 + 12) % 12;
                    for (int degree = 0; degree < 7; ++degree)
                    {
                        if (scale[degree] == pitchClass && std::fabs(note - sung) < bestDistance)
                        {
                            bestDistance = std::fabs(note - sung);
                            best = note;
                            bestDegree = degree;
                        }
                    }
                }

                const int index = bestDegree + steps;
                const int octaves = static_cast<int>(std::floor(index / 7.0));
                const int degree = index - octaves * 7;
                const int target = best - scale[bestDegree] + scale[degree] + 12 * octaves;
                return static_cast<double>(target - best);
            }

        private:
            struct Voice
            {
                double shift = 0.0;
                double target = 0.0;
                float gate = 0.0f;
                PitchShifter shifter;
            };

            double _sampleRate = 44100.0;
            int _grain = 1764;
            VoiceLine _line;
            PitchTracker _tracker;
            std::atomic<float> _meter{-1.0f};
            Voice _voices[2];
        };

        // ---------------------------------------------------------------- SonCoro
        // A choir from one voice: singers who each sing it a few cents off the others, a little late, each wavering
        // slowly in pitch on their own, spread across the stereo field; with a section an octave below and one an
        // octave above if wanted. Each singer is a PitchShifter, its delay, detune, wavering and place drawn once at
        // random (always the same, so the choir sounds alike each time). The voice is heard as it is at Mix 0.
        // Parameters: 0 singers (2..16), 1 detune cents, 2 timing ms, 3 wavering %, 4 low section %, 5 high
        // section %, 6 width %, 7 mix %.
        class Choir final : public Effect
        {
        public:
            Choir() { SetDefaults(*this, {8.0f, 15.0f, 25.0f, 40.0f, 0.0f, 0.0f, 80.0f, 50.0f}); }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                _grain = std::max(256, static_cast<int>(sampleRate * 0.040));
                _line.Prepare(static_cast<size_t>(_grain * 3 + sampleRate * 0.065 + 64));
                _tracker.Prepare(sampleRate);

                // Each singer's character, drawn from a fixed seed: where it stands, how late and how far off it
                // sings, and how its pitch wavers (two slow cycles).
                unsigned state = 0x5C0A0u;
                auto random = [&state]()
                {
                    state = state * 1664525u + 1013904223u;
                    return static_cast<double>(state >> 8) / 16777216.0;
                };
                auto draw = [&](Singer& singer, int i)
                {
                    singer = Singer{};
                    singer.shifter.Reset(_grain);
                    singer.detune = random() * 2.0 - 1.0;
                    singer.lateness = random();
                    singer.pan = (i % 2 == 0 ? -1.0 : 1.0) * (0.25 + 0.75 * random());
                    singer.rateA = 0.15 + 0.35 * random();
                    singer.rateB = 0.45 + 0.6 * random();
                    singer.phaseA = random();
                    singer.phaseB = random();
                    singer.gain = 0.8 + 0.4 * random();
                };
                for (int i = 0; i < Singers; ++i)
                {
                    draw(_singers[i], i);
                }

                for (int i = 0; i < 4; ++i)
                {
                    draw(_lowSection[i], i);
                    draw(_highSection[i], i + 1);
                }
            }

            void Process(float** channels, int numChannels, int frames) override
            {
                const int singers = std::clamp(static_cast<int>(std::lround(parameters[0].load())), 2, 16);
                const double detune = std::clamp(static_cast<double>(parameters[1].load()), 0.0, 60.0) / 100.0; // semitones
                const double timing = std::clamp(static_cast<double>(parameters[2].load()), 0.0, 60.0) * 0.001 * _sampleRate;
                const double wavering = std::clamp(static_cast<double>(parameters[3].load()) / 100.0, 0.0, 1.0) * 0.25; // semitones
                const float low = std::clamp(parameters[4].load() / 100.0f, 0.0f, 1.0f);
                const float high = std::clamp(parameters[5].load() / 100.0f, 0.0f, 1.0f);
                const float width = std::clamp(parameters[6].load() / 100.0f, 0.0f, 1.0f);
                const float mix = std::clamp(parameters[7].load() / 100.0f, 0.0f, 1.0f);

                // The main section plays the voice's own octave; up to four singers each sing in the low and high
                // sections, taken from the end of the list.
                const int sectionSize = 4;
                const float unisonGain = 1.0f / std::sqrt(static_cast<float>(singers));
                const float sectionGain = 1.0f / std::sqrt(static_cast<float>(sectionSize));
                const double twoPi = 2.0 * Pi;

                for (int n = 0; n < frames; ++n)
                {
                    const float mono = numChannels > 1 ? (channels[0][n] + channels[1][n]) * 0.5f : channels[0][n];
                    _tracker.Push(mono);
                    _line.Write(mono);

                    float left = 0.0f;
                    float right = 0.0f;
                    auto sing = [&](Singer& singer, double octave, float gain)
                    {
                        singer.phaseA += singer.rateA / _sampleRate;
                        singer.phaseB += singer.rateB / _sampleRate;
                        singer.phaseA -= std::floor(singer.phaseA);
                        singer.phaseB -= std::floor(singer.phaseB);
                        const double waver = wavering * (0.65 * std::sin(twoPi * singer.phaseA) + 0.35 * std::sin(twoPi * singer.phaseB));
                        const double semitones = octave + singer.detune * detune + waver;
                        const float sample = singer.shifter.Next(_line, semitones, _tracker.Period, _grain, singer.lateness * timing)
                            * gain * static_cast<float>(singer.gain);
                        const double pan = singer.pan * width;
                        left += sample * static_cast<float>(std::sqrt(0.5 * (1.0 - pan)));
                        right += sample * static_cast<float>(std::sqrt(0.5 * (1.0 + pan)));
                    };

                    for (int i = 0; i < singers; ++i)
                    {
                        sing(_singers[i], 0.0, unisonGain);
                    }

                    if (low > 1e-4f)
                    {
                        for (int i = 0; i < sectionSize; ++i)
                        {
                            sing(_lowSection[i], -12.0, sectionGain * low);
                        }
                    }

                    if (high > 1e-4f)
                    {
                        for (int i = 0; i < sectionSize; ++i)
                        {
                            sing(_highSection[i], 12.0, sectionGain * high);
                        }
                    }

                    _line.Advance();
                    if (numChannels > 1)
                    {
                        channels[0][n] = channels[0][n] * (1.0f - mix) + left * mix;
                        channels[1][n] = channels[1][n] * (1.0f - mix) + right * mix;
                    }
                    else
                    {
                        channels[0][n] = channels[0][n] * (1.0f - mix) + (left + right) * 0.7071f * mix;
                    }
                }
            }

        private:
            static constexpr int Singers = 16;

            struct Singer
            {
                PitchShifter shifter;
                double detune = 0.0;   // -1 to 1, of the detune knob
                double lateness = 0.0; // 0 to 1, of the timing knob
                double pan = 0.0;
                double rateA = 0.3;
                double rateB = 0.7;
                double phaseA = 0.0;
                double phaseB = 0.0;
                double gain = 1.0;
            };

            double _sampleRate = 44100.0;
            int _grain = 1764;
            VoiceLine _line;
            PitchTracker _tracker;
            Singer _singers[Singers];
            Singer _lowSection[4];
            Singer _highSection[4];
        };

        // ---------------------------------------------------------------- SonMast
        // Mastering for the whole song, on the master: a tone EQ (low shelf at 100 Hz, presence at 3 kHz, air shelf
        // at 10 kHz), a gentle glue compressor (stereo, its ratio and threshold from one knob, its loss made up), a
        // stereo width (the sides turned up or down), then drive into a look-ahead limiter that never lets a peak
        // over the ceiling. Its meter is the loudness of the last 3 s in LUFS (short-term, K-weighted, BS.1770).
        // Parameters: 0 low dB, 1 presence dB, 2 air dB, 3 glue %, 4 width %, 5 drive dB, 6 ceiling dB.
        class Mastering final : public Effect
        {
        public:
            Mastering() { SetDefaults(*this, {0.0f, 0.0f, 0.0f, 30.0f, 100.0f, 0.0f, -1.0f}); }

            int Latency() const override { return _lookahead; }

            void Prepare(double sampleRate, int /*maxBlock*/) override
            {
                _sampleRate = sampleRate;
                for (auto& filter : _tone)
                {
                    filter.Reset();
                }
                for (auto& filter : _weighting)
                {
                    filter.Reset();
                }
                _designed.fill(std::nanf(""));

                // The K-weighting of BS.1770: a high shelf of +4 dB from about 1.7 kHz, and a high-pass at 38 Hz.
                _weighting[0].Design(Shape::HighShelf, 4.0, 1681.97, 0.7072, sampleRate);
                _weighting[1].Design(Shape::HighPass, 0.0, 38.135, 0.5003, sampleRate);

                _envelope = 0.0;
                _lookahead = std::max(1, static_cast<int>(sampleRate * 0.002));
                for (auto& delay : _delay)
                {
                    delay.assign(static_cast<size_t>(_lookahead + 1), 0.0f);
                }
                _peaks.assign(static_cast<size_t>(_lookahead + 1), 0.0f);
                _delayIndex = 0;
                _limit = 1.0;
                _blockEnergy = 0.0;
                _blockFrames = 0;
                _blocks.fill(0.0);
                _blockIndex = 0;
                _meter.store(-70.0f);
            }

            double Meter() const override { return _meter.load(); }

            void Process(float** channels, int numChannels, int frames) override
            {
                Design();
                const int sides = std::min(numChannels, 2);
                const double glue = std::clamp(static_cast<double>(parameters[3].load()) / 100.0, 0.0, 1.0);
                const double ratio = 1.0 + 3.0 * glue;
                const double threshold = -6.0 - 18.0 * glue;
                const double makeup = -threshold * (1.0 - 1.0 / ratio) * 0.5;
                const float width = std::clamp(parameters[4].load() / 100.0f, 0.0f, 2.0f);
                const double drive = DbToGain(std::clamp(static_cast<double>(parameters[5].load()), 0.0, 18.0) + (glue > 0 ? makeup : 0.0));
                const double ceiling = DbToGain(std::clamp(static_cast<double>(parameters[6].load()), -6.0, 0.0));
                const double attack = std::exp(-1.0 / (0.030 * _sampleRate));
                const double release = std::exp(-1.0 / (0.250 * _sampleRate));
                const double limitRelease = std::exp(-1.0 / (0.080 * _sampleRate));
                const int blockLength = static_cast<int>(_sampleRate * 0.1);

                for (int n = 0; n < frames; ++n)
                {
                    double left = channels[0][n];
                    double right = sides > 1 ? channels[1][n] : left;

                    // Tone.
                    for (int band = 0; band < 3; ++band)
                    {
                        if (!_flat[band])
                        {
                            left = _tone[band].Run(0, left);
                            right = _tone[band].Run(1, right);
                        }
                    }

                    // Glue: the louder side's level, smoothed, sets how far both are turned down above the threshold.
                    if (glue > 0.0)
                    {
                        const double level = std::max(std::fabs(left), std::fabs(right));
                        const double coefficient = level > _envelope ? attack : release;
                        _envelope = level + (_envelope - level) * coefficient;
                        const double over = GainToDb(_envelope) - threshold;
                        const double knee = 6.0;
                        double reduction = 0.0;
                        if (over > knee / 2)
                        {
                            reduction = over * (1.0 - 1.0 / ratio);
                        }
                        else if (over > -knee / 2)
                        {
                            const double into = over + knee / 2;
                            reduction = (1.0 - 1.0 / ratio) * into * into / (2.0 * knee);
                        }

                        const double gain = DbToGain(-reduction);
                        left *= gain;
                        right *= gain;
                    }

                    // Width: the sides against the middle.
                    if (sides > 1 && std::fabs(width - 1.0f) > 1e-4f)
                    {
                        const double mid = (left + right) * 0.5;
                        const double side = (left - right) * 0.5 * width;
                        left = mid + side;
                        right = mid - side;
                    }

                    left *= drive;
                    right *= drive;

                    // The limiter looks 2 ms ahead: the gain is already down when the peak comes out.
                    _delay[0][_delayIndex] = static_cast<float>(left);
                    _delay[1][_delayIndex] = static_cast<float>(right);
                    _peaks[_delayIndex] = static_cast<float>(std::max(std::fabs(left), std::fabs(right)));
                    _delayIndex = (_delayIndex + 1) % _peaks.size();
                    float ahead = 0.0f;
                    for (const float peak : _peaks)
                    {
                        ahead = std::max(ahead, peak);
                    }

                    const double needed = ahead > ceiling ? ceiling / ahead : 1.0;
                    _limit = needed < _limit ? needed : needed + (_limit - needed) * limitRelease;
                    const double outLeft = std::clamp(_delay[0][_delayIndex] * _limit, -ceiling, ceiling);
                    const double outRight = std::clamp(_delay[1][_delayIndex] * _limit, -ceiling, ceiling);
                    channels[0][n] = static_cast<float>(outLeft);
                    if (sides > 1)
                    {
                        channels[1][n] = static_cast<float>(outRight);
                    }

                    // Loudness: the K-weighted energy of both sides, in blocks of 100 ms, the last 30 of them.
                    const double weightedLeft = _weighting[1].Run(0, _weighting[0].Run(0, outLeft));
                    const double weightedRight = _weighting[1].Run(1, _weighting[0].Run(1, outRight));
                    _blockEnergy += weightedLeft * weightedLeft + (sides > 1 ? weightedRight * weightedRight : weightedLeft * weightedLeft);
                    if (++_blockFrames >= blockLength)
                    {
                        _blocks[_blockIndex] = _blockEnergy / _blockFrames;
                        _blockIndex = (_blockIndex + 1) % _blocks.size();
                        _blockEnergy = 0.0;
                        _blockFrames = 0;
                        double mean = 0.0;
                        for (const double block : _blocks)
                        {
                            mean += block;
                        }
                        mean /= static_cast<double>(_blocks.size());
                        _meter.store(static_cast<float>(mean > 1e-10 ? -0.691 + 10.0 * std::log10(mean) : -70.0));
                    }
                }
            }

        private:
            enum class Shape { LowShelf, Peak, HighShelf, HighPass };

            // A two-pole filter of the RBJ cookbook, for both sides.
            struct Filter
            {
                double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
                double x1[2]{}, x2[2]{}, y1[2]{}, y2[2]{};

                void Reset()
                {
                    for (int c = 0; c < 2; ++c)
                    {
                        x1[c] = x2[c] = y1[c] = y2[c] = 0.0;
                    }
                }

                double Run(int c, double x)
                {
                    const double y = b0 * x + b1 * x1[c] + b2 * x2[c] - a1 * y1[c] - a2 * y2[c];
                    x2[c] = x1[c];
                    x1[c] = x;
                    y2[c] = y1[c];
                    y1[c] = std::fabs(y) < 1e-20 ? 0.0 : y;
                    return y;
                }

                void Design(Shape shape, double gainDb, double frequency, double q, double sampleRate)
                {
                    const double a = std::pow(10.0, gainDb / 40.0);
                    const double w0 = 2.0 * Pi * std::clamp(frequency, 10.0, sampleRate * 0.45) / sampleRate;
                    const double cosw = std::cos(w0);
                    const double alpha = std::sin(w0) / (2.0 * q);
                    double nb0, nb1, nb2, na0, na1, na2;
                    if (shape == Shape::HighPass)
                    {
                        nb0 = (1 + cosw) / 2;
                        nb1 = -(1 + cosw);
                        nb2 = (1 + cosw) / 2;
                        na0 = 1 + alpha;
                        na1 = -2 * cosw;
                        na2 = 1 - alpha;
                    }
                    else if (shape == Shape::Peak)
                    {
                        nb0 = 1 + alpha * a;
                        nb1 = -2 * cosw;
                        nb2 = 1 - alpha * a;
                        na0 = 1 + alpha / a;
                        na1 = -2 * cosw;
                        na2 = 1 - alpha / a;
                    }
                    else
                    {
                        const double rootA = 2 * std::sqrt(a) * alpha;
                        if (shape == Shape::LowShelf)
                        {
                            nb0 = a * ((a + 1) - (a - 1) * cosw + rootA);
                            nb1 = 2 * a * ((a - 1) - (a + 1) * cosw);
                            nb2 = a * ((a + 1) - (a - 1) * cosw - rootA);
                            na0 = (a + 1) + (a - 1) * cosw + rootA;
                            na1 = -2 * ((a - 1) + (a + 1) * cosw);
                            na2 = (a + 1) + (a - 1) * cosw - rootA;
                        }
                        else
                        {
                            nb0 = a * ((a + 1) + (a - 1) * cosw + rootA);
                            nb1 = -2 * a * ((a - 1) + (a + 1) * cosw);
                            nb2 = a * ((a + 1) + (a - 1) * cosw - rootA);
                            na0 = (a + 1) - (a - 1) * cosw + rootA;
                            na1 = 2 * ((a - 1) - (a + 1) * cosw);
                            na2 = (a + 1) - (a - 1) * cosw - rootA;
                        }
                    }

                    b0 = nb0 / na0;
                    b1 = nb1 / na0;
                    b2 = nb2 / na0;
                    a1 = na1 / na0;
                    a2 = na2 / na0;
                }
            };

            // The tone EQ again only when its knobs changed.
            void Design()
            {
                const std::array<float, 3> now{parameters[0].load(), parameters[1].load(), parameters[2].load()};
                if (now == _designed)
                {
                    return;
                }

                _designed = now;
                _tone[0].Design(Shape::LowShelf, std::clamp(now[0], -6.0f, 6.0f), 100.0, 0.7071, _sampleRate);
                _tone[1].Design(Shape::Peak, std::clamp(now[1], -6.0f, 6.0f), 3000.0, 0.8, _sampleRate);
                _tone[2].Design(Shape::HighShelf, std::clamp(now[2], -6.0f, 6.0f), 10000.0, 0.7071, _sampleRate);
                for (int band = 0; band < 3; ++band)
                {
                    _flat[band] = std::fabs(now[static_cast<size_t>(band)]) < 0.01f;
                }
            }

            double _sampleRate = 44100.0;
            Filter _tone[3];
            bool _flat[3] = {true, true, true};
            std::array<float, 3> _designed{};
            Filter _weighting[2];
            double _envelope = 0.0;
            int _lookahead = 96;
            std::vector<float> _delay[2];
            std::vector<float> _peaks;
            size_t _delayIndex = 0;
            double _limit = 1.0;
            double _blockEnergy = 0.0;
            int _blockFrames = 0;
            std::array<double, 30> _blocks{};
            size_t _blockIndex = 0;
            std::atomic<float> _meter{-70.0f};
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
        case EffectType::Harmonizer:
            return std::make_unique<Harmonizer>();
        case EffectType::Choir:
            return std::make_unique<Choir>();
        case EffectType::Mastering:
            return std::make_unique<Mastering>();
        }
        return nullptr;
    }

    namespace
    {
        // Holds the place of an effect that could not be made, so the slots keep their numbers.
        class PassThrough final : public Effect
        {
        public:
            void Prepare(double, int) override {}
            void Process(float**, int, int) override {}
        };
    }

    namespace
    {
        std::atomic<uint64_t> g_block{1};
    }

    uint64_t CurrentBlock() { return g_block.load(std::memory_order_relaxed); }

    void NextBlock() { g_block.fetch_add(1, std::memory_order_relaxed); }

    void EffectChain::SetSidechain(int32_t slot, const std::shared_ptr<EffectChain>& source)
    {
        std::lock_guard<std::mutex> lock(_mutex);
        if (slot >= 0 && slot < static_cast<int32_t>(_effects.size()))
        {
            _effects[static_cast<size_t>(slot)]->sidechainSource = source.get() == this ? std::shared_ptr<EffectChain>() : source;
        }
        if (source != nullptr)
        {
            source->_hasListeners.store(true);
        }
    }

    bool EffectChain::ReadTap(uint64_t block, float** channels, int& numChannels, int frames)
    {
        const size_t slot = static_cast<size_t>(block % 2);
        if (_tapBlock[slot].load(std::memory_order_acquire) != block || _tapFrames[slot].load() < frames)
        {
            return false;
        }

        numChannels = _tapChannels[slot].load();
        for (int c = 0; c < numChannels; ++c)
        {
            channels[c] = _tap[slot][c].data();
        }
        return numChannels > 0;
    }

    void EffectChain::SetLayout(const int32_t* types, int32_t count)
    {
        SetLayout(types, nullptr, count, [](int32_t type, void*) { return CreateEffect(static_cast<EffectType>(type)); });
    }

    void EffectChain::SetLayout(const int32_t* types, void* const* identities, int32_t count, const EffectFactory& make)
    {
        // An effect with an identity (a plug-in) that is still in the chain keeps running as it is, sound and
        // state, wherever it moves to. The others are made afresh. Only the caller's thread replaces the
        // effects, so reading them here needs no lock.
        std::vector<int32_t> kept(static_cast<size_t>(std::max(count, 0)), -1);
        std::vector<bool> claimed(_effects.size(), false);
        std::vector<std::unique_ptr<Effect>> fresh(static_cast<size_t>(std::max(count, 0)));
        for (int32_t i = 0; i < count; ++i)
        {
            void* identity = identities != nullptr ? identities[i] : nullptr;
            for (size_t j = 0; identity != nullptr && j < _effects.size(); ++j)
            {
                if (!claimed[j] && _effects[j]->Identity() == identity)
                {
                    kept[static_cast<size_t>(i)] = static_cast<int32_t>(j);
                    claimed[j] = true;
                    break;
                }
            }

            if (kept[static_cast<size_t>(i)] < 0)
            {
                auto effect = make(types[i], identity);
                fresh[static_cast<size_t>(i)] = effect != nullptr ? std::move(effect) : std::make_unique<PassThrough>();
            }
        }

        // Prepared here, on the caller's thread and outside the lock, so the audio thread neither allocates
        // nor waits for a plug-in that is slow to start.
        double sampleRate = 0.0;
        int maxBlock = 0;
        {
            std::lock_guard<std::mutex> lock(_mutex);
            sampleRate = _sampleRate;
            maxBlock = _maxBlock;
        }
        if (sampleRate > 0.0)
        {
            for (auto& effect : fresh)
            {
                if (effect != nullptr)
                {
                    effect->Prepare(sampleRate, maxBlock);
                }
            }
        }

        std::vector<std::unique_ptr<Effect>> effects(fresh.size());
        {
            std::lock_guard<std::mutex> lock(_mutex);
            const bool formatChanged = _sampleRate != sampleRate || _maxBlock != maxBlock;
            for (size_t i = 0; i < effects.size(); ++i)
            {
                if (kept[i] >= 0)
                {
                    effects[i] = std::move(_effects[static_cast<size_t>(kept[i])]);
                }
                else
                {
                    effects[i] = std::move(fresh[i]);
                    if (formatChanged && _sampleRate > 0.0)
                    {
                        effects[i]->Prepare(_sampleRate, _maxBlock); // the output changed meanwhile: rare
                    }
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

            // Each effect's level is measured going in and coming out, for its IN and OUT meters. An effect with a
            // sidechain hears its source's sound from the block before.
            const uint64_t block = CurrentBlock();
            int latency = 0;
            for (auto& effect : _effects)
            {
                StorePeak(effect->inputPeak, PeakOf(channels, numChannels, frames));
                if (effect->enabled.load())
                {
                    effect->sidechainChannels = 0;
                    if (auto source = effect->sidechainSource.lock())
                    {
                        int count = 0;
                        if (source->ReadTap(block - 1, effect->sidechain, count, frames))
                        {
                            effect->sidechainChannels = count;
                        }
                    }
                    effect->Process(channels, numChannels, frames);
                    latency += std::max(0, effect->Latency());
                }
                StorePeak(effect->outputPeak, PeakOf(channels, numChannels, frames));
            }
            _latency.store(latency, std::memory_order_relaxed);
        }

        const float gain = this->gain.load();
        const float pan = std::clamp(this->pan.load(), -1.0f, 1.0f);

        // A change of gain or pan, as automation makes many times a second, glides over a few milliseconds
        // instead of jumping, which would click.
        const int glideFrames = std::min(frames, std::max(1, static_cast<int>(sampleRate * 0.01)));
        float peak = 0.0f;
        for (int c = 0; c < numChannels; ++c)
        {
            float* samples = channels[c];
            float channelGain = gain;
            float channelPeak = 0.0f;
            if (numChannels >= 2 && c == 0 && pan > 0.0f)
            {
                channelGain *= 1.0f - pan;
            }
            else if (numChannels >= 2 && c == 1 && pan < 0.0f)
            {
                channelGain *= 1.0f + pan;
            }

            int n = 0;
            if (c < static_cast<int>(_appliedGains.size()))
            {
                float& applied = _appliedGains[static_cast<size_t>(c)];
                if (applied >= 0.0f && applied != channelGain)
                {
                    const float step = (channelGain - applied) / static_cast<float>(glideFrames);
                    for (; n < glideFrames; ++n)
                    {
                        samples[n] *= applied + step * static_cast<float>(n + 1);
                        channelPeak = std::max(channelPeak, std::fabs(samples[n]));
                    }
                }
                applied = channelGain;
            }

            for (; n < frames; ++n)
            {
                samples[n] *= channelGain;
                channelPeak = std::max(channelPeak, std::fabs(samples[n]));
            }

            peak = std::max(peak, channelPeak);
            auto& side = _sidePeaks[std::min(c, 1)];
            float sideStored = side.load();
            while (channelPeak > sideStored && !side.compare_exchange_weak(sideStored, channelPeak))
            {
            }
        }

        float stored = _peak.load();
        while (peak > stored && !_peak.compare_exchange_weak(stored, peak))
        {
        }

        // Kept for the chains it drives, when there are any.
        if (_hasListeners.load(std::memory_order_relaxed))
        {
            const uint64_t block = CurrentBlock();
            const size_t slot = static_cast<size_t>(block % 2);
            const int count = std::min(numChannels, 2);
            for (int c = 0; c < count; ++c)
            {
                auto& tap = _tap[slot][c];
                if (tap.size() < static_cast<size_t>(frames))
                {
                    tap.resize(static_cast<size_t>(frames));
                }
                std::copy(channels[c], channels[c] + frames, tap.data());
            }
            _tapChannels[slot].store(count);
            _tapFrames[slot].store(frames);
            _tapBlock[slot].store(block, std::memory_order_release);
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

    void EffectChain::TakeStereoPeak(float& left, float& right)
    {
        left = _sidePeaks[0].exchange(0.0f);
        right = _sidePeaks[1].exchange(0.0f);
    }

    void EffectChain::NoteStereoPeak(float left, float right)
    {
        StorePeak(_sidePeaks[0], left);
        StorePeak(_sidePeaks[1], right);
    }
}
