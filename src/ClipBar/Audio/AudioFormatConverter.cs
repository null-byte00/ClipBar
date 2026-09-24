using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ClipBar.Audio;

internal sealed class AudioFormatConverter
{
    public const int TargetSampleRate = 48000;

    enum Sample { F32, F64, I16, I24, I32, U8 }

    static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    readonly Sample _sample;
    readonly int _channels;
    readonly float[] _left, _right;
    readonly bool _fastStereo, _fastMono;
    readonly int _srcRate;

    double _resamplePos;
    float _prevL, _prevR;
    bool _havePrev;

    public int BlockAlign { get; }
    public string SampleText { get; }

    public AudioFormatConverter(WaveFormat format)
    {
        _channels = format.Channels;
        BlockAlign = format.BlockAlign;
        _srcRate = format.SampleRate;
        if (_channels < 1 || _channels > 32 || BlockAlign < _channels * (format.BitsPerSample / 8))
            throw new NotSupportedException($"Unsupported audio format: {format}");

        var encoding = format.Encoding;
        int mask = 0;
        if (format is WaveFormatExtensible ext)
        {
            mask = ext.ChannelMask;
            encoding = ext.SubFormat == FloatSubFormat ? WaveFormatEncoding.IeeeFloat
                     : ext.SubFormat == PcmSubFormat ? WaveFormatEncoding.Pcm
                     : WaveFormatEncoding.Unknown;
        }

        _sample = (encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => Sample.F32,
            (WaveFormatEncoding.IeeeFloat, 64) => Sample.F64,
            (WaveFormatEncoding.Pcm, 16) => Sample.I16,
            (WaveFormatEncoding.Pcm, 24) => Sample.I24,
            (WaveFormatEncoding.Pcm, 32) => Sample.I32,
            (WaveFormatEncoding.Pcm, 8) => Sample.U8,
            _ => throw new NotSupportedException($"Unsupported audio format: {format}"),
        };
        SampleText = _sample switch
        {
            Sample.F32 => "float32", Sample.F64 => "float64", Sample.I16 => "int16",
            Sample.I24 => "int24", Sample.I32 => "int32", _ => "uint8",
        };

        (_left, _right) = BuildMatrix(_channels, mask);
        _fastStereo = _sample == Sample.F32 && _channels == 2 && BlockAlign == 8;
        _fastMono = _sample == Sample.F32 && _channels == 1 && BlockAlign == 4;
    }

    float[] _scratch = [];

    public int Convert(ReadOnlySpan<byte> src, Span<float> dst)
    {
        int srcFrames = src.Length / BlockAlign;
        if (srcFrames <= 0) return 0;

        if (_scratch.Length < srcFrames * 2) _scratch = new float[srcFrames * 2];
        var scratch = _scratch.AsSpan(0, srcFrames * 2);
        DecodeToStereo(src, srcFrames, scratch);

        if (_srcRate == TargetSampleRate)
        {
            int frames = Math.Min(srcFrames, dst.Length / 2);
            scratch[..(frames * 2)].CopyTo(dst);
            return frames;
        }
        return Resample(scratch, srcFrames, dst);
    }

    void DecodeToStereo(ReadOnlySpan<byte> src, int frames, Span<float> dst)
    {
        if (_fastStereo)
        {
            MemoryMarshal.Cast<byte, float>(src[..(frames * 8)]).CopyTo(dst);
            return;
        }
        if (_fastMono)
        {
            var s = MemoryMarshal.Cast<byte, float>(src[..(frames * 4)]);
            for (int i = 0; i < frames; i++)
                dst[2 * i] = dst[2 * i + 1] = s[i];
            return;
        }

        Span<float> frame = stackalloc float[_channels];
        for (int f = 0; f < frames; f++)
        {
            Decode(src.Slice(f * BlockAlign, BlockAlign), frame);
            float l = 0, r = 0;
            for (int c = 0; c < _channels; c++)
            {
                l += frame[c] * _left[c];
                r += frame[c] * _right[c];
            }
            dst[2 * f] = l;
            dst[2 * f + 1] = r;
        }
    }

