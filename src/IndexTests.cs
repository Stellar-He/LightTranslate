using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LightTranslate
{
    internal static class IndexTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send;
            internal int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            { Calls++; return Send(request, ct); }
        }
        private static void Check(bool ok, string message) { if (!ok) throw new Exception("Index: " + message); }
        private static HttpResponseMessage Response(string model, string text = "fixture")
        { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(new { model = model, choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } })) }; }
        private static string RequestModel(HttpRequestMessage request)
        { return (string)((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(request.Content.ReadAsStringAsync().Result))["model"]; }
        private static void Fails(Action action, string expected)
        {
            try { action(); throw new Exception("Expected failure: " + expected); }
            catch (IndexException ex) { Check(ex.Message.Contains(expected), "error message " + expected); }
        }
        internal static int RunStandalone()
        {
            var results = new Dictionary<string, object>(); Run(results);
            File.WriteAllText(Path.Combine(Program.Folder, "index-test.json"), new JavaScriptSerializer().Serialize(results));
            return 0;
        }
        internal static void Run(Dictionary<string, object> results)
        {
            Task.Run((Action)HttpTests).GetAwaiter().GetResult();
            SettingsTests();
            results["indexHttp"] = "all 3 model payloads, no Authorization, endpoint/UA, script prompts, response validation, model mismatch, truncation, errors, 429 bounded backoff/Retry-After, timeout, cancel before send/during HTTP/during backoff, stale response and cache isolation passed; fake HTTP only";
            results["indexSettings"] = "legacy migration, defaults retained, all 3 model save/reopen, clone/copy, cancel/reset, invalid model/language, inactive model preservation and failed-save rollback passed";
        }
        private static void HttpTests()
        {
            var none = CancellationToken.None;
            var handler = new Handler();
            handler.Send = (request, ct) => {
                Check(request.RequestUri.AbsoluteUri == IndexTranslator.Endpoint && request.Method == HttpMethod.Post, "fixed endpoint");
                Check(request.Headers.Authorization == null && request.Headers.UserAgent.ToString() == "Index-Translate-Client/1.0", "headers");
                var body = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(request.Content.ReadAsStringAsync().Result);
                Check(Convert.ToInt32(body["temperature"]) == 0 && (bool)body["stream"] == false && (bool)((Dictionary<string, object>)body["chat_template_kwargs"])["enable_thinking"] == false, "generation parameters");
                var messages = (object[])body["messages"];
                Check(messages.Length == 1 && (string)((Dictionary<string, object>)messages[0])["role"] == "user", "user prompt");
                Check(((string)((Dictionary<string, object>)messages[0])["content"]).Contains("sample"), "source in prompt");
                return Task.FromResult(Response(RequestModel(request)));
            };
            using (var translator = new Translator(null, null, null, handler))
            {
                foreach (string model in IndexTranslator.Models)
                {
                    Check(translator.Translate("sample", "zh-Hans", none, "index", model).GetAwaiter().GetResult() == "fixture", "model routing");
                    translator.Translate("sample", "zh-Hans", none, "index", model).GetAwaiter().GetResult();
                }
                Check(handler.Calls == 3, "model-separated cache and repeated hit");
                translator.Translate("sample", "zh-Hant", none, "index", IndexTranslator.DefaultModel).GetAwaiter().GetResult();
                translator.Translate("sample 2", "zh-Hans", none, "index", IndexTranslator.DefaultModel).GetAwaiter().GetResult();
                string cached;
                Check(handler.Calls == 5 && !translator.TryCached("sample", "zh-Hans", out cached, "microsoft") && !translator.TryCached("sample", "ja", out cached, "index"), "provider/language/source isolation");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool stopped = false;
                    try { translator.Translate("sample", "zh-Hans", canceled.Token, "index").GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && handler.Calls == 5, "cancel before cache");
                }
                Fails(() => translator.Translate("sample", "xx", none, "index").GetAwaiter().GetResult(), "目标语言");
                Fails(() => translator.Translate("sample", "en", none, "index", "unknown").GetAwaiter().GetResult(), "模型配置无效");
                Check(handler.Calls == 5, "invalid settings never send");
            }
            Check(IndexTranslator.Prompt("sample", "zh-Hans").Contains("必须使用简体汉字") && IndexTranslator.Prompt("sample", "zh-Hant").Contains("必須使用繁體漢字"), "explicit script constraint");
            Check(IndexTranslator.TargetName("ZH-CN") == "简体中文" && IndexTranslator.TargetName("zh-TW") == "繁體中文", "Chinese aliases");
            Check(IndexTranslator.Parse("{\"choices\":[{\"message\":{\"content\":\"<think>private thought</think>译文\"}}]}", IndexTranslator.DefaultModel).Text == "译文", "thought stripped");
            foreach (string bad in new[] { "bad json", "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":\" \"}}]}", "{\"choices\":[{\"message\":{\"content\":null}}]}" })
                Fails(() => IndexTranslator.Parse(bad, IndexTranslator.DefaultModel), "无法解析");
            Fails(() => IndexTranslator.Parse("{\"model\":\"other\",\"choices\":[{\"message\":{\"content\":\"wrong model\"}}]}", IndexTranslator.DefaultModel), "模型不一致");
            Fails(() => IndexTranslator.Parse("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}", IndexTranslator.DefaultModel), "长度上限");
            foreach (int status in new[] { 400, 401, 403, 404, 412, 500, 503 })
            {
                var failing = new Handler { Send = (r, t) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)) };
                using (var index = new IndexTranslator(failing)) Fails(() => index.Translate("sample", "en", IndexTranslator.DefaultModel, none).GetAwaiter().GetResult(), "Index");
                Check(failing.Calls == 1, "errors never fall back or retry other model");
            }
            var waits = new List<TimeSpan>();
            var limited = new Handler();
            limited.Send = (r, t) => {
                if (limited.Calls > 2) return Task.FromResult(Response(RequestModel(r)));
                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)); return Task.FromResult(response);
            };
            using (var index = new IndexTranslator(limited, null, (wait, token) => { waits.Add(wait); return Task.FromResult(0); }))
                Check(index.Translate("sample", "en", IndexTranslator.DefaultModel, none).Result == "fixture", "429 retry success");
            Check(limited.Calls == 3 && waits.Count == 2 && waits[0].TotalSeconds > 1, "Retry-After respected");
            var exhausted = new Handler { Send = (r, t) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)) };
            using (var index = new IndexTranslator(exhausted, null, (w, t) => Task.FromResult(0)))
                Fails(() => index.Translate("sample", "en", IndexTranslator.DefaultModel, none).GetAwaiter().GetResult(), "停止重试");
            Check(exhausted.Calls == 3, "retry budget");
            var longWait = new Handler { Send = (r, t) => { var response = new HttpResponseMessage((HttpStatusCode)429); response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2)); return Task.FromResult(response); } };
            using (var index = new IndexTranslator(longWait))
            {
                Fails(() => index.Translate("sample", "en", IndexTranslator.DefaultModel, none).GetAwaiter().GetResult(), "过于频繁");
                Fails(() => index.Translate("sample2", "en", IndexTranslator.DefaultModel, none).GetAwaiter().GetResult(), "过于频繁");
                Check(longWait.Calls == 1, "long server cooldown retained across requests");
            }
            var blocking = new Handler { Send = (r, t) => { var promise = new TaskCompletionSource<HttpResponseMessage>(); t.Register(() => promise.TrySetCanceled()); return promise.Task; } };
            using (var index = new IndexTranslator(blocking, TimeSpan.FromMilliseconds(20)))
                Fails(() => index.Translate("sample", "en", IndexTranslator.DefaultModel, none).GetAwaiter().GetResult(), "超时");
            using (var index = new IndexTranslator(blocking))
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = index.Translate("sample", "en", IndexTranslator.DefaultModel, cancellation.Token);
                cancellation.Cancel(); bool stopped = false;
                try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "in-flight cancellation");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var index = new IndexTranslator(exhausted, null, (w, t) => { cancellation.Cancel(); t.ThrowIfCancellationRequested(); return Task.FromResult(0); }))
            {
                bool stopped = false;
                try { index.Translate("sample", "en", IndexTranslator.DefaultModel, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "cancellation during backoff");
            }
            var completion = new TaskCompletionSource<HttpResponseMessage>();
            var stale = new Handler { Send = (r, t) => completion.Task };
            using (var translator = new Translator(null, null, null, stale))
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = translator.Translate("sample", "en", cancellation.Token, "index");
                cancellation.Cancel(); completion.SetResult(Response(IndexTranslator.DefaultModel));
                bool stopped = false; try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                string cached; Check(stopped && !translator.TryCached("sample", "en", out cached, "index"), "late canceled result discarded before cache");
            }
        }
        private static void SettingsTests()
        {
            string root = Path.Combine(Program.Folder, "index-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "settings.json");
                File.WriteAllText(path, "{\"TranslationProvider\":\"microsoft\",\"Target\":\"zh-Hant\",\"BubbleFontSize\":22,\"MaxSelectionLength\":777,\"Diagnostics\":true}");
                var migrated = Settings.Load(path);
                Check(migrated.TranslationProvider == "microsoft" && migrated.IndexModel == IndexTranslator.DefaultModel && migrated.BubbleFontSize == 22 && migrated.MaxSelectionLength == 777 && migrated.Diagnostics, "old settings preserved");
                var window = new SettingsWindow(migrated, null, new FakeStartupRegistration());
                Check(window.ProviderForTest == 1 && !window.IndexModelEnabledForTest, "existing provider unchanged");
                window.SetProviderForTest(4); Check(window.IndexModelEnabledForTest && window.TargetEnabledForTest && migrated.TranslationProvider == "microsoft", "local pending choice");
                window.Close(); Check(Settings.Load(path).TranslationProvider == "microsoft", "cancel preserves disk");
                foreach (string model in IndexTranslator.Models)
                {
                    window = new SettingsWindow(migrated, null, new FakeStartupRegistration()); window.SetProviderForTest(4); window.SetIndexModelForTest(model);
                    Check(window.Apply(path) && migrated.IndexModel == model && migrated.TranslationProvider == "index", "save model and copy to runtime");
                    var saved = Settings.Load(path); Check(saved.IndexModel == model && saved.Clone().IndexModel == model && saved.Target == "zh-Hant", "roundtrip and clone");
                    var reopened = new SettingsWindow(saved, null, new FakeStartupRegistration()); Check(reopened.ProviderForTest == 4 && reopened.IndexModelForTest == model, "reopen model");
                    reopened.SetIndexModelForTest(IndexTranslator.Models[0]); reopened.Close(); Check(Settings.Load(path).IndexModel == model, "cancel pending model");
                }
                window = new SettingsWindow(migrated, null, new FakeStartupRegistration());
                window.SetProviderForTest(0); Check(window.Apply(path) && migrated.IndexModel == IndexTranslator.DefaultModel && migrated.TranslationProvider == "auto", "switch away retains model and original auto");
                window = new SettingsWindow(migrated, null, new FakeStartupRegistration()); window.Fill(new Settings());
                Check(window.ProviderForTest == 0 && window.IndexModelForTest == IndexTranslator.DefaultModel, "reset defaults"); window.Close();
                var invalid = migrated.Clone(); invalid.TranslationProvider = "index"; invalid.IndexModel = "unknown"; invalid.NormalizeReading();
                Check(invalid.IndexModel == "unknown", "invalid model not silently replaced");
                window = new SettingsWindow(invalid, null, new FakeStartupRegistration()); Check(!window.Apply(path), "invalid model shown and rejected");
                window.SetIndexModelForTest(IndexTranslator.DefaultModel); window.SetBehaviorForTest(true, true, "xx", "777", ""); Check(!window.Apply(path), "unsupported language rejected"); window.Close();
                window = new SettingsWindow(migrated, null, new FakeStartupRegistration()); window.SetProviderForTest(4); window.SetIndexModelForTest(IndexTranslator.Models[0]);
                Check(!window.Apply(Path.Combine(root, "missing", "settings.json")) && migrated.TranslationProvider == "auto" && migrated.IndexModel == IndexTranslator.DefaultModel, "failed save keeps runtime state"); window.Close();
                window = new SettingsWindow(new Settings { TranslationProvider = "index", IndexModel = IndexTranslator.Models[1] }, null, new FakeStartupRegistration());
                window.RenderPreview(Path.Combine(Program.Folder, "settings-index-preview.png"), 0); window.Close();
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class LiveObserver : DelegatingHandler
        {
            internal int Calls, Status;
            internal string ActualModel;
            internal LiveObserver() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++; var response = await base.SendAsync(request, ct); Status = (int)response.StatusCode;
                try { object value; var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(await response.Content.ReadAsStringAsync()); if (root.TryGetValue("model", out value)) ActualModel = value as string; } catch { }
                return response;
            }
        }
        internal static int RunLive()
        {
            return Task.Run(delegate {
                var results = new List<object>();
                const string sample = "The window closes automatically.";
                foreach (string model in IndexTranslator.Models)
                {
                    var observer = new LiveObserver(); var clock = Stopwatch.StartNew();
                    string output = null, error = null;
                    using (var index = new IndexTranslator(observer))
                    {
                        try { output = index.Translate(sample, "zh-Hans", model, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    }
                    clock.Stop();
                    results.Add(new { requestedModel = model, actualModel = observer.ActualModel, success = error == null, milliseconds = clock.ElapsedMilliseconds, httpStatus = observer.Status, attempts = observer.Calls, output = output, error = error });
                    File.WriteAllText(Path.Combine(Program.Folder, "index-live-test.json"), new JavaScriptSerializer().Serialize(new { testedAtUtc = DateTime.UtcNow.ToString("o"), endpoint = IndexTranslator.Endpoint, sample = sample, target = "zh-Hans", results = results }));
                }
                return 0;
            }).GetAwaiter().GetResult();
        }
    }
}
