using System;
using UnityEngine;
using Verse;

namespace VoiceDeputy
{
    // 主线程泵（01 §5.1 / §8.1）：每帧固定顺序 Drain ⇒ Plan ⇒ 分派 ⇒ VoicePlayer.Tick。
    // 不注册：框架的 Game.FillComponents 用 ctor(Game) 自动收养（I7），本类只提供构造与顺序。
    public sealed class DeputyGameComponent : GameComponent
    {
        private const string SaveKey = "voiceCacheId";
        private const int ErrorTraceFrameGap = 600;

        private static readonly string[] NoDeadTexts = new string[0];

        private string saveFingerprint;
        private int lastErrorFrame = -ErrorTraceFrameGap;
        private string lastErrorStage;

        public DeputyGameComponent(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref saveFingerprint, SaveKey);
            EnsureSaveFingerprint();
        }

        public override void LoadedGame()
        {
            EnsureSaveFingerprint();
        }

        public override void StartedNewGame()
        {
            EnsureSaveFingerprint();
        }

        public override void GameComponentUpdate()
        {
            try
            {
                DrainCompleted();
            }
            catch (Exception exception)
            {
                NoteError("drain", exception);
            }

            try
            {
                PlanNewUtterances();
            }
            catch (Exception exception)
            {
                NoteError("plan", exception);
            }

            try
            {
                VoicePlayer.Tick();
            }
            catch (Exception exception)
            {
                NoteError("player", exception);
            }
        }

        // 第 1 步：拿本帧合成结果（落库已在 Drain 里做完），回路闭合一次。
        private void DrainCompleted()
        {
            CompletedJob[] jobs = SegmentSynthesizer.Drain();
            for (int i = 0; i < jobs.Length; i++)
            {
                if (jobs[i].Playback == null)
                {
                    // 预热作业：只落库，不组装、不出声。
                    continue;
                }

                Dispatch(jobs[i].Playback, ClipAssembler.Plan(jobs[i].Playback, jobs[i].DeadTexts), true);
            }
        }

        // 第 3 步：新句（含聚合批出句、试听/朗读）。
        private void PlanNewUtterances()
        {
            Utterance[] utterances = Announcer.Tick();
            for (int i = 0; i < utterances.Length; i++)
            {
                Dispatch(utterances[i], ClipAssembler.Plan(utterances[i], NoDeadTexts), false);
            }
        }

        // 第 4 步：唯一分派点（01 §6.15）。
        private void Dispatch(Utterance utterance, AssemblyResult result, bool afterSynthesis)
        {
            switch (result.Outcome)
            {
                case AssemblyOutcome.Ready:
                    VoicePlayer.Play(result.Playable);
                    return;

                case AssemblyOutcome.Missing:
                    // 每句最多一轮合成：合成回来还缺 ⇒ 丢弃该句（01 §5.4 失败模式）。
                    if (afterSynthesis)
                    {
                        Trace.Step("5.3", "loop-guard");
                        return;
                    }

                    SegmentSynthesizer.RequestMissing(utterance, result.MissingSegments);
                    return;

                default:
                    // Nothing：无可用 clip，丢弃该句（ClipAssembler 已记 3a nothing）。
                    return;
            }
        }

        private void EnsureSaveFingerprint()
        {
            if (string.IsNullOrEmpty(saveFingerprint))
            {
                saveFingerprint = Guid.NewGuid().ToString("N");
            }

            // 读档时 ExposeData 晚于 StartedNewGame；两处都判空补铸，保证键的作用域尽早确定。
            SpeechLibrary.SetSaveFingerprint(saveFingerprint);
        }

        // 同一段每 600 帧最多记一条：泵里抛异常不能变成日志洪水。
        private void NoteError(string stage, Exception exception)
        {
            int frame = Time.frameCount;
            if (stage == lastErrorStage && frame - lastErrorFrame < ErrorTraceFrameGap)
            {
                return;
            }

            lastErrorStage = stage;
            lastErrorFrame = frame;
            Trace.Step("err", stage + " " + exception.GetType().Name);
        }
    }
}
