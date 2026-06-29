using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using GovAssistant.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GovAssistant.Services;

public class AiService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private const string SystemPrompt =
        "당신은 대한민국 행정기관 공문서 작성 전문가입니다.\n" +
        "사용자가 제공한 핵심 내용을 표준 행정 공문 형식으로 변환해 주세요.\n\n" +
        "공문 형식 (반드시 준수):\n" +
        "수신: [수신 기관]\n" +
        "발신: [발신 기관]\n" +
        "제목: [제목]\n\n" +
        "1. [본문 내용]\n\n" +
        "  끝.\n\n" +
        "붙임: [필요시만 작성]\n\n" +
        "규칙:\n" +
        "- 간결하고 정확한 행정 문체 사용\n" +
        "- 존댓말 금지 (공문은 평서형)\n" +
        "- 날짜는 YYYY. MM. DD. 형식\n" +
        "- 수신·발신 기관이 명시되지 않으면 [수신 기관], [발신 기관]으로 표기";

    public async Task<string> GenerateAsync(string userInput, AppConfig cfg,
        IProgress<string>? progress = null)
    {
        var prompt = $"{SystemPrompt}\n\n[사용자 입력]\n{userInput}";
        return await CallProviderAsync(prompt, cfg, progress);
    }

    public record FormRewrite(
        Dictionary<int, string> Paragraphs,
        Dictionary<int, List<List<string>>> Tables,
        string RawResponse);

    /// <summary>
    /// 단락 + 표를 모두 받아 AI에 보내고, 단락 치환 + 표 행 데이터를 함께 반환.
    /// 표는 행 수 증감 자유 (AI 판단).
    /// </summary>
    public async Task<FormRewrite> RewriteFormAsync(
        IEnumerable<HwpxTemplate.ParagraphRef> paragraphs,
        IEnumerable<HwpxTemplate.TableRef> tables,
        string userText, AppConfig cfg, IProgress<string>? progress = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[양식 단락 목록] — 표 바깥의 본문 단락. 번호는 정수.");
        foreach (var p in paragraphs)
        {
            var t = string.IsNullOrWhiteSpace(p.Text) ? "[EMPTY]" : p.Text;
            sb.AppendLine($"{p.Id}: {t}");
        }
        sb.AppendLine();
        sb.AppendLine("[양식 표 목록] — 각 표의 원본 행 구조. 표 번호는 정수.");
        foreach (var tbl in tables)
        {
            sb.AppendLine($"표 {tbl.Id} ({tbl.RowCount}행 {tbl.ColCount}열):");
            for (int r = 0; r < tbl.Rows.Count; r++)
            {
                var row = string.Join(" | ", tbl.Rows[r].Select(c => string.IsNullOrWhiteSpace(c) ? "[empty]" : c));
                sb.AppendLine($"  행{r}: {row}");
            }
        }

        var prompt =
            "당신은 대한민국 행정기관 공문서 작성 전문가입니다.\n\n" +
            "아래 [양식 단락 목록]과 [양식 표 목록]은 공문 **레이아웃 가이드**일 뿐입니다.\n" +
            "실제 텍스트는 무시하고 **[사용자 입력]**을 바탕으로 모든 단락과 표 내용을 새로 작성하세요.\n\n" +
            "## 출력 형식 (반드시 이 구조):\n" +
            "```\n" +
            "{\n" +
            "  \"paragraphs\": {\n" +
            "    \"0\": \"수신: 행정안전부\",\n" +
            "    \"1\": \"제목: 회의 개최 안내\"\n" +
            "  },\n" +
            "  \"tables\": {\n" +
            "    \"0\": [[\"일시\", \"장소\"], [\"2026. 6. 1.\", \"본관\"]]\n" +
            "  }\n" +
            "}\n" +
            "```\n\n" +
            "## 규칙\n" +
            "- 응답은 위 형식의 JSON 객체 **하나만** 출력 (앞뒤 설명/코드블록 마커 금지)\n" +
            "- 키는 반드시 **정수**(따옴표 안에 숫자만, 예: \"0\", \"1\", \"2\")\n" +
            "- paragraphs: **모든 단락 번호에 대해** 새 텍스트를 작성 (구조 유지, 내용은 새로)\n" +
            "- 표제어(\"수신:\", \"발신:\", \"제목:\" 등)는 라벨 유지하고 실제 값을 이어 붙이기\n" +
            "- 원본 \"  끝.\" 같은 종결부는 그대로\n" +
            "- tables: 사용자 데이터에 맞춰 행을 늘리거나 줄임. 첫 행이 헤더면 헤더 유지\n" +
            "- 빈 셀은 \"\"\n" +
            "- 평서형(존댓말 금지), 날짜는 YYYY. MM. DD. 형식\n\n" +
            sb.ToString() + "\n" +
            "[사용자 입력]\n" + userText + "\n\n" +
            "JSON:";

        var raw = await CallProviderAsync(prompt, cfg, progress);
        var (paraResult, tableResult) = ParseFormRewrite(raw);
        return new FormRewrite(paraResult, tableResult, raw ?? "");
    }

    // ── 사전 점검 (문체·맞춤법·행정용어) ───────────────────────────────
    private const string CheckSystemPrompt =
        "당신은 대한민국 행정기관 공문서 감수 전문가입니다.\n" +
        "아래 [문서]를 행정 공문 작성 기준으로 점검하고 문제를 지적하세요.\n" +
        "형식(날짜형식, 항목부호, 끝 표시 등)은 별도 규칙 엔진이 이미 검사하므로,\n" +
        "당신은 **문체, 맞춤법·어법, 행정용어 적절성, 모호하거나 누락된 정보**에 집중하세요.\n\n" +
        "## 출력 형식 (반드시 이 JSON 객체 하나만):\n" +
        "{\n" +
        "  \"issues\": [\n" +
        "    {\"severity\": \"오류|경고|제안\", \"category\": \"분류\", \"location\": \"위치(예: 제목, 3행, 본문)\",\n" +
        "     \"issue\": \"문제 설명\", \"suggestion\": \"수정안\"}\n" +
        "  ]\n" +
        "}\n\n" +
        "## 규칙\n" +
        "- 응답은 위 JSON 객체 하나만 출력 (앞뒤 설명/코드블록 마커 금지)\n" +
        "- severity는 반드시 '오류', '경고', '제안' 중 하나\n" +
        "- 지적할 문제가 없으면 \"issues\": [] 로 출력\n" +
        "- 형식(날짜·항목부호·끝표시)은 중복 지적하지 말 것";

    /// <summary>문서를 AI로 점검해 CheckItem 목록을 반환. 형식 오류는 규칙 엔진이 담당.</summary>
    public async Task<List<CheckItem>> CheckDocumentAsync(
        string text, AppConfig cfg, IProgress<string>? progress = null)
    {
        var prompt = $"{CheckSystemPrompt}\n\n[문서]\n{text}\n\nJSON:";
        var raw = await CallProviderAsync(prompt, cfg, progress, jsonMode: true);
        return ParseCheckItems(raw);
    }

    private static CheckSeverity MapSeverity(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "오류" or "error" or "critical" or "high" => CheckSeverity.Error,
            "경고" or "warning" or "medium"           => CheckSeverity.Warning,
            _                                         => CheckSeverity.Suggestion
        };
    }

    private static List<CheckItem> ParseCheckItems(string raw)
    {
        var items = new List<CheckItem>();
        var s = raw?.Trim() ?? "";
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');

        JArray? arr = null;
        if (i >= 0 && j > i)
        {
            try
            {
                var jo = JObject.Parse(s.Substring(i, j - i + 1));
                arr = jo["issues"] as JArray;
            }
            catch { }
        }
        // 폴백: 최상위가 배열인 응답도 수용
        if (arr == null)
        {
            var a = s.IndexOf('[');
            var b = s.LastIndexOf(']');
            if (a >= 0 && b > a)
            {
                try { arr = JArray.Parse(s.Substring(a, b - a + 1)); } catch { }
            }
        }
        if (arr == null) return items;

        foreach (var t in arr)
        {
            if (t is not JObject o) continue;
            var issue = o["issue"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(issue)) continue;
            items.Add(new CheckItem(
                MapSeverity(o["severity"]?.ToString()),
                CheckSource.Ai,
                o["category"]?.ToString() ?? "기타",
                o["location"]?.ToString() ?? "-",
                issue,
                o["suggestion"]?.ToString() ?? ""));
        }
        return items;
    }

    private static bool TryParseLooseInt(string s, out int v)
    {
        // "0", "P0", "T0", "단락 0", "row 12" 같은 잡음에서 정수 추출
        v = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out v);
    }

    private static (Dictionary<int, string>, Dictionary<int, List<List<string>>>) ParseFormRewrite(string raw)
    {
        var paragraphs = new Dictionary<int, string>();
        var tables = new Dictionary<int, List<List<string>>>();

        var s = raw?.Trim() ?? "";
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');
        if (i < 0 || j <= i) return (paragraphs, tables);

        try
        {
            var jo = JObject.Parse(s.Substring(i, j - i + 1));
            if (jo["paragraphs"] is JObject pj)
            {
                foreach (var prop in pj.Properties())
                    if (TryParseLooseInt(prop.Name, out var idx))
                        paragraphs[idx] = prop.Value?.ToString() ?? "";
            }
            if (jo["tables"] is JObject tj)
            {
                foreach (var prop in tj.Properties())
                {
                    if (!TryParseLooseInt(prop.Name, out var tid)) continue;
                    if (prop.Value is not JArray rowsArr) continue;
                    var rows = new List<List<string>>();
                    foreach (var rowToken in rowsArr)
                    {
                        if (rowToken is not JArray cellsArr) continue;
                        rows.Add(cellsArr.Select(c => c?.ToString() ?? "").ToList());
                    }
                    tables[tid] = rows;
                }
            }
            // 폴백: 최상위에 정수 키만 있는 응답도 수용
            if (paragraphs.Count == 0 && tables.Count == 0)
            {
                foreach (var prop in jo.Properties())
                    if (TryParseLooseInt(prop.Name, out var idx))
                        paragraphs[idx] = prop.Value?.ToString() ?? "";
            }
        }
        catch { }

        return (paragraphs, tables);
    }

    private Task<string> CallProviderAsync(string prompt, AppConfig cfg,
        IProgress<string>? progress, bool jsonMode = false)
        => cfg.Provider switch
        {
            "gemini" => CallGeminiAsync(prompt, cfg, progress, jsonMode),
            "openai" => CallOpenAiAsync(prompt, cfg, progress, jsonMode),
            "claude" => CallClaudeAsync(prompt, cfg, progress, jsonMode),
            _        => CallOllamaAsync(prompt, cfg, progress, jsonMode)
        };

    // ── 모델 목록 조회 (API 키 테스트 겸용) ────────────────────────────
    public Task<List<string>> ListModelsAsync(AppConfig cfg) => cfg.Provider switch
    {
        "gemini" => ListGeminiModelsAsync(cfg),
        "openai" => ListOpenAiModelsAsync(cfg),
        "claude" => ListClaudeModelsAsync(cfg),
        _        => ListOllamaModelsAsync(cfg)
    };

    private async Task<List<string>> ListOpenAiModelsAsync(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("OpenAI API 키가 비어 있습니다.");
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var jo = JObject.Parse(await res.Content.ReadAsStringAsync());
        return jo["data"]?
            .Select(x => x["id"]?.ToString() ?? "")
            .Where(s => !string.IsNullOrEmpty(s) && (s.StartsWith("gpt-") || s.StartsWith("o1") || s.StartsWith("o3") || s.StartsWith("o4") || s.StartsWith("chatgpt")))
            .OrderBy(s => s)
            .ToList() ?? new();
    }

    private async Task<List<string>> ListGeminiModelsAsync(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("Gemini API 키가 비어 있습니다.");
        var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={cfg.ApiKey}";
        var res = await Http.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var jo = JObject.Parse(await res.Content.ReadAsStringAsync());
        return jo["models"]?
            .Where(m => m["supportedGenerationMethods"]?.Any(x => x.ToString() == "generateContent") == true)
            .Select(m => (m["name"]?.ToString() ?? "").Replace("models/", ""))
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s)
            .ToList() ?? new();
    }

    private async Task<List<string>> ListClaudeModelsAsync(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("Claude API 키가 비어 있습니다.");
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var jo = JObject.Parse(await res.Content.ReadAsStringAsync());
        return jo["data"]?
            .Select(x => x["id"]?.ToString() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderByDescending(s => s)
            .ToList() ?? new();
    }

    private async Task<List<string>> ListOllamaModelsAsync(AppConfig cfg)
    {
        var url = cfg.OllamaUrl.TrimEnd('/');
        var res = await Http.GetAsync($"{url}/api/tags");
        res.EnsureSuccessStatusCode();
        var jo = JObject.Parse(await res.Content.ReadAsStringAsync());
        return jo["models"]?
            .Select(x => x["name"]?.ToString() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s)
            .ToList() ?? new();
    }

    // ── Ollama ──────────────────────────────────────────────────────────
    private async Task<string> CallOllamaAsync(string prompt, AppConfig cfg,
        IProgress<string>? progress, bool jsonMode = false)
    {
        var url = (cfg.OllamaUrl.TrimEnd('/'));
        var model = cfg.OllamaModel.Trim();
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException("Ollama 모델명을 설정에서 입력해 주세요.");

        progress?.Report("Ollama 호출 중...");
        // jsonMode: format="json"으로 유효한 JSON만 강제 + 추출은 결정적으로 temp 0.
        var body = new
        {
            model,
            stream = false,
            format = jsonMode ? "json" : null,
            messages = new[] { new { role = "user", content = prompt } },
            options = new { temperature = jsonMode ? 0.0 : 0.3 }
        };
        var res = await Http.PostAsync($"{url}/api/chat",
            new StringContent(JsonConvert.SerializeObject(body,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
                Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var json = JObject.Parse(await res.Content.ReadAsStringAsync());
        return json["message"]?["content"]?.ToString() ?? "";
    }

    // ── Gemini ──────────────────────────────────────────────────────────
    private async Task<string> CallGeminiAsync(string prompt, AppConfig cfg,
        IProgress<string>? progress, bool jsonMode = false)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("Gemini API 키가 설정되지 않았습니다.");

        var model = string.IsNullOrEmpty(cfg.Model) ? "gemini-2.5-flash" : cfg.Model;
        progress?.Report("Gemini 호출 중...");
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={cfg.ApiKey}";

        // 추론 모델의 과도한 thinking → 지연(수십~100초) 완화.
        // 표 채우기/공문 작성은 깊은 추론이 불필요하므로 thinking을 낮춤.
        // 모델 세대별로 필드가 다름: gemini-3.x = thinkingLevel, 2.5 = thinkingBudget(토큰).
        var genConfig = new JObject
        {
            ["temperature"] = 0.3,
            ["maxOutputTokens"] = 8192
        };
        if (model.Contains("gemini-3"))
            genConfig["thinkingConfig"] = new JObject { ["thinkingLevel"] = "low" };
        else if (model.Contains("2.5"))
            genConfig["thinkingConfig"] = new JObject { ["thinkingBudget"] = 512 };
        if (jsonMode)
            genConfig["responseMimeType"] = "application/json";

        var body = new JObject
        {
            ["contents"] = new JArray { new JObject { ["parts"] = new JArray { new JObject { ["text"] = prompt } } } },
            ["generationConfig"] = genConfig
        };
        var res = await Http.PostAsync(url,
            new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var json = JObject.Parse(await res.Content.ReadAsStringAsync());
        return json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString() ?? "";
    }

    // ── OpenAI ──────────────────────────────────────────────────────────
    private async Task<string> CallOpenAiAsync(string prompt, AppConfig cfg,
        IProgress<string>? progress, bool jsonMode = false)
    {
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("OpenAI API 키가 설정되지 않았습니다.");

        var model = string.IsNullOrEmpty(cfg.Model) ? "gpt-4o-mini" : cfg.Model;
        progress?.Report("OpenAI 호출 중...");
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } },
            ["temperature"] = jsonMode ? 0.0 : 0.3,
            ["max_tokens"] = 8192
        };
        if (jsonMode)
            body["response_format"] = new JObject { ["type"] = "json_object" };
        req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = JObject.Parse(await res.Content.ReadAsStringAsync());
        return json["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
    }

    // ── Claude ──────────────────────────────────────────────────────────
    private async Task<string> CallClaudeAsync(string prompt, AppConfig cfg,
        IProgress<string>? progress, bool jsonMode = false)
    {
        // Claude는 별도 json 모드가 없어 프리필로 유도(jsonMode 시 응답 앞 '{' 보강).
        if (string.IsNullOrEmpty(cfg.ApiKey))
            throw new InvalidOperationException("Claude API 키가 설정되지 않았습니다.");

        var model = string.IsNullOrEmpty(cfg.Model) ? "claude-sonnet-4-8" : cfg.Model;
        progress?.Report("Claude 호출 중...");
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var messages = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } };
        if (jsonMode) // assistant 프리필 '{' → JSON으로 시작하도록 유도
            messages.Add(new JObject { ["role"] = "assistant", ["content"] = "{" });
        var body = new JObject
        {
            ["model"] = model,
            ["max_tokens"] = 8192,
            ["messages"] = messages
        };
        req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = JObject.Parse(await res.Content.ReadAsStringAsync());
        var text = json["content"]?[0]?["text"]?.ToString() ?? "";
        // 프리필한 '{'는 응답에 안 포함되므로 다시 붙여 완전한 JSON 복원
        return jsonMode ? "{" + text : text;
    }
}
