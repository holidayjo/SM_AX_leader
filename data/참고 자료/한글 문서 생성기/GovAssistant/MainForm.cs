using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GovAssistant.Models;
using GovAssistant.Services;

namespace GovAssistant;

public class MainForm : Form
{
    private AppConfig _cfg = AppConfigService.Load();
    private HwpService _hwp = new();
    private HwpxTemplate? _template;
    private string? _currentTempPath;

    // ── Tabs ──
    private TabControl _tabs = null!;

    // ── Main tab controls ──
    private Label    _lblForm        = null!;
    private Button   _btnLoadForm    = null!;
    private TextBox  _txtUserInput   = null!;
    private Button   _btnLoadTxt     = null!;
    private Button   _btnApply       = null!;
    private Button   _btnSaveAs      = null!;
    private Button   _btnConnect     = null!;
    private Label    _lblStatus      = null!;
    private Label    _lblHwpStatus   = null!;
    private ProgressBar _progress    = null!;
    private System.Windows.Forms.Timer _progressTimer = null!;
    private DateTime _aiStartUtc;
    private const int EstimatedAiSeconds = 15;

    // ── Settings tab controls ──
    private ComboBox _cboProvider    = null!;
    private TextBox  _txtApiKey      = null!;
    private TextBox  _txtOllamaUrl   = null!;
    private ComboBox _cboModel       = null!;
    private Button   _btnTestModels  = null!;
    private Label    _lblTestResult  = null!;
    private Label    _lblApiKeyHint  = null!;
    private Label    _lblOllamaHint  = null!;
    private Panel    _rowApiKey      = null!;
    private Panel    _rowOllamaUrl   = null!;

    // ── 사전 점검 tab controls ──
    private TextBox      _txtCheckInput   = null!;
    private CheckBox     _chkUseAi        = null!;
    private Button       _btnRunCheck     = null!;
    private Button       _btnLoadCheckSrc = null!;
    private Button       _btnPullFromMain = null!;
    private Button       _btnCopyResults  = null!;
    private DataGridView _gridResults     = null!;
    private Label        _lblCheckStatus  = null!;
    private ProgressBar  _checkProgress   = null!;
    private readonly DocCheckService _checkSvc = new();
    private List<CheckItem> _lastCheckResults = new();

    public MainForm()
    {
        BuildUi();
        LoadSettingsToUi();
        TryConnectHwp();
    }

    // ── UI ────────────────────────────────────────────────────────────
    private void BuildUi()
    {
        Text          = "CDSAhwpx-AI";
        Width         = 980;
        Height        = 680;
        MinimumSize   = new System.Drawing.Size(840, 560);
        StartPosition = FormStartPosition.Manual;
        Location      = new System.Drawing.Point(_cfg.WindowX, _cfg.WindowY);
        Font          = new System.Drawing.Font("맑은 고딕", 9.5f);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildMainTab());
        _tabs.TabPages.Add(BuildCheckTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        Controls.Add(_tabs);

        FormClosing += OnFormClosing;
    }

