using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace EldenRingDeathCounter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CounterOverlayForm());
    }
}

internal sealed class CounterOverlayForm : Form
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad
    {
        public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY;
    }

    private const int UndoHoldMilliseconds = 700;
    private const int MaximumHistory = 5;
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001, XINPUT_GAMEPAD_DPAD_DOWN = 0x0002,
        XINPUT_GAMEPAD_DPAD_LEFT = 0x0004, XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008,
        XINPUT_GAMEPAD_START = 0x0010, XINPUT_GAMEPAD_BACK = 0x0020,
        XINPUT_GAMEPAD_LEFT_THUMB = 0x0040, XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080,
        XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100, XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200,
        XINPUT_GAMEPAD_A = 0x1000, XINPUT_GAMEPAD_B = 0x2000,
        XINPUT_GAMEPAD_X = 0x4000, XINPUT_GAMEPAD_Y = 0x8000;

    private readonly Label totalLabel = new();
    private readonly Label bossLabel = new();
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 20 };
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldenRingDeathCounter", "counters.json");
    private int totalDeaths, bossDeaths;
    private string deathBinding = "K:118"; // F7
    private string resetBinding = "K:119"; // F8
    private HashSet<string> previousInputs = new();
    private bool chordLatched, chordTriggered;
    private DateTime? chordStarted;
    private Point dragStart;
    private bool dragging;
    private ControlPanelForm? controlPanel;
    private string? captureTarget;

    private sealed class PersistedData
    {
        public int Total { get; set; }
        public int Boss { get; set; }
        public string DeathBinding { get; set; } = "K:118";
        public string ResetBinding { get; set; } = "K:119";
        public List<UndoEntry> History { get; set; } = new();
    }
    internal sealed class UndoEntry
    {
        public string Description { get; set; } = "";
        public int PreviousTotal { get; set; }
        public int PreviousBoss { get; set; }
        public DateTime When { get; set; }
    }

    public CounterOverlayForm()
    {
        Text = "Elden Ring — Contador de mortes";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Location = new Point(32, 80);
        BackColor = Color.FromArgb(22, 22, 26); Opacity = 0.93;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = new Padding(0), Padding = new Padding(0) };
        totalLabel.AutoSize = true; totalLabel.Font = new Font("Segoe UI", 14, FontStyle.Bold); totalLabel.ForeColor = Color.FromArgb(234, 198, 125); totalLabel.Margin = new Padding(0, 0, 0, 3);
        bossLabel.AutoSize = true; bossLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold); bossLabel.ForeColor = Color.White; bossLabel.Margin = new Padding(0);
        layout.Controls.Add(totalLabel, 0, 0); layout.Controls.Add(bossLabel, 0, 1); Controls.Add(layout);
        LoadData(); RefreshDisplay(); BindDrag(this); BindDrag(totalLabel); BindDrag(bossLabel);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel de controle", null, (_, _) => OpenControlPanel());
        menu.Items.Add("Registrar morte", null, (_, _) => AddDeath());
        menu.Items.Add("Zerar mortes do boss", null, (_, _) => ResetBoss());
        menu.Items.Add("Desfazer última alteração", null, (_, _) => UndoLast());
        menu.Items.Add("Sair", null, (_, _) => Close());
        ContextMenuStrip = menu; totalLabel.ContextMenuStrip = menu; bossLabel.ContextMenuStrip = menu;
        tray.Icon = SystemIcons.Application; tray.Text = "Elden Ring Death Counter"; tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += (_, _) => OpenControlPanel();
        inputTimer.Tick += (_, _) => PollInputs(); inputTimer.Start();
        FormClosing += (_, _) => { inputTimer.Stop(); tray.Visible = false; tray.Dispose(); SaveData(); };
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static string KeyBinding(int vk) => $"K:{vk}";
    private static string PadBinding(int controller, string button) => $"P{controller}:{button}";

    private HashSet<string> ReadCurrentInputs()
    {
        var down = new HashSet<string>();
        // Keyboard keys; exclude mouse buttons and mouse wheel pseudo-keys.
        for (int vk = 8; vk <= 255; vk++)
        {
            if (vk is >= 1 and <= 6) continue;
            if (IsKeyDown(vk)) down.Add(KeyBinding(vk));
        }
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState((uint)i, out var state) != 0) continue;
                ushort b = state.Gamepad.Buttons;
                void Add(ushort mask, string name) { if ((b & mask) != 0) down.Add(PadBinding(i + 1, name)); }
                Add(XINPUT_GAMEPAD_A, "A"); Add(XINPUT_GAMEPAD_B, "B"); Add(XINPUT_GAMEPAD_X, "X"); Add(XINPUT_GAMEPAD_Y, "Y");
                Add(XINPUT_GAMEPAD_DPAD_UP, "DPadUp"); Add(XINPUT_GAMEPAD_DPAD_DOWN, "DPadDown"); Add(XINPUT_GAMEPAD_DPAD_LEFT, "DPadLeft"); Add(XINPUT_GAMEPAD_DPAD_RIGHT, "DPadRight");
                Add(XINPUT_GAMEPAD_START, "Start"); Add(XINPUT_GAMEPAD_BACK, "Back"); Add(XINPUT_GAMEPAD_LEFT_THUMB, "LeftStick"); Add(XINPUT_GAMEPAD_RIGHT_THUMB, "RightStick");
                Add(XINPUT_GAMEPAD_LEFT_SHOULDER, "LB"); Add(XINPUT_GAMEPAD_RIGHT_SHOULDER, "RB");
                if (state.Gamepad.LeftTrigger > 120) down.Add(PadBinding(i + 1, "LT"));
                if (state.Gamepad.RightTrigger > 120) down.Add(PadBinding(i + 1, "RT"));
            }
            catch (DllNotFoundException) { break; }
            catch (EntryPointNotFoundException) { break; }
        }
        return down;
    }

    private void PollInputs()
    {
        var current = ReadCurrentInputs();
        if (captureTarget != null)
        {
            string? newlyPressed = current.FirstOrDefault(x => !previousInputs.Contains(x));
            if (newlyPressed != null)
            {
                string otherBinding = captureTarget == "death" ? resetBinding : deathBinding;
                if (newlyPressed == otherBinding)
                {
                    if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Esse botão já está atribuído à outra ação. Aperte um botão diferente.");
                }
                else
                {
                    if (captureTarget == "death") deathBinding = newlyPressed; else resetBinding = newlyPressed;
                    captureTarget = null; SaveData(); RefreshAll();
                    if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus($"Botão registrado: {FormatBinding(newlyPressed)}");
                }
            }
            previousInputs = current; return;
        }

        bool deathDown = current.Contains(deathBinding);
        bool resetDown = current.Contains(resetBinding);
        DateTime now = DateTime.Now;
        if (deathDown && resetDown)
        {
            if (!chordLatched) { chordLatched = true; chordStarted = now; chordTriggered = false; }
            if (!chordTriggered && chordStarted.HasValue && (now - chordStarted.Value).TotalMilliseconds >= UndoHoldMilliseconds)
            { UndoLast(); chordTriggered = true; }
        }
        else
        {
            if (!chordLatched)
            {
                if (previousInputs.Contains(deathBinding) && !deathDown) AddDeath();
                if (previousInputs.Contains(resetBinding) && !resetDown) ResetBoss();
            }
            if (!deathDown && !resetDown) { chordLatched = false; chordStarted = null; chordTriggered = false; }
        }
        previousInputs = current;
    }

    internal static string FormatBinding(string binding)
    {
        if (binding.StartsWith("K:") && int.TryParse(binding[2..], out int vk))
        {
            string name = Enum.IsDefined(typeof(Keys), vk) ? ((Keys)vk).ToString() : $"Tecla {vk}";
            return $"Teclado: {name}";
        }
        if (binding.StartsWith("P"))
        {
            int colon = binding.IndexOf(':');
            if (colon > 1) return $"Controle {binding[1..colon]}: {binding[(colon + 1)..]}";
        }
        return binding;
    }
    internal string DeathBindingDisplay => FormatBinding(deathBinding);
    internal string ResetBindingDisplay => FormatBinding(resetBinding);
    internal void BeginCapture(string target) { captureTarget = target; if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Aguardando botão/tecla... aperte o botão desejado."); }

    private void AddDeath() => ApplyCommand($"Morte registrada ({FormatBinding(deathBinding)})", () => { totalDeaths++; bossDeaths++; });
    private void ResetBoss() => ApplyCommand($"Boss zerado ({FormatBinding(resetBinding)})", () => bossDeaths = 0);
    private void SetCounts(int total, int boss) => ApplyCommand($"Contadores editados: total {totalDeaths} → {total}; boss {bossDeaths} → {boss}", () => { totalDeaths = Math.Max(0, total); bossDeaths = Math.Max(0, boss); });
    private void ApplyCommand(string description, Action action)
    {
        int oldTotal = totalDeaths, oldBoss = bossDeaths; action();
        if (oldTotal == totalDeaths && oldBoss == bossDeaths) { RefreshAll(); return; }
        History.Insert(0, new UndoEntry { Description = description, PreviousTotal = oldTotal, PreviousBoss = oldBoss, When = DateTime.Now });
        if (History.Count > MaximumHistory) History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);
        SaveData(); RefreshAll();
    }
    private readonly List<UndoEntry> History = new();
    private void UndoLast()
    {
        if (History.Count == 0) { tray.BalloonTipTitle = "Elden Ring Death Counter"; tray.BalloonTipText = "Não há alterações no histórico para desfazer."; tray.ShowBalloonTip(1500); return; }
        var entry = History[0]; History.RemoveAt(0); totalDeaths = Math.Max(0, entry.PreviousTotal); bossDeaths = Math.Max(0, entry.PreviousBoss);
        SaveData(); RefreshAll(); if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus($"Desfeito: {entry.Description}");
    }
    private void OpenControlPanel()
    {
        if (controlPanel == null || controlPanel.IsDisposed) controlPanel = new ControlPanelForm(this);
        controlPanel.ReloadFromOwner(); controlPanel.Show(); controlPanel.BringToFront(); controlPanel.Activate();
    }
    private void RefreshDisplay() { totalLabel.Text = $"MORTES TOTAIS: {totalDeaths}"; bossLabel.Text = $"MORTES NO BOSS: {bossDeaths}"; }
    private void RefreshAll() { RefreshDisplay(); if (controlPanel is { IsDisposed: false }) controlPanel.RefreshFromOwner(); }
    internal int TotalDeaths => totalDeaths;
    internal int BossDeaths => bossDeaths;
    internal IReadOnlyList<UndoEntry> UndoHistory => History.AsReadOnly();
    internal void EditCounts(int total, int boss) => SetCounts(total, boss);
    internal void UndoFromPanel() => UndoLast();

    private void LoadData()
    {
        try
        {
            if (!File.Exists(savePath)) return;
            var saved = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(savePath)); if (saved == null) return;
            totalDeaths = Math.Max(0, saved.Total); bossDeaths = Math.Max(0, saved.Boss);
            if (!string.IsNullOrWhiteSpace(saved.DeathBinding)) deathBinding = saved.DeathBinding;
            if (!string.IsNullOrWhiteSpace(saved.ResetBinding)) resetBinding = saved.ResetBinding;
            History.Clear(); History.AddRange(saved.History ?? new List<UndoEntry>());
            if (History.Count > MaximumHistory) History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);
        }
        catch { totalDeaths = 0; bossDeaths = 0; History.Clear(); }
    }
    private void SaveData()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            File.WriteAllText(savePath, JsonSerializer.Serialize(new PersistedData { Total = totalDeaths, Boss = bossDeaths, DeathBinding = deathBinding, ResetBinding = resetBinding, History = History }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { tray.BalloonTipTitle = "Falha ao salvar contador"; tray.BalloonTipText = ex.Message; tray.ShowBalloonTip(1500); }
    }
    private void BindDrag(Control control)
    {
        control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        control.MouseMove += (_, e) => { if (dragging && e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); };
        control.MouseUp += (_, _) => dragging = false;
    }
}