    int Resample(ReadOnlySpan<float> src, int srcFrames, Span<float> dst)
    {
        double step = (double)_srcRate / TargetSampleRate;
        int outCount = 0;
        int maxOut = dst.Length / 2;

        while (outCount < maxOut)
        {
            double pos = _resamplePos;
            int i0 = (int)Math.Floor(pos);
            if (i0 >= srcFrames - 1) break;
            float l0, r0, l1, r1;
            if (i0 < 0)
            {
                if (!_havePrev) { l0 = src[0]; r0 = src[1]; } else { l0 = _prevL; r0 = _prevR; }
                l1 = src[0]; r1 = src[1];
            }
            else
            {
                l0 = src[i0 * 2]; r0 = src[i0 * 2 + 1];
                l1 = src[(i0 + 1) * 2]; r1 = src[(i0 + 1) * 2 + 1];
            }
            float t = (float)(pos - i0);
            dst[outCount * 2] = l0 + (l1 - l0) * t;
            dst[outCount * 2 + 1] = r0 + (r1 - r0) * t;
            outCount++;
            _resamplePos += step;
        }

        if (srcFrames > 0)
        {
            _prevL = src[(srcFrames - 1) * 2];
            _prevR = src[(srcFrames - 1) * 2 + 1];
            _havePrev = true;
        }
        _resamplePos -= srcFrames;
        return outCount;
    }

    void Decode(ReadOnlySpan<byte> frame, Span<float> outp)
    {
        switch (_sample)
        {
            case Sample.F32:
                MemoryMarshal.Cast<byte, float>(frame)[.._channels].CopyTo(outp);
                break;
            case Sample.F64:
            {
                var d = MemoryMarshal.Cast<byte, double>(frame);
                for (int c = 0; c < _channels; c++) outp[c] = (float)d[c];
                break;
            }
            case Sample.I16:
            {
                var s = MemoryMarshal.Cast<byte, short>(frame);
                for (int c = 0; c < _channels; c++) outp[c] = s[c] * (1f / 32768f);
                break;
            }
            case Sample.I32:
            {
                var s = MemoryMarshal.Cast<byte, int>(frame);
                for (int c = 0; c < _channels; c++) outp[c] = s[c] * (1f / 2147483648f);
                break;
            }
            case Sample.I24:
                for (int c = 0; c < _channels; c++)
                {
                    int o = c * 3;
                    int v = (frame[o] << 8) | (frame[o + 1] << 16) | (frame[o + 2] << 24);
                    outp[c] = (v >> 8) * (1f / 8388608f);
                }
                break;
            default:
                for (int c = 0; c < _channels; c++) outp[c] = (frame[c] - 128) * (1f / 128f);
                break;
        }
    }

    static (float[] Left, float[] Right) BuildMatrix(int channels, int mask)
    {
        var l = new float[channels];
        var r = new float[channels];
        if (channels == 1) { l[0] = r[0] = 1f; return (l, r); }
        if (channels == 2) { l[0] = 1f; r[1] = 1f; return (l, r); }

        if (mask == 0)
            mask = channels switch
            {
                3 => 0x007,
                4 => 0x033,
                5 => 0x037,
                6 => 0x03F,
                7 => 0x13F,
                8 => 0x63F,
                _ => 0,
            };

        if (mask == 0)
        {
            l[0] = 1f; r[1] = 1f;
            return (l, r);
        }

        int idx = 0;
        for (int bit = 0; bit < 32 && idx < channels; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            (l[idx], r[idx]) = SpeakerCoefficients(bit);
            idx++;
        }

        float sumL = 0, sumR = 0;
        foreach (var v in l) sumL += v;
        foreach (var v in r) sumR += v;
        float scale = 1f / Math.Max(1f, Math.Max(sumL, sumR));
        for (int i = 0; i < channels; i++) { l[i] *= scale; r[i] *= scale; }
        return (l, r);
    }

    static (float L, float R) SpeakerCoefficients(int bit) => bit switch
    {
        0 => (1f, 0f),
        1 => (0f, 1f),
        2 => (0.707f, 0.707f),
        3 => (0f, 0f),
        4 => (0.707f, 0f),
        5 => (0f, 0.707f),
        6 => (0.707f, 0f),
        7 => (0f, 0.707f),
        8 => (0.5f, 0.5f),
        9 => (0.707f, 0f),
        10 => (0f, 0.707f),
        11 => (0.5f, 0.5f),
        12 => (0.707f, 0f),
        13 => (0.5f, 0.5f),
        14 => (0f, 0.707f),
        15 => (0.707f, 0f),
        16 => (0.5f, 0.5f),
        17 => (0f, 0.707f),
        _ => (0f, 0f),
    };
}
