using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LightTranslate
{
    // Fixed local messages only; never expose selected text or server response bodies in logs.
    internal sealed class IndexException : Exception
    {
        internal IndexException(string message) : base(message) { }
    }
    internal sealed class IndexResult
    {
        internal string Text, Model;
    }
    internal sealed class IndexTranslator : IDisposable
    {
        internal const string Endpoint = "https://index-translate.bilibili.com/v1/chat/completions";
        internal const string DefaultModel = "Index-Translate-35B-A3B";
        internal static readonly string[] Models = { "Index-Translate-2B", "Index-Translate-9B", DefaultModel };
        private readonly HttpClient http;
        private readonly TimeSpan timeout;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly object gate = new object();
        private DateTime retryAt;
        internal IndexTranslator(HttpMessageHandler handler = null, TimeSpan? timeout = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
            http.Timeout = Timeout.InfiniteTimeSpan;
            this.timeout = timeout ?? TimeSpan.FromSeconds(60);
            this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        }
        internal static bool ValidModel(string model) { return Array.IndexOf(Models, model) >= 0; }
        internal static string TargetName(string target)
        {
            switch ((target ?? "").Trim().ToLowerInvariant())
            {
                case "zh": case "zh-cn": case "zh-sg": case "zh-hans": return "简体中文";
                case "zh-tw": case "zh-hk": case "zh-mo": case "zh-hant": return "繁體中文";
                case "en": return "英语"; case "ja": return "日语"; case "ko": return "韩语";
                case "de": return "德语"; case "fr": return "法语"; case "es": return "西班牙语";
                case "pt": return "葡萄牙语"; case "ru": return "俄语"; case "ar": return "阿拉伯语";
                case "it": return "意大利语"; case "nl": return "荷兰语"; case "pl": return "波兰语";
                case "ro": return "罗马尼亚语"; case "sv": return "瑞典语"; case "tr": return "土耳其语";
                case "hi": return "印地语"; case "vi": return "越南语"; case "th": return "泰语";
                case "id": return "印尼语"; case "ms": return "马来语"; case "fil": return "菲律宾语";
                case "uk": case "ukr_cyrl": return "乌克兰语"; case "fa": case "fas_arab": return "波斯语";
                case "cs": case "ces_latn": return "捷克语"; case "el": case "ell_grek": return "希腊语";
                case "da": case "dan_latn": return "丹麦语"; case "hu": case "hun_latn": return "匈牙利语";
                case "fi": case "fin_latn": return "芬兰语"; case "nb": case "nob_latn": return "书面挪威语";
                case "sk": case "slk_latn": return "斯洛伐克语"; case "bg": case "bul_cyrl": return "保加利亚语";
                default: throw new IndexException("Index 不支持此目标语言代码，请检查设置。");
            }
        }
        internal static string Prompt(string text, string target)
        {
            string name = TargetName(target);
            string script = name == "简体中文" ? "必须使用简体汉字。" : name == "繁體中文" ? "必須使用繁體漢字。" : "";
            return "请将以下文本翻译为" + name + "，直接输出翻译结果，不要进行任何解释。" + script + "\n\n" + text.Trim();
        }
        internal async Task<string> Translate(string text, string target, string model, CancellationToken ct)
        { return (await Complete(text, target, model, ct)).Text; }

        internal async Task<IndexResult> Complete(string text, string target, string model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!ValidModel(model)) throw new IndexException("Index 模型配置无效，请在设置中重新选择 2B、9B 或 35B。");
            string body = new JavaScriptSerializer().Serialize(new {
                model = model, messages = new[] { new { role = "user", content = Prompt(text, target) } },
                temperature = 0, max_tokens = 4096, stream = false, chat_template_kwargs = new { enable_thinking = false }
            });
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeout);
                var token = deadline.Token;
                try
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        TimeSpan wait;
                        lock (gate) wait = retryAt - DateTime.UtcNow;
                        if (wait > TimeSpan.FromSeconds(10)) throw new IndexException("Index 请求过于频繁，请稍后重试（已保留所选模型）。");
                        if (wait > TimeSpan.Zero) await delay(wait, token);
                        token.ThrowIfCancellationRequested();
                        using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                        {
                            request.Headers.UserAgent.ParseAdd("Index-Translate-Client/1.0");
                            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                            using (var response = await http.SendAsync(request, token))
                            {
                                token.ThrowIfCancellationRequested();
                                int status = (int)response.StatusCode;
                                if (status == 429)
                                {
                                    var after = response.Headers.RetryAfter;
                                    TimeSpan backoff = after != null && after.Delta.HasValue ? after.Delta.Value :
                                        after != null && after.Date.HasValue ? after.Date.Value.UtcDateTime - DateTime.UtcNow : TimeSpan.FromSeconds(1 << attempt);
                                    if (backoff < TimeSpan.FromSeconds(1)) backoff = TimeSpan.FromSeconds(1);
                                    lock (gate) { var until = DateTime.UtcNow + backoff; if (until > retryAt) retryAt = until; }
                                    if (attempt == 2) throw new IndexException("Index 请求过于频繁，已停止重试，请稍后再试。");
                                    continue;
                                }
                                if (status == 400 || status == 404) throw new IndexException("Index 不接受此模型或语言，请检查设置；不会自动换模型。");
                                if (status == 401 || status == 403 || status == 412) throw new IndexException("Index 接口拒绝访问（HTTP " + status + "），请稍后重试。");
                                if (!response.IsSuccessStatusCode) throw new IndexException("Index 暂时不可用（HTTP " + status + "），请稍后重试。");
                                string raw = await response.Content.ReadAsStringAsync();
                                token.ThrowIfCancellationRequested();
                                return Parse(raw, model);
                            }
                        }
                    }
                    throw new IndexException("Index 请求过于频繁，请稍后重试。");
                }
                catch (OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new IndexException("Index 请求超时（60 秒），请稍后重试或手动选择其他模型。");
                }
                catch (HttpRequestException) { ct.ThrowIfCancellationRequested(); throw new IndexException("无法连接 Index，请检查网络后重试。"); }
            }
        }
        internal static IndexResult Parse(string raw, string expected)
        {
            try
            {
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(raw);
                var choices = (object[])root["choices"];
                var choice = (Dictionary<string, object>)choices[0];
                object reason;
                if (choice.TryGetValue("finish_reason", out reason) && (string)reason == "length")
                    throw new IndexException("Index 译文达到长度上限，请缩短选区后重试。");
                string content = (string)((Dictionary<string, object>)choice["message"])["content"];
                int endThink = content.IndexOf("</think>", StringComparison.Ordinal);
                if (endThink >= 0) content = content.Substring(endThink + 8);
                else if (content.TrimStart().StartsWith("<think>", StringComparison.Ordinal)) throw new InvalidOperationException();
                content = content.Trim();
                if (content.Length == 0) throw new InvalidOperationException();
                object actual; root.TryGetValue("model", out actual);
                string model = actual as string;
                if (!string.IsNullOrEmpty(model) && model != expected)
                    throw new IndexException("Index 返回模型与所选模型不一致，已丢弃结果；请稍后重试。");
                return new IndexResult { Text = content, Model = model };
            }
            catch (IndexException) { throw; }
            catch { throw new IndexException("Index 返回了无法解析或空白的译文，请稍后重试。"); }
        }
        public void Dispose() { http.Dispose(); }
    }
}