internal sealed class ControlPanelForm : Form
{
    private readonly CounterOverlayForm owner;
    private readonly NumericUpDown totalInput = new(), bossInput = new();
    private readonly ListBox historyList = new();
    private readonly Label statusLabel = new(), deathBindingLabel = new(), resetBindingLabel = new();
    private readonly Button undoButton = new();

    public ControlPanelForm(CounterOverlayForm owner)
    {
        this.owner = owner; Text = "Elden Ring — Painel de controle"; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true; ClientSize = new Size(620, 560); Font = new Font("Segoe UI", 9.5f);
        Controls.Add(new Label { Text = "CONTADORES DE MORTES", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(18, 14) });
        AddCaption("Mortes totais", 20, 55); AddCaption("Mortes no boss atual", 320, 55); ConfigureNumber(totalInput, 20, 78); ConfigureNumber(bossInput, 320, 78); Controls.Add(totalInput); Controls.Add(bossInput);
        var apply = new Button { Text = "Aplicar valores", Location = new Point(20, 116), Size = new Size(140, 30) };
        apply.Click += (_, _) => { owner.EditCounts((int)totalInput.Value, (int)bossInput.Value); SetStatus("Valores atualizados; a alteração entrou no histórico."); }; Controls.Add(apply);
        var reload = new Button { Text = "Recarregar valores", Location = new Point(170, 116), Size = new Size(150, 30) }; reload.Click += (_, _) => ReloadFromOwner(); Controls.Add(reload);

        Controls.Add(new Label { Text = "ATALHOS — clique em Definir e aperte uma tecla ou botão do controle", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 160) });
        deathBindingLabel.Location = new Point(22, 190); deathBindingLabel.Size = new Size(235, 25); Controls.Add(deathBindingLabel);
        var deathCapture = new Button { Text = "Definir botão de morte", Location = new Point(260, 186), Size = new Size(170, 30) }; deathCapture.Click += (_, _) => owner.BeginCapture("death"); Controls.Add(deathCapture);
        resetBindingLabel.Location = new Point(22, 226); resetBindingLabel.Size = new Size(235, 25); Controls.Add(resetBindingLabel);
        var resetCapture = new Button { Text = "Definir botão de zerar boss", Location = new Point(260, 222), Size = new Size(200, 30) }; resetCapture.Click += (_, _) => owner.BeginCapture("reset"); Controls.Add(resetCapture);

