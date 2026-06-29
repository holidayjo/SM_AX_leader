using System.IO;
using System.Security.Cryptography;
using System.Text;
using GovAssistant.Models;
using Newtonsoft.Json;

namespace GovAssistant.Services;

public static class AppConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    // DPAPI(CurrentUser)로 암호화한 API 키는 "ENC:" 접두사로 식별.
    private const string EncPrefix = "ENC:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CDSAhwpx-AI.v1");

    public static AppConfig Load()
    {
        // env vars take priority
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? "";

        if (!File.Exists(ConfigPath))
            return new AppConfig { ApiKey = envKey };

        try
        {
            var saved = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(ConfigPath));
            if (saved == null) return new AppConfig { ApiKey = envKey };
            // 저장된 키 복호화 (기존 평문 키는 그대로 통과 → 마이그레이션)
            saved.ApiKey = DecryptKey(saved.ApiKey);
            // env vars override saved key
            if (!string.IsNullOrEmpty(envKey)) saved.ApiKey = envKey;
            return saved;
        }
        catch { return new AppConfig { ApiKey = envKey }; }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            // 디스크에는 암호화된 키로 저장하되, 호출자가 들고 있는 cfg는 건드리지 않도록 복제.
            var toSave = JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(cfg))!;
            toSave.ApiKey = EncryptKey(cfg.ApiKey);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(toSave, Formatting.Indented));
        }
        catch { /* non-critical */ }
    }

    private static string EncryptKey(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var enc = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return EncPrefix + Convert.ToBase64String(enc);
        }
        catch { return plain; } // 암호화 불가 환경이면 최소한 동작은 유지
    }

    private static string DecryptKey(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(EncPrefix)) return stored; // 기존 평문 → 그대로
        try
        {
            var raw = Convert.FromBase64String(stored.Substring(EncPrefix.Length));
            var dec = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; } // 다른 사용자/PC에서 복호화 실패 시 빈 키
    }
}