    private TabPage BuildMainTab()
    {
        var page = new TabPage("공문 작성") { Padding = new Padding(10) };

        const int BtnH = 34;
        var btnFont = new System.Drawing.Font("맑은 고딕", 9.5f);

        // ── 상단: 양식 불러오기 + 파일 정보 ─────────────────────
        var topRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = BtnH + 8, ColumnCount = 2,
            Padding = new Padding(0, 4, 0, 4)
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _btnLoadForm = new Button
        {
            Text = "📂 양식 불러오기",
            Dock = DockStyle.Fill, Height = BtnH, Font = btnFont,
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnLoadForm.Click += OnLoadForm;

        _lblForm = new Label
        {
            Text = "(양식 없음 — .hwpx 파일을 선택하세요)",
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            ForeColor = System.Drawing.Color.Gray
        };
        topRow.Controls.Add(_btnLoadForm, 0, 0);
        topRow.Controls.Add(_lblForm, 1, 0);

        // ── 메인 본문 ────────────────────────────────────────────
        var mainRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3
        };
        mainRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));    // label
        mainRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // textbox
        mainRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, BtnH + 16)); // button row

        mainRoot.Controls.Add(new Label
        {
            Text = "사용자 텍스트:",
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 0, 0)
        }, 0, 0);

        _txtUserInput = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("맑은 고딕", 10f),
            PlaceholderText =
                "공문에 들어갈 내용을 자유롭게 작성하세요. AI가 양식 구조에 맞춰 전체 내용을 새로 작성합니다.\n\n" +
                "예) 6월 1일 14시 본관 3층 회의실에서 정기 회의를 개최한다.\n" +
                "    수신은 행정안전부, 발신은 ABC구청.\n" +
                "    참석 대상은 부서장 10명, 안건은 하반기 사업계획 검토."
        };
        mainRoot.Controls.Add(_txtUserInput, 0, 1);

        // ── 버튼 행 (FlowLayoutPanel — 안정적인 가로 배열) ─────
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Padding = new Padding(0, 8, 0, 0)
        };

        _btnLoadTxt = new Button
        {
            Text = "📄 .txt 불러오기",
            Width = 140, Height = BtnH, Font = btnFont,
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnLoadTxt.Click += OnLoadTxt;

        _btnApply = new Button
        {
            Text = "✨ AI 적용 → 한글 반영",
            Width = 220, Height = BtnH,
            Font = new System.Drawing.Font("맑은 고딕", 10f, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.FromArgb(0, 120, 212),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 16, 0)
        };
        _btnApply.FlatAppearance.BorderSize = 0;
        _btnApply.Click += OnApply;

        _btnSaveAs = new Button
        {
            Text = "💾 다른 이름으로 저장",
            Width = 180, Height = BtnH, Font = btnFont,
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnSaveAs.Click += OnSaveAs;

        _btnConnect = new Button
        {
            Text = "🔌 한글 재연결",
            Width = 130, Height = BtnH, Font = btnFont,
            Margin = new Padding(0)
        };
        _btnConnect.Click += (_, _) => TryConnectHwp();

        btnRow.Controls.AddRange([_btnLoadTxt, _btnApply, _btnSaveAs, _btnConnect]);
        mainRoot.Controls.Add(btnRow, 0, 2);

        // ── 상태바 ─────────────────────────────────────────
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 22, ColumnCount = 2
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        _lblStatus = new Label { Text = "준비", Dock = DockStyle.Fill, ForeColor = System.Drawing.Color.Gray };
        _lblHwpStatus = new Label
        {
            Text = "한글: 미연결", Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight,
            ForeColor = System.Drawing.Color.Firebrick
        };
        statusPanel.Controls.Add(_lblStatus, 0, 0);
        statusPanel.Controls.Add(_lblHwpStatus, 1, 0);

        _progress = new ProgressBar
        {
            Dock = DockStyle.Bottom, Height = 8,
            Minimum = 0, Maximum = 100, Value = 0,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        _progressTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _progressTimer.Tick += OnProgressTick;

        page.Controls.Add(mainRoot);
        page.Controls.Add(_progress);
        page.Controls.Add(statusPanel);
        page.Controls.Add(topRow);

        return page;
    }

    // ── 사전 점검 탭 ─────────────────────────────────────────────────
    private TabPage BuildCheckTab()
    {
        var page = new TabPage("사전 점검") { Padding = new Padding(10) };
        const int BtnH = 34;
        var btnFont = new System.Drawing.Font("맑은 고딕", 9.5f);

        // ── 상단 도구 행 ────────────────────────────────────────
        var topRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = BtnH + 10,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Padding = new Padding(0, 4, 0, 4)
        };
        _btnLoadCheckSrc = new Button
        {
            Text = "📂 .txt/.hwpx 불러오기", Width = 180, Height = BtnH, Font = btnFont,
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnLoadCheckSrc.Click += OnLoadCheckSource;

        _btnPullFromMain = new Button
        {
            Text = "📥 공문 작성 탭에서 가져오기", Width = 210, Height = BtnH, Font = btnFont,
            Margin = new Padding(0, 0, 16, 0)
        };
        _btnPullFromMain.Click += (_, _) =>
        {
            _txtCheckInput.Text = _txtUserInput.Text;
            SetCheckStatus("공문 작성 탭의 텍스트를 가져왔습니다.");
        };

        _chkUseAi = new CheckBox
        {
            Text = "AI 점검 포함 (문체·맞춤법·용어)", Checked = true,
            AutoSize = true, Font = btnFont, Margin = new Padding(0, 6, 0, 0)
        };
        topRow.Controls.AddRange([_btnLoadCheckSrc, _btnPullFromMain, _chkUseAi]);

        // ── 본문: 입력(위) + 결과(아래) 분할 ───────────────────
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterWidth = 6, Panel1MinSize = 80, Panel2MinSize = 80
        };
        // SplitterDistance는 컨트롤이 실제 크기를 가진 뒤 설정 (작은 초기 크기에서 예외 방지)
        split.HandleCreated += (_, _) => split.BeginInvoke(() =>
        {
            try { if (split.Height > 260) split.SplitterDistance = 180; } catch { }
        });

        // 입력 영역
        var inputRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        inputRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        inputRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inputRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, BtnH + 12));
        inputRoot.Controls.Add(new Label
        {
            Text = "점검 대상 텍스트:", Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 0, 0)
        }, 0, 0);
        _txtCheckInput = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("맑은 고딕", 10f),
            PlaceholderText = "점검할 공문 내용을 붙여넣거나 위 버튼으로 불러오세요."
        };
        inputRoot.Controls.Add(_txtCheckInput, 0, 1);

        var runRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 8, 0, 0)
        };
        _btnRunCheck = new Button
        {
            Text = "🔍 점검 실행", Width = 160, Height = BtnH,
            Font = new System.Drawing.Font("맑은 고딕", 10f, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.FromArgb(0, 120, 212),
            ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnRunCheck.FlatAppearance.BorderSize = 0;
        _btnRunCheck.Click += OnRunCheck;

        _btnCopyResults = new Button
        {
            Text = "📋 결과 복사", Width = 130, Height = BtnH, Font = btnFont,
            Margin = new Padding(0)
        };
        _btnCopyResults.Click += OnCopyResults;
        runRow.Controls.AddRange([_btnRunCheck, _btnCopyResults]);
        inputRoot.Controls.Add(runRow, 0, 2);
        split.Panel1.Controls.Add(inputRoot);

        // 결과 영역
        _gridResults = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, Font = btnFont, BackgroundColor = System.Drawing.Color.White,
            BorderStyle = BorderStyle.None
        };
        AddGridColumn("등급", 70, DataGridViewAutoSizeColumnMode.None);
        AddGridColumn("출처", 60, DataGridViewAutoSizeColumnMode.None);
        AddGridColumn("분류", 90, DataGridViewAutoSizeColumnMode.None);
        AddGridColumn("위치", 80, DataGridViewAutoSizeColumnMode.None);
        AddGridColumn("문제", 0, DataGridViewAutoSizeColumnMode.Fill);
        AddGridColumn("수정안", 0, DataGridViewAutoSizeColumnMode.Fill);
        _gridResults.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _gridResults.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        split.Panel2.Controls.Add(_gridResults);

        // ── 상태 + 진행 ─────────────────────────────────────────
        _lblCheckStatus = new Label
        {
            Text = "준비 — 텍스트를 입력하고 [점검 실행]을 누르세요.",
            Dock = DockStyle.Bottom, Height = 22, ForeColor = System.Drawing.Color.Gray
        };
        _checkProgress = new ProgressBar
        {
            Dock = DockStyle.Bottom, Height = 8, Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30, Visible = false
        };

        page.Controls.Add(split);
        page.Controls.Add(_checkProgress);
        page.Controls.Add(_lblCheckStatus);
        page.Controls.Add(topRow);
        return page;
    }

    private void AddGridColumn(string header, int width, DataGridViewAutoSizeColumnMode mode)
    {
        var col = new DataGridViewTextBoxColumn
        {
            HeaderText = header, AutoSizeMode = mode,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        };
        if (mode == DataGridViewAutoSizeColumnMode.None) col.Width = width;
        _gridResults.Columns.Add(col);
    }

    private async void OnRunCheck(object? s, EventArgs e)
    {
        var text = _txtCheckInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) { MessageBox.Show("점검할 텍스트를 입력하세요."); return; }

        SaveSettingsFromUi();   // AI 점검 시 현재 제공자/키/모델 반영
        SetCheckButtonsEnabled(false);
        _checkProgress.Visible = true;

        try
        {
            var progress = new Progress<string>(SetCheckStatus);
            var results = await _checkSvc.CheckTextAsync(text, _cfg, _chkUseAi.Checked, progress);
            _lastCheckResults = results;
            PopulateResults(results);

            int err = results.Count(r => r.Severity == CheckSeverity.Error);
            int warn = results.Count(r => r.Severity == CheckSeverity.Warning);
            int sug = results.Count(r => r.Severity == CheckSeverity.Suggestion);
            SetCheckStatus(results.Count == 0
                ? "✓ 점검 완료 — 지적 사항이 없습니다."
                : $"점검 완료 — 오류 {err} · 경고 {warn} · 제안 {sug} (총 {results.Count}건).");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"점검 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetCheckStatus("점검 실패.");
        }
        finally
        {
            _checkProgress.Visible = false;
            SetCheckButtonsEnabled(true);
        }
    }

    private void PopulateResults(List<CheckItem> items)
    {
        _gridResults.Rows.Clear();
        foreach (var it in items)
        {
            int idx = _gridResults.Rows.Add(
                SeverityLabel(it.Severity),
                it.Source == CheckSource.Rule ? "규칙" : "AI",
                it.Category, it.Location, it.Issue, it.Suggestion);
            _gridResults.Rows[idx].DefaultCellStyle.BackColor = SeverityColor(it.Severity);
        }
    }

    private static string SeverityLabel(CheckSeverity s) => s switch
    {
        CheckSeverity.Error   => "오류",
        CheckSeverity.Warning => "경고",
        _                     => "제안"
    };

    private static System.Drawing.Color SeverityColor(CheckSeverity s) => s switch
    {
        CheckSeverity.Error   => System.Drawing.Color.FromArgb(253, 231, 231), // 연한 적색
        CheckSeverity.Warning => System.Drawing.Color.FromArgb(255, 248, 225), // 연한 황색
        _                     => System.Drawing.Color.FromArgb(232, 240, 254)  // 연한 청색
    };

    private void OnLoadCheckSource(object? s, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "점검 대상 (*.txt;*.hwpx)|*.txt;*.hwpx|텍스트 (*.txt)|*.txt|HWPX (*.hwpx)|*.hwpx|모든 파일 (*.*)|*.*",
            Title  = "점검 대상 불러오기"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            if (Path.GetExtension(dlg.FileName).Equals(".hwpx", StringComparison.OrdinalIgnoreCase))
            {
                var tpl = HwpxTemplate.Load(dlg.FileName);
                _txtCheckInput.Text = DocCheckService.FlattenTemplate(tpl);
            }
            else
            {
                _txtCheckInput.Text = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
            }
            SetCheckStatus($"불러옴: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일 읽기 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnCopyResults(object? s, EventArgs e)
    {
        if (_lastCheckResults.Count == 0) { MessageBox.Show("복사할 점검 결과가 없습니다."); return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[공문 사전 점검 결과]");
        foreach (var it in _lastCheckResults)
            sb.AppendLine($"- [{SeverityLabel(it.Severity)}/{(it.Source == CheckSource.Rule ? "규칙" : "AI")}] " +
                          $"({it.Category}, {it.Location}) {it.Issue} → {it.Suggestion}");
        try { Clipboard.SetText(sb.ToString()); SetCheckStatus("결과를 클립보드에 복사했습니다."); }
        catch (Exception ex) { MessageBox.Show($"복사 오류: {ex.Message}"); }
    }

    private void SetCheckButtonsEnabled(bool enabled)
    {
        _btnRunCheck.Enabled     = enabled;
        _btnLoadCheckSrc.Enabled = enabled;
        _btnPullFromMain.Enabled = enabled;
        _btnCopyResults.Enabled  = enabled;
    }

    private void SetCheckStatus(string msg)
    {
        if (_lblCheckStatus.InvokeRequired) { _lblCheckStatus.Invoke(() => SetCheckStatus(msg)); return; }
        _lblCheckStatus.Text = msg;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("설정") { Padding = new Padding(16) };
        var btnFont = new System.Drawing.Font("맑은 고딕", 9.5f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        // ── 그룹 1: AI 제공자 ────────────────────────────
        var grpProv = new GroupBox
        {
            Text = "1. AI 제공자",
            Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        _cboProvider = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top, Height = 28, Font = btnFont
        };
        _cboProvider.Items.AddRange(["openai", "gemini", "claude", "ollama"]);
        _cboProvider.SelectedIndexChanged += OnProviderChangedAsync;
        grpProv.Controls.Add(_cboProvider);
        root.Controls.Add(grpProv);

        // ── 그룹 2: 연결 정보 ────────────────────────────
        var grpAuth = new GroupBox
        {
            Text = "2. 연결 정보",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var authTbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true
        };
        authTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        authTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _rowApiKey = new Panel { Dock = DockStyle.Fill, Height = 28 };
        _txtApiKey = new TextBox
        {
            PasswordChar = '●', Dock = DockStyle.Fill, Font = btnFont,
            PlaceholderText = "OpenAI / Gemini / Claude API 키"
        };
        _txtApiKey.Leave += async (_, _) => await AutoTestAsync();
        _rowApiKey.Controls.Add(_txtApiKey);
        AddRow(authTbl, "API 키:", _rowApiKey);

        _rowOllamaUrl = new Panel { Dock = DockStyle.Fill, Height = 28 };
        _txtOllamaUrl = new TextBox
        {
            Dock = DockStyle.Fill, Font = btnFont,
            PlaceholderText = "http://localhost:11434"
        };
        _txtOllamaUrl.Leave += async (_, _) => await AutoTestAsync();
        _rowOllamaUrl.Controls.Add(_txtOllamaUrl);
        AddRow(authTbl, "Ollama URL:", _rowOllamaUrl);

        grpAuth.Controls.Add(authTbl);
        root.Controls.Add(grpAuth);

        // ── 그룹 3: 모델 + 테스트 ────────────────────────
        var grpModel = new GroupBox
        {
            Text = "3. 모델",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 18, 12, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        var modelTbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true
        };
        modelTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelTbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        modelTbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        modelTbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        _cboModel = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Dock = DockStyle.Fill, Font = btnFont,
            Margin = new Padding(0, 0, 8, 0)
        };
        modelTbl.Controls.Add(_cboModel, 0, 0);

        _btnTestModels = new Button
        {
            Text = "🔄 연결 테스트 + 모델 새로고침",
            Dock = DockStyle.Fill, Height = 32, Font = btnFont
        };
        _btnTestModels.Click += async (_, _) => await AutoTestAsync(force: true);
        modelTbl.Controls.Add(_btnTestModels, 1, 0);

        _lblTestResult = new Label
        {
            Text = "(테스트 전)", Dock = DockStyle.Fill,
            ForeColor = System.Drawing.Color.Gray,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        modelTbl.Controls.Add(_lblTestResult, 0, 1);
        modelTbl.SetColumnSpan(_lblTestResult, 2);

        grpModel.Controls.Add(modelTbl);
        root.Controls.Add(grpModel);

        // ── 저장 ─────────────────────────────────────────
        var btnSaveCfg = new Button
        {
            Text = "💾 설정 저장",
            Width = 140, Height = 36, Font = btnFont,
            Margin = new Padding(0, 4, 0, 0)
        };
        btnSaveCfg.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            AppConfigService.Save(_cfg);
            SetStatus("설정 저장됨.");
        };
        root.Controls.Add(btnSaveCfg);

        // 힌트(미사용 그룹은 흐리게)
        _lblApiKeyHint = new Label();
        _lblOllamaHint = new Label();

        page.Controls.Add(root);
        return page;
    }

    private async void OnProviderChangedAsync(object? sender, EventArgs e)
    {
        var provider = _cboProvider.SelectedItem?.ToString() ?? "openai";
        UpdateProviderPanels();
        // 모델 칸을 현재 제공자의 저장값으로 미리 채움
        _cboModel.Items.Clear();
        _cboModel.Text = provider == "ollama" ? _cfg.OllamaModel : _cfg.Model;
        _lastTestedKey = null;
        SetTestResult("(테스트 전)", System.Drawing.Color.Gray);

        // Ollama는 키 불필요 → 즉시 자동 테스트 / 다른 제공자는 API 키 있으면 자동
        if (provider == "ollama")
            await AutoTestAsync(force: true);
        else if (!string.IsNullOrEmpty(_txtApiKey.Text))
            await AutoTestAsync(force: true);
    }

    private string? _lastTestedKey;
    private async Task AutoTestAsync(bool force = false)
    {
        SaveSettingsFromUi();
        var provider = _cfg.Provider;
        var key = provider == "ollama" ? _cfg.OllamaUrl : _cfg.ApiKey;
        if (!force && string.IsNullOrEmpty(key)) return;
        if (!force && key == _lastTestedKey) return;
        _lastTestedKey = key;

        SetTestResult("⏳ 연결 테스트 중...", System.Drawing.Color.DimGray);
        _btnTestModels.Enabled = false;
        try
        {
            var ai = new AiService();
            var models = await ai.ListModelsAsync(_cfg);
            if (models.Count == 0)
            {
                SetTestResult("연결됨. 그러나 모델 목록이 비어 있습니다.", System.Drawing.Color.DarkOrange);
                return;
            }
            var currentValue = _cboModel.Text;
            _cboModel.Items.Clear();
            foreach (var m in models) _cboModel.Items.Add(m);
            if (!string.IsNullOrEmpty(currentValue) && _cboModel.Items.Contains(currentValue))
                _cboModel.SelectedItem = currentValue;
            else if (_cboModel.Items.Count > 0)
                _cboModel.SelectedIndex = 0;

            SetTestResult($"✓ 연결 성공. 사용 가능 모델 {models.Count}개. 위 드롭다운에서 선택하세요.",
                System.Drawing.Color.SeaGreen);
        }
        catch (Exception ex)
        {
            SetTestResult($"✗ 연결 실패: {ShortMsg(ex.Message)}", System.Drawing.Color.Firebrick);
        }
        finally { _btnTestModels.Enabled = true; }
    }

    private static string ShortMsg(string s) => s.Length > 120 ? s.Substring(0, 120) + "…" : s;

    private void SetTestResult(string text, System.Drawing.Color color)
    {
        if (_lblTestResult.InvokeRequired) { _lblTestResult.Invoke(() => SetTestResult(text, color)); return; }
        _lblTestResult.Text = text;
        _lblTestResult.ForeColor = color;
    }

    private static void AddRow(TableLayoutPanel tbl, string label, Control ctrl)
    {
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        tbl.Controls.Add(new Label
        {
            Text = label, Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 8, 0)
        });
        tbl.Controls.Add(ctrl);
    }

    // ── Settings sync ─────────────────────────────────────────────────
    private void LoadSettingsToUi()
    {
        // 텍스트/URL 먼저 — SelectedIndexChanged 이벤트가 정확한 값을 읽도록
        _txtApiKey.Text     = _cfg.ApiKey;
        _txtOllamaUrl.Text  = _cfg.OllamaUrl;
        _txtUserInput.Text  = _cfg.LastInputText;

        _cboProvider.SelectedItem = _cfg.Provider;
        if (_cboProvider.SelectedIndex < 0) _cboProvider.SelectedIndex = 0;

        // SelectedIndexChanged가 cboModel을 비우므로 그 뒤에 저장값을 채워 넣음
        _cboModel.Text = _cfg.Provider == "ollama" ? _cfg.OllamaModel : _cfg.Model;
    }

    private void UpdateProviderPanels()
    {
        var isOllama = _cboProvider.SelectedItem?.ToString() == "ollama";
        _rowApiKey.Enabled = !isOllama;
        _txtApiKey.BackColor = isOllama ? System.Drawing.SystemColors.Control : System.Drawing.SystemColors.Window;
        _rowOllamaUrl.Enabled = isOllama;
        _txtOllamaUrl.BackColor = isOllama ? System.Drawing.SystemColors.Window : System.Drawing.SystemColors.Control;
    }

    private void SaveSettingsFromUi()
    {
        _cfg.Provider  = _cboProvider.SelectedItem?.ToString() ?? "openai";
        _cfg.ApiKey    = _txtApiKey.Text.Trim();
        _cfg.OllamaUrl = string.IsNullOrWhiteSpace(_txtOllamaUrl.Text) ? "http://localhost:11434" : _txtOllamaUrl.Text.Trim();
        var modelText  = _cboModel.Text.Trim();
        if (_cfg.Provider == "ollama") _cfg.OllamaModel = modelText;
        else                            _cfg.Model      = modelText;
    }

    // ── Workflow ──────────────────────────────────────────────────────
    private void OnLoadForm(object? s, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "HWPX 양식 (*.hwpx)|*.hwpx|모든 파일 (*.*)|*.*",
            Title  = "공문 양식 불러오기"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            _template = HwpxTemplate.Load(dlg.FileName);
            _lblForm.Text = $"📄 {Path.GetFileName(dlg.FileName)}  (단락 {_template.Paragraphs.Count}, 표 {_template.Tables.Count})";
            _lblForm.ForeColor = System.Drawing.Color.Black;
            SetStatus($"양식 로드됨 (단락 {_template.Paragraphs.Count}, 표 {_template.Tables.Count}). 한글에 띄우는 중...");

            // 한글이 미연결이면 자동 연결 후 열기
            if (!_hwp.IsConnected) TryConnectHwp();
            OpenInHwp(dlg.FileName, "원본 양식");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"양식 로드 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnLoadTxt(object? s, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "텍스트 (*.txt)|*.txt|모든 파일 (*.*)|*.*",
            Title  = "사용자 텍스트 불러오기"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            _txtUserInput.Text = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
            SetStatus($"텍스트 로드됨: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"파일 읽기 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnApply(object? s, EventArgs e)
    {
        if (_template == null) { MessageBox.Show("먼저 양식(.hwpx)을 불러오세요."); return; }
        var userText = _txtUserInput.Text.Trim();
        if (string.IsNullOrEmpty(userText)) { MessageBox.Show("사용자 텍스트를 입력하세요."); return; }

        SaveSettingsFromUi();
        SetButtonsEnabled(false);
        StartProgress();

        try
        {
            var ai = new AiService();
            var progress = new Progress<string>(SetStatus);

            SetProgressStage(5, $"양식 분석 중 (단락 {_template.Paragraphs.Count}, 표 {_template.Tables.Count})...");

            // 원본 스냅샷에서 다시 시작 (반복 적용 가능)
            _template = HwpxTemplate.Load(_template.SourcePath);

            SetProgressStage(15, "AI 호출 시작...");
            var result = await ai.RewriteFormAsync(
                _template.Paragraphs, _template.Tables, userText, _cfg, progress);

            var rewrites = result.Paragraphs;
            var tableUpdates = result.Tables;

            if (rewrites.Count == 0 && tableUpdates.Count == 0)
            {
                var preview = result.RawResponse.Length > 800
                    ? result.RawResponse.Substring(0, 800) + "\n...(이하 생략)"
                    : result.RawResponse;
                MessageBox.Show(
                    "AI가 수정할 항목을 찾지 못했습니다 (JSON 파싱 실패).\n\n" +
                    "── AI 원본 응답 ──\n" + preview + "\n\n" +
                    "응답이 비었거나 형식이 다르면 다른 모델을 선택하거나 " +
                    "사용자 텍스트를 더 구체적으로 작성해 보세요.",
                    "결과 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("AI 응답 파싱 실패 — 원본 응답을 확인하세요.");
                return;
            }

            SetProgressStage(85, $"단락 {rewrites.Count}개 치환 중...");
            _template.ApplyRewrites(rewrites);

            if (tableUpdates.Count > 0)
            {
                SetProgressStage(90, $"표 {tableUpdates.Count}개 행 조정 중...");
                _template.ApplyTableUpdates(tableUpdates);
            }

            SetProgressStage(95, "HWPX 저장 중...");
            var temp = Path.Combine(Path.GetTempPath(),
                $"공문_{DateTime.Now:yyyyMMdd_HHmmss_fff}.hwpx");
            _template.SaveAs(temp);
            _currentTempPath = temp;

            SetProgressStage(98, "한글에 결과 표시 중...");
            OpenInHwp(temp, "AI 적용 결과");

            SetProgressStage(100,
                $"AI 적용 완료. 단락 {rewrites.Count}개, 표 {tableUpdates.Count}개 수정. 한글에서 확인하세요.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"AI 처리 오류:\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("AI 적용 실패.");
        }
        finally
        {
            StopProgress();
            SetButtonsEnabled(true);
        }
    }

    // ── 진행률 표시 ──────────────────────────────────────────────────
    private int _progressStageFloor = 0;
    private int _progressStageCap   = 90;

    private void StartProgress()
    {
        _aiStartUtc = DateTime.UtcNow;
        _progressStageFloor = 0;
        _progressStageCap = 15;
        _progress.Value = 0;
        _progress.Visible = true;
        _progressTimer.Start();
    }

    private void StopProgress()
    {
        _progressTimer.Stop();
        _progress.Value = 100;
        var hideAt = DateTime.Now.AddSeconds(1.2);
        Task.Run(async () =>
        {
            while (DateTime.Now < hideAt) await Task.Delay(100);
            try { Invoke(() => { _progress.Visible = false; _progress.Value = 0; }); }
            catch { }
        });
    }

    private void SetProgressStage(int targetPct, string status)
    {
        _progressStageFloor = Math.Max(_progressStageFloor, _progress.Value);
        _progressStageCap = Math.Min(100, targetPct);
        _progress.Value = Math.Min(_progressStageCap, Math.Max(_progress.Value, targetPct - 5));
        SetStatus(status);
    }

    private void OnProgressTick(object? s, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _aiStartUtc).TotalSeconds;
        // 시작점에서 cap까지 elapsed/Estimated 비율로 천천히 증가 (단조 증가)
        var ratio = Math.Min(1.0, elapsed / EstimatedAiSeconds);
        var dyn = _progressStageFloor + (int)((_progressStageCap - _progressStageFloor) * ratio);
        if (dyn > _progress.Value) _progress.Value = Math.Min(_progressStageCap, dyn);
        // 상태 메시지에 % + 경과시간 부가
        var baseMsg = _lblStatus.Text;
        var cleanBase = baseMsg.Contains(" — ") ? baseMsg.Substring(0, baseMsg.IndexOf(" — ")) : baseMsg;
        _lblStatus.Text = $"{cleanBase} — {_progress.Value}% ({elapsed:F1}초)";
    }

    private void OnSaveAs(object? s, EventArgs e)
    {
        if (_currentTempPath == null || !File.Exists(_currentTempPath))
        {
            MessageBox.Show("저장할 결과가 없습니다. 먼저 [AI 적용]을 실행하세요.");
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter   = "HWPX (*.hwpx)|*.hwpx",
            FileName = $"공문_{DateTime.Now:yyyyMMdd_HHmm}.hwpx",
            Title    = "공문 저장"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            File.Copy(_currentTempPath, dlg.FileName, overwrite: true);
            SetStatus($"저장 완료: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"저장 오류:\n{ex.Message}\n\n한글에서 동일 파일을 닫고 다시 시도하세요.",
                "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenInHwp(string path, string what)
    {
        if (!_hwp.IsConnected) TryConnectHwp();
        if (!_hwp.IsConnected)
        {
            SetStatus($"한글 미연결 → 기본 프로그램으로 {what} 열기.");
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show($"열기 오류: {ex.Message}"); }
            return;
        }
        if (_hwp.OpenFile(path))
            SetStatus($"한글에 {what} 표시: {Path.GetFileName(path)}");
        else
            SetStatus($"한글 열기 실패: {_hwp.LastError ?? "원인 불명"}");
    }

    // ── HWP connection ────────────────────────────────────────────────
    private void TryConnectHwp()
    {
        if (_hwp.Connect())
        {
            _lblHwpStatus.Text = "한글: 연결됨 ✓";
            _lblHwpStatus.ForeColor = System.Drawing.Color.SeaGreen;
            SetStatus("한글 COM 연결 성공.");
        }
        else
        {
            _lblHwpStatus.Text = "한글: 미연결";
            _lblHwpStatus.ForeColor = System.Drawing.Color.Firebrick;
            var err = _hwp.LastError ?? "원인 불명";
            SetStatus($"한글 연결 실패: {err}");
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnApply.Enabled    = enabled;
        _btnSaveAs.Enabled   = enabled;
        _btnLoadForm.Enabled = enabled;
        _btnLoadTxt.Enabled  = enabled;
    }

    private void SetStatus(string msg)
    {
        if (InvokeRequired) { Invoke(() => SetStatus(msg)); return; }
        _lblStatus.Text = msg;
    }

    private void OnFormClosing(object? s, FormClosingEventArgs e)
    {
        _cfg.LastInputText = _txtUserInput.Text;
        _cfg.WindowX = Location.X;
        _cfg.WindowY = Location.Y;
        SaveSettingsFromUi();
        AppConfigService.Save(_cfg);
        _hwp.Dispose();
    }
}
