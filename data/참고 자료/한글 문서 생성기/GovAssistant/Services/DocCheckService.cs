using System.Text;
using System.Text.RegularExpressions;
using GovAssistant.Models;

namespace GovAssistant.Services;

/// <summary>
/// 공문 사전 점검 오케스트레이터.
/// 결정적 규칙 엔진(정규식)으로 형식 오류를 즉시 잡고,
/// 선택적으로 AiService를 호출해 문체·맞춤법·용어를 추가 점검한 뒤 병합한다.
/// </summary>
public class DocCheckService
{
    private readonly AiService _ai;

    public DocCheckService(AiService? ai = null) => _ai = ai ?? new AiService();

    /// <summary>평문 텍스트를 점검. useAi=true면 규칙+AI, false면 규칙만.</summary>
    public async Task<List<CheckItem>> CheckTextAsync(
        string text, AppConfig cfg, bool useAi, IProgress<string>? progress = null)
    {
        var items = new List<CheckItem>();

        progress?.Report("규칙 점검 중...");
        items.AddRange(RunRuleChecks(text));

        if (useAi)
        {
            progress?.Report("AI 점검 중...");
            try
            {
                items.AddRange(await _ai.CheckDocumentAsync(text, cfg, progress));
            }
            catch (Exception ex)
            {
                items.Add(new CheckItem(CheckSeverity.Warning, CheckSource.Ai,
                    "AI 점검 실패", "-",
                    $"AI 점검을 수행하지 못했습니다: {Short(ex.Message)}",
                    "설정 탭에서 제공자/API 키/모델을 확인하거나 'AI 점검 포함'을 끄고 규칙 점검만 사용하세요."));
            }
        }

        return Sort(items);
    }

    /// <summary>로드된 HWPX 양식을 평문으로 환원한 뒤 동일 규칙으로 점검.</summary>
    public Task<List<CheckItem>> CheckHwpxAsync(
        HwpxTemplate tpl, AppConfig cfg, bool useAi, IProgress<string>? progress = null)
        => CheckTextAsync(FlattenTemplate(tpl), cfg, useAi, progress);

    /// <summary>HWPX 단락+표를 줄 단위 평문으로 환원.</summary>
    public static string FlattenTemplate(HwpxTemplate tpl)
    {
        var sb = new StringBuilder();
        foreach (var p in tpl.Paragraphs)
            if (!string.IsNullOrWhiteSpace(p.Text)) sb.AppendLine(p.Text);
        foreach (var t in tpl.Tables)
            foreach (var row in t.Rows)
            {
                var line = string.Join(" ", row.Where(c => !string.IsNullOrWhiteSpace(c)));
                if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
            }
        return sb.ToString();
    }

    // ── 규칙 엔진 ──────────────────────────────────────────────────────
    private static List<CheckItem> RunRuleChecks(string text)
    {
        var items = new List<CheckItem>();
        var raw = text ?? "";
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        CheckRequiredFields(raw, items);
        CheckDates(lines, items);
        CheckMarkerHierarchy(lines, items);
        CheckClosing(lines, items);
        CheckSpacingAndPunctuation(lines, items);
        CheckSpokenStyle(lines, items);

        return items;
    }

    // 필수 항목: 수신 / 제목 (경유는 선택)
    private static void CheckRequiredFields(string raw, List<CheckItem> items)
    {
        if (!Regex.IsMatch(raw, @"(^|\n)\s*수신\s*[:：]"))
            items.Add(new CheckItem(CheckSeverity.Error, CheckSource.Rule,
                "필수항목", "문서 전체",
                "'수신' 항목을 찾지 못했습니다.",
                "공문 상단에 '수신: ○○기관' 형식으로 수신 항목을 추가하세요."));

        if (!Regex.IsMatch(raw, @"(^|\n)\s*제목\s*[:：]"))
            items.Add(new CheckItem(CheckSeverity.Error, CheckSource.Rule,
                "필수항목", "문서 전체",
                "'제목' 항목을 찾지 못했습니다.",
                "공문에 '제목: ○○○' 형식으로 제목 항목을 추가하세요."));
    }

    // 날짜 형식: YYYY. M. D. (각 온점 뒤 한 칸, 끝에 온점)
    private static readonly Regex DateCandidate =
        new(@"\b(\d{4})\s*[.\-/]\s*(\d{1,2})\s*[.\-/]\s*(\d{1,2})\.?", RegexOptions.Compiled);
    private static readonly Regex DateCorrect =
        new(@"^\d{4}\. \d{1,2}\. \d{1,2}\.$", RegexOptions.Compiled);