        Controls.Add(new Label { Text = "HISTÓRICO — últimas 5 alterações (data e horário)", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 270) });
        historyList.Location = new Point(20, 296); historyList.Size = new Size(580, 155); historyList.HorizontalScrollbar = true; Controls.Add(historyList);
        undoButton.Text = "Desfazer última alteração"; undoButton.Location = new Point(20, 464); undoButton.Size = new Size(190, 32); undoButton.Click += (_, _) => owner.UndoFromPanel(); Controls.Add(undoButton);
        var close = new Button { Text = "Fechar painel", Location = new Point(460, 464), Size = new Size(140, 32) }; close.Click += (_, _) => Hide(); Controls.Add(close);
        statusLabel.Location = new Point(20, 510); statusLabel.Size = new Size(580, 36); statusLabel.ForeColor = Color.DimGray; Controls.Add(statusLabel);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        ReloadFromOwner();
    }
    private void AddCaption(string text, int x, int y) => Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) });
    private static void ConfigureNumber(NumericUpDown c, int x, int y) { c.Location = new Point(x, y); c.Size = new Size(260, 28); c.Minimum = 0; c.Maximum = 999999; c.ThousandsSeparator = true; c.Font = new Font("Segoe UI", 12); }
    public void ReloadFromOwner() { totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum); bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum); RefreshFromOwner(); }
    public void RefreshFromOwner()
    {
        if (!totalInput.Focused) totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum);
        if (!bossInput.Focused) bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum);
        deathBindingLabel.Text = "Morte: " + owner.DeathBindingDisplay; resetBindingLabel.Text = "Zerar boss: " + owner.ResetBindingDisplay;
        historyList.BeginUpdate(); historyList.Items.Clear();
        foreach (var e in owner.UndoHistory) historyList.Items.Add($"{e.When:dd/MM/yyyy HH:mm:ss} — {e.Description}");
        historyList.EndUpdate(); undoButton.Enabled = owner.UndoHistory.Count > 0;
    }
    public void SetStatus(string message) => statusLabel.Text = message;
}
