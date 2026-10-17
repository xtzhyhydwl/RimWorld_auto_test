using System;

namespace VoiceDeputy
{
    // 解码后的 PCM（01 §6.13）：Samples 是交错存储（多声道按 L,R,L,R…），音量归一由 LoudnessNormalizer 做。
    public sealed class PcmData
    {
        public readonly float[] Samples;
        public readonly int SampleRate;
        public readonly int Channels;

        public PcmData(float[] samples, int sampleRate, int channels)
        {
            Samples = samples;
            SampleRate = sampleRate;
            Channels = channels;
        }

        public int Frames
        {
            get { return Channels <= 0 ? 0 : Samples.Length / Channels; }
        }
    }

    // WAV 字节 → PcmData（01 §5.4）。只认 RIFF/WAVE 的 PCM 与 IEEE float。
    // 硬约束 8：SiliconFlow 的 `data` 长度字段是占位值（4294967040），所以长度一律按
    // "data 起始 + 剩余字节" 重算，完全不看 header 那一格。
    public static class WavReader
    {
        private const int WavHeaderTagLength = 4;
        private const int MaxChannels = 8;
        private const int MaxSampleRate = 192000;

        public static PcmData Read(byte[] wav)
        {
            try
            {
                return ReadCore(wav);
            }
            catch
            {
                // 解不开就是"这个片段坏了"，由调用方走 Missing（01 §5.4 失败模式）。
                return null;
            }
        }

        private static PcmData ReadCore(byte[] wav)
        {
            if (wav == null || wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE"))
            {
                return null;
            }

            int format = 0;
            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            bool formatFound = false;
            int dataStart = -1;
            int dataBytes = 0;

            int position = 12;
            while (position + 8 <= wav.Length)
            {
                string chunkId = Ascii(wav, position, WavHeaderTagLength);
                long declaredSize = UInt32(wav, position + 4);
                int body = position + 8;

                if (chunkId == "fmt ")
                {
                    if (body + 16 > wav.Length)
                    {
                        return null;
                    }

                    format = UInt16(wav, body);
                    channels = UInt16(wav, body + 2);
                    sampleRate = (int)UInt32(wav, body + 4);
                    bitsPerSample = UInt16(wav, body + 14);

                    // WAVE_FORMAT_EXTENSIBLE：真实格式在 SubFormat GUID 的头两个字节。
                    if (format == 0xFFFE && body + 26 <= wav.Length)
                    {
                        format = UInt16(wav, body + 24);
                    }

                    formatFound = true;
                }
                else if (chunkId == "data")
                {
                    dataStart = body;
                    dataBytes = wav.Length - body;
                    break;
                }

                long next = body + declaredSize + (declaredSize % 2);
                if (declaredSize <= 0 || next <= position || next > wav.Length)
                {
                    // 长度是占位值的块（或截断文件）：没法再跳过，扫描到此为止。
                    break;
                }

                position = (int)next;
            }

            if (!formatFound || dataStart < 0 || dataBytes <= 0)
            {
                return null;
            }

            if (channels <= 0 || channels > MaxChannels || sampleRate <= 0 || sampleRate > MaxSampleRate)
            {
                return null;
            }

            bool isFloat;
            int bytesPerSample = BytesPerSample(format, bitsPerSample, out isFloat);
            if (bytesPerSample <= 0)
            {
                return null;
            }

            int frameBytes = bytesPerSample * channels;
            int frames = dataBytes / frameBytes;
            if (frames <= 0)
            {
                return null;
            }

            // 尾部不足一帧的字节直接丢掉（截断文件常见）。
            int total = frames * channels;
            float[] samples = new float[total];
            int offset = dataStart;
            for (int i = 0; i < total; i++)
            {
                samples[i] = ReadSample(wav, offset, bytesPerSample, isFloat);
                offset += bytesPerSample;
            }

            return new PcmData(samples, sampleRate, channels);
        }

        // 返回每个采样的字节数；0 = 不支持的格式。
        private static int BytesPerSample(int format, int bitsPerSample, out bool isFloat)
        {
            isFloat = false;

            if (format == 1)
            {
                if (bitsPerSample == 8 || bitsPerSample == 16 || bitsPerSample == 24 || bitsPerSample == 32)
                {
                    return bitsPerSample / 8;
                }

                return 0;
            }

            if (format == 3 && bitsPerSample == 32)
            {
                isFloat = true;
                return 4;
            }

            return 0;
        }

        private static float ReadSample(byte[] data, int offset, int bytesPerSample, bool isFloat)
        {
            switch (bytesPerSample)
            {
                case 1:
                    // 8 bit WAV 是无符号。
                    return (data[offset] - 128) / 128f;
                case 2:
                    return (short)(data[offset] | (data[offset + 1] << 8)) / 32768f;
                case 3:
                    return (data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16)) / 8388608f;
                default:
                    if (isFloat)
                    {
                        return BitConverter.ToSingle(data, offset);
                    }

                    uint value = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16)) | ((uint)data[offset + 3] << 24);
                    return (int)value / 2147483648f;
            }
        }

        private static bool Tag(byte[] data, int offset, string tag)
        {
            if (offset + tag.Length > data.Length)
            {
                return false;
            }

            for (int i = 0; i < tag.Length; i++)
            {
                if (data[offset + i] != tag[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Ascii(byte[] data, int offset, int count)
        {
            char[] chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                chars[i] = offset + i < data.Length ? (char)data[offset + i] : '\0';
            }

            return new string(chars);
        }

        private static int UInt16(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8);
        }

        private static long UInt32(byte[] data, int offset)
        {
            long value = data[offset];
            value |= (long)data[offset + 1] << 8;
            value |= (long)data[offset + 2] << 16;
            value |= (long)data[offset + 3] << 24;
            return value;
        }
    }
}