    private static void CheckDates(string[] lines, List<CheckItem> items)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in DateCandidate.Matches(lines[i]))
            {
                var token = m.Value.Trim();
                var correct = $"{m.Groups[1].Value}. {int.Parse(m.Groups[2].Value)}. {int.Parse(m.Groups[3].Value)}.";
                if (!DateCorrect.IsMatch(token))
                    items.Add(new CheckItem(CheckSeverity.Warning, CheckSource.Rule,
                        "날짜형식", $"{i + 1}행",
                        $"날짜 표기 '{token}'가 행정 표준 형식과 다릅니다.",
                        $"'{correct}' 형식으로 표기하세요 (온점 뒤 한 칸, 끝에 온점)."));
            }
        }
    }

    // 항목 부호 계층: 1. → 가. → 1) → 가) → (1) → (가)
    private static (int level, string kind)? DetectMarker(string line)
    {
        var s = line.TrimStart();
        if (Regex.IsMatch(s, @"^\(\s*[가-힣]\s*\)")) return (6, "(가)");
        if (Regex.IsMatch(s, @"^\(\s*\d+\s*\)"))     return (5, "(1)");
        if (Regex.IsMatch(s, @"^[가-힣]\)"))          return (4, "가)");
        if (Regex.IsMatch(s, @"^\d+\)"))              return (3, "1)");
        if (Regex.IsMatch(s, @"^[가-힣]\.\s"))         return (2, "가.");
        if (Regex.IsMatch(s, @"^\d+\.\s"))            return (1, "1.");
        return null;
    }

    private static void CheckMarkerHierarchy(string[] lines, List<CheckItem> items)
    {
        var seenLevels = new HashSet<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            var marker = DetectMarker(lines[i]);
            if (marker is not { } mk) continue;
            // 상위 단계가 한 번도 등장하지 않았는데 하위 부호가 먼저 나오면 위반
            if (mk.level >= 2 && !seenLevels.Contains(mk.level - 1))
                items.Add(new CheckItem(CheckSeverity.Warning, CheckSource.Rule,
                    "항목부호", $"{i + 1}행",
                    $"상위 항목부호 없이 '{mk.kind}'(이)가 사용되었습니다.",
                    "항목부호는 '1. → 가. → 1) → 가) → (1) → (가)' 순서로 위 단계부터 사용하세요."));
            seenLevels.Add(mk.level);
        }
    }

    // 끝 표시: 마지막 본문 뒤에 '끝.'
    private static void CheckClosing(string[] lines, List<CheckItem> items)
    {
        var lastNonEmpty = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (lastNonEmpty.Length == 0) return;
        bool hasClosing = lines.Any(l => l.Contains("끝."));
        if (!hasClosing)
            items.Add(new CheckItem(CheckSeverity.Suggestion, CheckSource.Rule,
                "종결표시", "문서 끝",
                "본문 종결 표시 '끝.'을 찾지 못했습니다.",
                "본문(또는 붙임)의 마지막 글자에서 한 칸 띄우고 '끝.'을 표기하세요."));
    }

    // 이중 공백 / 반복 문장부호
    private static void CheckSpacingAndPunctuation(string[] lines, List<CheckItem> items)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\S {2,}\S"))
                items.Add(new CheckItem(CheckSeverity.Suggestion, CheckSource.Rule,
                    "띄어쓰기", $"{i + 1}행",
                    "연속된 공백(두 칸 이상)이 있습니다.",
                    "불필요한 연속 공백을 한 칸으로 정리하세요."));
            if (Regex.IsMatch(lines[i], @"[.,!?]{2,}"))
                items.Add(new CheckItem(CheckSeverity.Suggestion, CheckSource.Rule,
                    "문장부호", $"{i + 1}행",
                    "문장부호가 연속으로 반복됩니다.",
                    "반복된 문장부호를 하나로 정리하세요."));
        }
    }

    // 구어체/해요체 (공문은 평서형) — 결정적으로 잡히는 어미만
    private static readonly Regex SpokenStyle =
        new(@"(해요|예요|에요|거예요|구요|하죠|네요|는데요)([\s.,!?]|$)", RegexOptions.Compiled);

    private static void CheckSpokenStyle(string[] lines, List<CheckItem> items)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var m = SpokenStyle.Match(lines[i]);
            if (m.Success)
                items.Add(new CheckItem(CheckSeverity.Warning, CheckSource.Rule,
                    "문체", $"{i + 1}행",
                    $"구어체 표현('{m.Groups[1].Value}')이 사용되었습니다.",
                    "공문은 평서형 문체로 작성하세요 (예: '~합니다' 대신 '~한다')."));
        }
    }

    // ── 유틸 ──────────────────────────────────────────────────────────
    private static List<CheckItem> Sort(List<CheckItem> items) =>
        items.OrderBy(x => (int)x.Severity)
             .ThenBy(x => x.Source == CheckSource.Rule ? 0 : 1)
             .ThenBy(x => x.Category, StringComparer.Ordinal)
             .ToList();

    private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
