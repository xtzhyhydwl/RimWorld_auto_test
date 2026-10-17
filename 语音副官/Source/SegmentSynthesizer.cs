using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Verse;

namespace VoiceDeputy
{
    // 预热作业（01 §6.12）：不经播放链，只落库。
    public sealed class WarmupJob
    {
        public readonly JobKind Kind;
        public readonly string Text;
        public readonly SegmentKind SegmentKind;

        public WarmupJob(string text, SegmentKind segmentKind)
        {
            Kind = JobKind.Warmup;
            Text = text;
            SegmentKind = segmentKind;
        }
    }

    // 一个作业的完成回执（01 §6.12）：Playback 非空 = 缺片段作业，空 = 预热作业。
    public sealed class CompletedJob
    {
        public readonly long JobId;
        public readonly JobKind Kind;
        public readonly Utterance Playback;
        public readonly string[] DeadTexts;
        public readonly int Put;

        public CompletedJob(long jobId, JobKind kind, Utterance playback, string[] deadTexts, int put)
        {
            JobId = jobId;
            Kind = kind;
            Playback = playback;
            DeadTexts = deadTexts == null || deadTexts.Length == 0 ? EmptyTexts : (string[])deadTexts.Clone();
            Put = put;
        }

        private static readonly string[] EmptyTexts = new string[0];
    }

    // 唯一跨线程层（I1）：主线程登记作业并拍 TtsRequestParams 快照，后台 Task 只回 SynthesisResult，
    // 主线程 Drain 里落库（SpeechLibrary.Put 只在主线程发生，I2）。
    public static class SegmentSynthesizer
    {
        private sealed class Job
        {
            public long Id;
            public JobKind Kind;
            public Utterance Playback;
            public List<Part> Parts;
            public int Arrived;
            public int Failed;
            public int Put;
            public List<string> DeadTexts;
        }

        private readonly struct Part
        {
            public readonly string Text;
            public readonly SegmentKind Kind;

            public Part(string text, SegmentKind kind)
            {
                Text = text;
                Kind = kind;
            }
        }

        private static readonly Dictionary<long, Job> Jobs = new Dictionary<long, Job>();
        private static readonly ConcurrentQueue<SynthesisResult> Completed = new ConcurrentQueue<SynthesisResult>();
        private static readonly CompletedJob[] EmptyJobs = new CompletedJob[0];
        private static long lastJobId;

        public static long RequestMissing(Utterance utterance, Segment[] missing)
        {
            if (utterance == null || missing == null || missing.Length == 0)
            {
                return 0;
            }

            List<Part> parts = new List<Part>(missing.Length);
            foreach (Segment segment in missing)
            {
                if (!string.IsNullOrEmpty(segment.Text))
                {
                    parts.Add(new Part(segment.Text, segment.Kind));
                }
            }

            return StartJob(JobKind.Missing, utterance, parts);
        }

        public static long RequestWarmup(WarmupJob warmup)
        {
            if (warmup == null || string.IsNullOrEmpty(warmup.Text))
            {
                return 0;
            }

            return StartJob(JobKind.Warmup, null, new List<Part> { new Part(warmup.Text, warmup.SegmentKind) });
        }

        // 每帧一次（DeputyGameComponent）。就绪判据写死 Arrived + Failed == Total。
        public static CompletedJob[] Drain()
        {
            List<CompletedJob> done = null;
            SynthesisResult result;
            while (Completed.TryDequeue(out result))
            {
                Job job;
                if (!Jobs.TryGetValue(result.JobId, out job))
                {
                    continue;
                }

                if (result.Succeeded)
                {
                    LibraryKey key = SpeechLibrary.Key(result.Text, result.Kind);
                    SpeechLibrary.Put(key, result.WavBytes);
                    if (SpeechLibrary.Contains(key))
                    {
                        job.Arrived++;
                        job.Put++;
                        Trace.Step("5.2", result.Text);
                    }
                    else
                    {
                        Fail(job, result.Text, "put-failed");
                    }
                }
                else
                {
                    Fail(job, result.Text, result.Error);
                }

                if (job.Arrived + job.Failed != job.Parts.Count)
                {
                    continue;
                }

                Jobs.Remove(job.Id);
                if (done == null)
                {
                    done = new List<CompletedJob>();
                }

                done.Add(new CompletedJob(job.Id, job.Kind, job.Playback, job.DeadTexts == null ? null : job.DeadTexts.ToArray(), job.Put));
            }

            return done == null ? EmptyJobs : done.ToArray();
        }

        private static void Fail(Job job, string text, string error)
        {
            job.Failed++;
            if (job.DeadTexts == null)
            {
                job.DeadTexts = new List<string>();
            }

            job.DeadTexts.Add(text);
            Trace.Step("5.3", text + " " + (error ?? string.Empty));
        }

        private static long StartJob(JobKind kind, Utterance playback, List<Part> parts)
        {
            if (parts.Count == 0)
            {
                return 0;
            }

            // Settings 只在主线程读一次，拍成不可变快照交给后台（I1）。
            TtsRequestParams parameters = Snapshot();

            Job job = new Job
            {
                Id = ++lastJobId,
                Kind = kind,
                Playback = playback,
                Parts = Dedupe(parts)
            };

            Jobs[job.Id] = job;

            foreach (Part part in job.Parts)
            {
                long jobId = job.Id;
                string text = part.Text;
                SegmentKind segmentKind = part.Kind;
                Trace.Step("5.1", text);
                Task.Run(() =>
                {
                    SynthesisResult result = TtsClient.Synthesize(new TtsRequest(jobId, text, segmentKind, parameters));
                    Completed.Enqueue(result);
                });
            }

            return job.Id;
        }

        // 同一句里同一个片段可能出现两次（例如两个人同名）：只合成一次。
        private static List<Part> Dedupe(List<Part> parts)
        {
            List<Part> unique = new List<Part>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                bool seen = false;
                for (int j = 0; j < unique.Count; j++)
                {
                    if (unique[j].Kind == parts[i].Kind && unique[j].Text == parts[i].Text)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    unique.Add(parts[i]);
                }
            }

            return unique;
        }

        private static TtsRequestParams Snapshot()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            if (settings == null)
            {
                return new TtsRequestParams(string.Empty, string.Empty, string.Empty, string.Empty, 1f);
            }

            return new TtsRequestParams(settings.BaseUrl, settings.ApiKey, settings.Model, settings.Voice, settings.Speed);
        }
    }
}
