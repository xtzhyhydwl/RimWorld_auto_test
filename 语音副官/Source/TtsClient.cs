using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using Verse;

namespace VoiceDeputy
{
    // 主线程从 Settings 拍下的不可变快照（01 §6.11）；后台线程只认这一份，不读 Settings。
    public sealed class TtsRequestParams
    {
        public const string WavFormat = "wav";

        public readonly string BaseUrl;
        public readonly string ApiKey;
        public readonly string Model;
        public readonly string Voice;
        public readonly string ResponseFormat;
        public readonly float Speed;

        public TtsRequestParams(string baseUrl, string apiKey, string model, string voice, float speed)
        {
            BaseUrl = baseUrl ?? string.Empty;
            ApiKey = apiKey ?? string.Empty;
            Model = model ?? string.Empty;
            Voice = voice ?? string.Empty;
            ResponseFormat = WavFormat;
            Speed = speed;
        }

        public bool IsConfigured
        {
            get { return BaseUrl.Length > 0 && ApiKey.Length > 0; }
        }
    }

    // 一条合成请求。JobId 与 Kind 让后台回来的结果能与作业表对上（结果按片段原文与片段种类落库）。
    public sealed class TtsRequest
    {
        public readonly long JobId;
        public readonly string Text;
        public readonly SegmentKind Kind;
        public readonly TtsRequestParams Params;

        public TtsRequest(long jobId, string text, SegmentKind kind, TtsRequestParams requestParams)
        {
            JobId = jobId;
            Text = text;
            Kind = kind;
            Params = requestParams;
        }
    }

    // 单条合成结果（01 §6.12）：WavBytes 与 Error 互斥。
    public sealed class SynthesisResult
    {
        public readonly long JobId;
        public readonly string Text;
        public readonly SegmentKind Kind;
        public readonly byte[] WavBytes;
        public readonly string Error;

        public SynthesisResult(long jobId, string text, SegmentKind kind, byte[] wavBytes, string error)
        {
            JobId = jobId;
            Text = text;
            Kind = kind;
            WavBytes = wavBytes;
            Error = error;
        }

        public bool Succeeded
        {
            get { return WavBytes != null && WavBytes.Length > 0; }
        }
    }

    // 失败快照（01 §5.4）：UI 只读它（I4），不做自己的"能不能用"判断。
    // 类型放在命名空间层是因为 TtsClient 里还要有一个同名静态属性 LastError（嵌套类型与成员不能同名）。
    public sealed class LastError
    {
        public readonly int StatusCode;
        public readonly string BodyHead200;
        public readonly string Message;
        public readonly int AtTick;

        public LastError(int statusCode, string bodyHead200, string message)
        {
            StatusCode = statusCode;
            BodyHead200 = bodyHead200 ?? string.Empty;
            Message = message ?? string.Empty;
            AtTick = GenTicks.TicksGame;
        }

        public string Describe()
        {
            if (Message.Length > 0)
            {
                return Message;
            }

            return StatusCode == 0 ? string.Empty : "HTTP " + StatusCode.ToString(CultureInfo.InvariantCulture) + ": " + BodyHead200;
        }
    }

    // HTTP 协议只在这一处；配置闸也只在这一处（01 §5.4 的 I4）。
    // 本类在后台线程跑：只碰 string / TtsRequestParams / byte[]，不碰 UnityEngine 与 Settings。
    public static class TtsClient
    {
        public const string NotConfigured = "未配置";

        private const int BodyHeadLength = 200;

        // 成功与未配置都要能被 UI 看见，所以是属性而不是字段（读的人不关心它是怎么来的）。
        public static LastError LastError { get; private set; }

        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static SynthesisResult Synthesize(TtsRequest request)
        {
            if (request == null)
            {
                return new SynthesisResult(0, string.Empty, SegmentKind.RawText, null, "null-request");
            }

            TtsRequestParams parameters = request.Params;
            if (parameters == null || !parameters.IsConfigured)
            {
                LastError = new LastError(0, string.Empty, NotConfigured);
                return Failed(request, NotConfigured);
            }

            try
            {
                using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, parameters.BaseUrl))
                {
                    message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + parameters.ApiKey);
                    message.Content = new StringContent(Payload(parameters, request.Text), Encoding.UTF8, "application/json");

                    using (HttpResponseMessage response = Client.SendAsync(message).GetAwaiter().GetResult())
                    {
                        byte[] body = response.Content == null
                            ? new byte[0]
                            : response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                        if (!response.IsSuccessStatusCode)
                        {
                            string head = Head200(body);
                            LastError = new LastError((int)response.StatusCode, head, string.Empty);
                            return Failed(request, "HTTP " + (int)response.StatusCode);
                        }

                        if (body == null || body.Length == 0)
                        {
                            LastError = new LastError((int)response.StatusCode, string.Empty, "empty-body");
                            return Failed(request, "empty-body");
                        }

                        // 成功后清掉上一次失败，状态条才会回到「已配置」。
                        LastError = null;
                        return new SynthesisResult(request.JobId, request.Text, request.Kind, body, null);
                    }
                }
            }
            catch (Exception exception)
            {
                // HTTP 失败一律不向调用方抛（OOM 交给上层 Task 的 finally），只留一条快照。
                string reason = exception.GetType().Name + ": " + exception.Message;
                LastError = new LastError(0, string.Empty, reason);
                return Failed(request, reason);
            }
        }

        private static SynthesisResult Failed(TtsRequest request, string error)
        {
            return new SynthesisResult(request.JobId, request.Text, request.Kind, null, error);
        }

        // OpenAI 兼容的 /v1/audio/speech 请求体（文档没写死格式，这条假设记在 STATUS 待办）。
        private static string Payload(TtsRequestParams parameters, string text)
        {
            StringBuilder builder = new StringBuilder(160);
            builder.Append('{');
            AppendField(builder, "model", parameters.Model, false);
            AppendField(builder, "input", text, true);
            AppendField(builder, "voice", parameters.Voice, true);
            AppendField(builder, "response_format", parameters.ResponseFormat, true);
            builder.Append(",\"speed\":");
            builder.Append(parameters.Speed.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendField(StringBuilder builder, string name, string value, bool comma)
        {
            if (comma)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(name).Append("\":\"");
            AppendEscaped(builder, value);
            builder.Append('"');
        }

        private static void AppendEscaped(StringBuilder builder, string value)
        {
            if (value == null)
            {
                return;
            }

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }
        }

        private static string Head200(byte[] body)
        {
            if (body == null || body.Length == 0)
            {
                return string.Empty;
            }

            string text = Encoding.UTF8.GetString(body);
            return text.Length <= BodyHeadLength ? text : text.Substring(0, BodyHeadLength);
        }
    }
}
