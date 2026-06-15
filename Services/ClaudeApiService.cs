using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClashRuleEngine.Services
{
    public class ClaudeApiException : Exception
    {
        public ClaudeApiException(string message) : base(message) { }
    }

    /// <summary>
    /// Minimal transport for the Claude Messages API over raw HTTP. Deliberately
    /// SDK-free: this ships as a single-DLL Navisworks plugin, so we use only
    /// framework assemblies (System.Net.Http + System.Web.Extensions JSON) rather
    /// than dragging the Anthropic SDK's NuGet dependency chain into the NW process.
    ///
    /// One non-streaming POST to /v1/messages with adaptive thinking; returns the
    /// concatenated text blocks (thinking blocks are skipped).
    /// </summary>
    public static class ClaudeApiService
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string Model = "claude-opus-4-8";
        private const string AnthropicVersion = "2023-06-01";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // .NET 4.8 negotiates modern TLS by default, but be explicit.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // Tls13 where supported
            return new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        }

        public static async Task<string> SendAsync(string apiKey, string system, string userText, int maxTokens = 16000)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ClaudeApiException("No Claude API key set. Add one in Settings → General.");

            var body = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["max_tokens"] = maxTokens,
                ["system"] = system,
                ["thinking"] = new Dictionary<string, object> { ["type"] = "adaptive" },
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = userText }
                }
            };
            string json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(body);

            using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", AnthropicVersion);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req).ConfigureAwait(false); }
                catch (Exception ex) { throw new ClaudeApiException("Network error contacting Claude: " + ex.Message); }

                string respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new ClaudeApiException($"Claude API error {(int)resp.StatusCode}: {ExtractError(respText)}");

                return ExtractText(respText);
            }
        }

        private static string ExtractText(string responseJson)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.Deserialize<Dictionary<string, object>>(responseJson);
            if (root == null) throw new ClaudeApiException("Empty response from Claude.");

            object stop;
            if (root.TryGetValue("stop_reason", out stop) && Convert.ToString(stop) == "refusal")
                throw new ClaudeApiException("Claude declined this request (safety refusal).");

            object contentObj;
            if (!root.TryGetValue("content", out contentObj) || !(contentObj is IEnumerable))
                throw new ClaudeApiException("Unexpected response shape from Claude.");

            var sb = new StringBuilder();
            foreach (var item in (IEnumerable)contentObj)
            {
                var block = item as Dictionary<string, object>;
                if (block == null) continue;
                object t; block.TryGetValue("type", out t);
                object txt;
                if (Convert.ToString(t) == "text" && block.TryGetValue("text", out txt))
                    sb.Append(Convert.ToString(txt));
            }
            return sb.ToString();
        }

        private static string ExtractError(string responseJson)
        {
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(responseJson);
                object e;
                if (root != null && root.TryGetValue("error", out e) && e is Dictionary<string, object>)
                {
                    object m;
                    if (((Dictionary<string, object>)e).TryGetValue("message", out m))
                        return Convert.ToString(m);
                }
            }
            catch { }
            return responseJson != null && responseJson.Length > 300 ? responseJson.Substring(0, 300) : responseJson;
        }
    }
}
