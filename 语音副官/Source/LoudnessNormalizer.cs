using System;

namespace VoiceDeputy
{
    // 峰值归一到 −3 dBFS（01 §5.4）。纯函数、不缓存、可原地改采样。
    public static class LoudnessNormalizer
    {
        public const float TargetPeakDbfs = -3f;

        public static float[] Normalize(float[] samples)
        {
            float peak;
            return Normalize(samples, out peak);
        }

        public static float[] Normalize(float[] samples, out float peak)
        {
            peak = 0f;
            if (samples == null || samples.Length == 0)
            {
                return samples;
            }

            float max = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float magnitude = Math.Abs(samples[i]);
                if (magnitude > max)
                {
                    max = magnitude;
                }
            }

            peak = max;
            if (max <= 0f)
            {
                // 全零（静音片段）：原样返回，不除零、不抛。
                return samples;
            }

            float gain = TargetPeak() / max;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = samples[i] * gain;
            }

            return samples;
        }

        private static float TargetPeak()
        {
            return (float)Math.Pow(10.0, TargetPeakDbfs / 20.0);
        }
    }
}
