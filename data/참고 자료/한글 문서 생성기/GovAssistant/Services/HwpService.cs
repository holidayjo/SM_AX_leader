using System.Runtime.InteropServices;

namespace GovAssistant.Services;

/// <summary>
/// 범정부오피스/한컴오피스 한글 COM 연결.
/// Activator.CreateInstance(Type.GetTypeFromProgID) 우선 → GetActiveObject(P/Invoke) 폴백.
/// </summary>
public class HwpService : IDisposable
{
    private dynamic? _hwp;
    public bool IsConnected => _hwp != null;
    public string? LastError { get; private set; }

    private static readonly string[] KnownProgIds =
    [
        "HWPFrame.HwpObject",
        "HWPFrame.HwpObject.1",
        "HNCHwpObject.HwpObject"
    ];

    [DllImport("ole32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    public bool Connect()
    {
        LastError = null;
        foreach (var pid in KnownProgIds)
        {
            // 1순위: Activator.CreateInstance (한글이 안 떠 있어도 자동 실행)
            try
            {
                var t = Type.GetTypeFromProgID(pid, throwOnError: false);
                if (t != null)
                {
                    var obj = Activator.CreateInstance(t);
                    if (obj != null)
                    {
                        _hwp = obj;
                        TryRegisterSecurityModule();
                        return true;
                    }
                }
            }
            catch (Exception ex) { LastError = $"CreateInstance({pid}): {ex.Message}"; }

            // 2순위: ROT에서 이미 실행 중인 인스턴스 찾기
            try
            {
                if (CLSIDFromProgID(pid, out var clsid) == 0
                    && GetActiveObject(ref clsid, IntPtr.Zero, out var rotObj) == 0
                    && rotObj != null)
                {
                    _hwp = rotObj;
                    TryRegisterSecurityModule();
                    return true;
                }
            }
            catch (Exception ex) { LastError = $"GetActiveObject({pid}): {ex.Message}"; }
        }
        return false;
    }

    private void TryRegisterSecurityModule()
    {
        // 한글의 자동화 보안 경고 우회 (선택)
        try { ((dynamic)_hwp!).RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); }
        catch
        {
            try { ((dynamic)_hwp!).RegisterModule("FilePathCheckDLL", "AutomationModule"); } catch { }
        }
    }

    public bool ShowWindow(bool visible = true)
    {
        if (_hwp == null) return false;
        try
        {
            dynamic hwp = _hwp;
            try { hwp.XHwpWindows.Item(0).Visible = visible; return true; } catch { }
            try { hwp.Visible = visible; return true; } catch { }
            return false;
        }
        catch { return false; }
    }

    public bool OpenFile(string path)
    {
        var raw = _hwp;
        if (raw == null) return false;
        try
        {
            dynamic hwp = raw!;
            ShowWindow(true);
            // 현재 문서가 있으면 닫기 (잠금 해제 + 깔끔한 재오픈)
            try { hwp.XHwpDocuments.Item(0).Close(false); } catch { }
            try { hwp.Clear(1); } catch { }

            try { hwp.Open(path, "HWPX", "forceopen:true"); return true; } catch { }
            try { hwp.Open(path, "HWP",  "forceopen:true"); return true; } catch { }
            try { hwp.Open(path); return true; } catch { }
            return false;
        }
        catch (Exception ex) { LastError = $"Open: {ex.Message}"; return false; }
    }

    public bool InsertAtCursor(string text)
    {
        var raw = _hwp;
        if (raw == null || string.IsNullOrEmpty(text)) return false;
        try
        {
            dynamic hwp = raw!;
            var act = hwp.CreateAction("InsertText");
            var set = hwp.CreateSet("InsertText");
            set.SetItem("Text", text);
            act.Execute(set);
            return true;
        }
        catch
        {
            try { ((dynamic)raw!).PutFieldText("", text); return true; }
            catch { return false; }
        }
    }

    public bool SaveAs(string filePath)
    {
        var raw = _hwp;
        if (raw == null) return false;
        try
        {
            dynamic hwp = raw!;
            try { hwp.SaveAs(filePath, "HWPX", ""); return true; } catch { }
            try { hwp.SaveAs(filePath, "HWP",  ""); return true; } catch { }
            try { hwp.SaveAs(filePath);             return true; } catch { }
            return false;
        }
        catch (Exception ex) { LastError = $"SaveAs: {ex.Message}"; return false; }
    }

    public void Quit()
    {
        if (_hwp == null) return;
        try { ((dynamic)_hwp).Quit(); } catch { }
    }

    public void Dispose()
    {
        if (_hwp != null)
        {
            try { Marshal.ReleaseComObject(_hwp); } catch { }
            _hwp = null;
        }
    }
}
