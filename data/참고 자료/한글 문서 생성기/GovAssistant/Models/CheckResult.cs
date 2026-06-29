namespace GovAssistant.Models;

/// <summary>점검 항목의 심각도. 표시 순서·색상 기준.</summary>
public enum CheckSeverity
{
    Error = 0,      // 오류 — 행정 공문 규칙 위반(반드시 수정)
    Warning = 1,    // 경고 — 형식/문체 권장 위반
    Suggestion = 2  // 제안 — 개선 권고
}

/// <summary>점검 항목의 출처. 규칙 엔진 vs AI.</summary>
public enum CheckSource
{
    Rule,
    Ai
}

/// <summary>단일 점검 결과 항목.</summary>
public record CheckItem(
    CheckSeverity Severity,
    CheckSource Source,
    string Category,    // 분류(예: 날짜형식, 필수항목, 문체)
    string Location,    // 위치(예: "3행", "제목", "본문")
    string Issue,       // 문제 설명
    string Suggestion); // 수정안
