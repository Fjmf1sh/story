// StorySteamAI - single-file WinForms app with Steam lobbies + Gemini AI DM.
// Build: .NET 8 WinForms. Requires Steamworks.NET NuGet and steam_api64.dll beside EXE.
// Transport: Steam Lobby + Lobby Chat (no separate server). Saves: JSON slots.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Security.Cryptography;
using Steamworks;
using WinFormsTimer = System.Windows.Forms.Timer;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppDomain.CurrentDomain.ProcessExit += (_, __) => SteamBridge.SafeShutdown();
        Application.ApplicationExit += (_, __) => SteamBridge.SafeShutdown();
        Application.Run(new MainForm());
    }
}

// =========================
// Constants / Models
// =========================
static class GameConst
{
    public const uint AppId = 480; // Spacewar for dev

    public const string GameKey = "STORY_STEAM_AI";
    public const int MaxParty = 4;
    public static readonly string SaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StorySteamAI_Saves");
    public static readonly string[] Slots = { "SlotA", "SlotB", "SlotC", "SlotD", "SlotE" };

    public const string DefaultModel = "gemini-1.5-flash";
    public static readonly Uri GeminiEndpoint =
        new Uri("https://generativelanguage.googleapis.com/v1beta/models/" + DefaultModel + ":generateContent");
}

class PlayerState
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "Player";
    public bool IsHost { get; set; }
    public bool IsAI { get; set; }
    public List<string> Inventory { get; set; } = new();
}

class SaveData
{
    public string SaveId { get; set; } = "";
    public string WorldSeed { get; set; } = "";
    public string StorySoFar { get; set; } = "You awaken at a crossroads under a violet sky.";
    public Dictionary<ulong, PlayerState> Players { get; set; } = new();
    public DateTime LastSavedUtc { get; set; } = DateTime.UtcNow;
}

enum NetMsgKind { Chat, Command, StateSync }

class NetMsg
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NetMsgKind Kind { get; set; }
    public ulong From { get; set; }
    public string Payload { get; set; } = "";
    public string SaveId { get; set; } = "";
}

// =========================
// Steam Lobby & Messaging
// =========================
static class SteamBridge
{
    static bool _inited;
    static Callback<LobbyCreated_t>? _cbLobbyCreated;
    static Callback<LobbyEnter_t>? _cbLobbyEnter;
    static Callback<LobbyChatMsg_t>? _cbLobbyChat;
    static Callback<GameLobbyJoinRequested_t>? _cbJoinRequested;
    static CallResult<LobbyMatchList_t>? _lobbyMatchList; // keep alive

    public static bool IsHost { get; private set; }
    public static CSteamID LobbyId { get; private set; }
    public static CSteamID Self { get; private set; }
    public static string PersonaName => SteamFriends.GetPersonaName();
    public static event Action? OnLobbyJoined;
    public static event Action<string>? OnLog;
    public static event Action<NetMsg>? OnNet;

    static WinFormsTimer? _pump;

    public static bool SafeInit()
    {
        if (_inited) return true;
        try
        {
            if (!SteamAPI.Init()) return false;
            _inited = true;
            Self = SteamUser.GetSteamID();
            _cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _cbLobbyEnter   = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _cbLobbyChat    = Callback<LobbyChatMsg_t>.Create(OnLobbyChat);
            _cbJoinRequested= Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _pump = new WinFormsTimer { Interval = 16 };
            _pump.Tick += (_, __) => SteamAPI.RunCallbacks();
            _pump.Start();
            Log($"Steam init OK. You are {PersonaName} ({Self.m_SteamID}).");
            return true;
        }
        catch (Exception ex) { Log("Steam init failed: " + ex.Message); return false; }
    }

    public static void SafeShutdown()
    {
        if (!_inited) return;
        try { _pump?.Stop(); } catch { }
        try { if (LobbyId.IsValid()) SteamMatchmaking.LeaveLobby(LobbyId); } catch { }
        try { SteamAPI.Shutdown(); } catch { }
        _inited = false;
    }

    public static void CreateLobby()
    {
        IsHost = true;
        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, GameConst.MaxParty);
    }

    public static void JoinLobby(ulong lobbyId)
    {
        IsHost = false;
        SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
    }

    public static void BrowseLobbies(Action<List<CSteamID>> cb)
    {
        SteamMatchmaking.AddRequestLobbyListStringFilter("GAME_KEY", GameConst.GameKey, ELobbyComparison.k_ELobbyComparisonEqual);
        var call = SteamMatchmaking.RequestLobbyList();
        _lobbyMatchList ??= CallResult<LobbyMatchList_t>.Create((res, fail) =>
        {
            var result = new List<CSteamID>();
            if (!fail)
            {
                for (int i = 0; i < res.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (SteamMatchmaking.GetLobbyData(id, "GAME_KEY") == GameConst.GameKey)
                        result.Add(id);
                }
            }
            cb(result);
        });
        _lobbyMatchList.Set(call);
    }

    public static void Send(NetMsg m)
    {
        if (!LobbyId.IsValid()) return;
        string json = JsonSerializer.Serialize(m);
        byte[] data = Encoding.UTF8.GetBytes(json);
        SteamMatchmaking.SendLobbyChatMsg(LobbyId, data, data.Length);
    }

    static void OnLobbyCreated(LobbyCreated_t e)
    {
        if (e.m_eResult != EResult.k_EResultOK) { Log("Lobby create failed: " + e.m_eResult); return; }
        LobbyId = new CSteamID(e.m_ulSteamIDLobby);
        SteamMatchmaking.SetLobbyData(LobbyId, "GAME_KEY", GameConst.GameKey);
        SteamMatchmaking.SetLobbyData(LobbyId, "NAME", $"{PersonaName}'s Story");
        SteamMatchmaking.SetLobbyData(LobbyId, "MAX", GameConst.MaxParty.ToString());
        Log($"Lobby created: {LobbyId.m_SteamID}");
    }

    static void OnLobbyEnter(LobbyEnter_t e)
    {
        LobbyId = new CSteamID(e.m_ulSteamIDLobby);
        Log($"Joined lobby {LobbyId.m_SteamID}. Members: {SteamMatchmaking.GetNumLobbyMembers(LobbyId)}");
        OnLobbyJoined?.Invoke();
    }

    static void OnLobbyChat(LobbyChatMsg_t e)
    {
        var buf = new byte[4096];
        CSteamID user; EChatEntryType type;
        int got = SteamMatchmaking.GetLobbyChatEntry(new CSteamID(e.m_ulSteamIDLobby), (int)e.m_iChatID, out user, buf, buf.Length, out type);
        if (got <= 0) return;
        var json = Encoding.UTF8.GetString(buf, 0, got);
        try { var msg = JsonSerializer.Deserialize<NetMsg>(json); if (msg != null) OnNet?.Invoke(msg); }
        catch { /* ignore */ }
    }

    static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t e) => SteamMatchmaking.JoinLobby(e.m_steamIDLobby);
    static void Log(string s) { OnLog?.Invoke(s); }
}

// =========================
// Gemini Client (very small)
// =========================
static class Gemini
{
    static readonly HttpClient _http = new HttpClient();
    static string? _apiKey;
    public static string ApiKey
    {
        get => _apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        set => _apiKey = value;
    }

    public static async Task<string> GenerateAsync(string system, string storySoFar, string turn, List<string> partyLines)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return "[AI not configured. Set Gemini API key.]";

        var sb = new StringBuilder();
        sb.AppendLine(system);
        sb.AppendLine("\n[CURRENT STORY]");
        sb.AppendLine(storySoFar);
        if (partyLines.Count > 0)
        {
            sb.AppendLine("\n[PARTY ACTIONS THIS TURN]");
            foreach (var l in partyLines) sb.AppendLine("- " + l);
        }
        sb.AppendLine("\n[GAME MASTER TASK]");
        sb.AppendLine(turn);

        var body = new
        {
            contents = new[] { new { parts = new[] { new { text = sb.ToString() } } } },
            generationConfig = new { temperature = 0.6, maxOutputTokens = 400 }
        };

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{GameConst.GeminiEndpoint}?key={ApiKey}")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var outText = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            return outText.Trim();
        }
        catch { return "[AI error] " + text; }
    }
}

// Secure(ish) local storage for the API key (per-user via DPAPI)
static class ApiKeyStore
{
    static string FilePath => Path.Combine(GameConst.SaveDir, "gemini.key");
    public static void Save(string key)
    {
        try
        {
            Directory.CreateDirectory(GameConst.SaveDir);
            var bytes = Encoding.UTF8.GetBytes(key);
            var prot = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, prot);
        } catch { }
    }
    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            var prot = File.ReadAllBytes(FilePath);
            var bytes = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        } catch { return ""; }
    }
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}

// =========================
class GameCore
{
    async Task PrologueAsync()
    {
        try
        {
            // Build a list of party names for flavor
            var party = string.Join(", ", Data.Players.Values.Select(p => p.Name));
            var intro = await Gemini.GenerateAsync(
                system: "You are an AI GAME MASTER. Start the adventure with a vivid, cinematic opening. "
                        + "Write 4–6 short sentences that set the scene, tone, and immediate stakes. "
                        + "Reference the party if helpful. Keep it tight and game-like. "
                        + "Finish with a line that begins with NEXT: and 1–2 clear choices (no questions).",
                storySoFar: $"Seed:{Data.WorldSeed}\nPlayers:{party}\n{Data.StorySoFar}",
                turn: "Introduce the world so players know where they are and what they can do next.",
                partyLines: new List<string>());

            intro = (intro ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(intro))
            {
                Data.StorySoFar = intro;
                Append("[GM] " + intro.Split(new[] { "\n\n" }, StringSplitOptions.None)[0].Trim());
                OnUiRefresh?.Invoke();
                SyncState();
            }
        }
        catch (Exception ex)
        {
            Append("[INFO] Prologue error: " + ex.Message);
        }
    }

    readonly HashSet<string> _processed = new();
    bool ShouldProcess(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return true;
        return _processed.Add(id);
    }

    public void AddBots(int count)
    {
        int existingAI = Data.Players.Values.Count(p => p.IsAI);
        var rng = new Random();
        for (int i = 0; i < count; i++)
        {
            ulong botId = (ulong)rng.Next(100_000_000, int.MaxValue);
            Data.Players[botId] = new PlayerState
            {
                SteamId = botId,
                Name = "AI-" + (existingAI + i + 1),
                IsAI = true,
                Inventory = new() { "Rations" }
            };
        }
        OnUiRefresh?.Invoke();
        SyncState();
        Append($"[SYSTEM] Added {count} AI companion(s).");
    }

    public SaveData Data { get; private set; } = new();
    public bool IsHost => SteamBridge.IsHost;
    public ulong SelfId => SteamBridge.Self.m_SteamID;
    public event Action<string>? OnAppend;
    public event Action? OnUiRefresh;

    public GameCore() => Directory.CreateDirectory(GameConst.SaveDir);

    public void NewGame(string saveId, bool asHost, bool addAIBots, int botCount)
    {
        Data = new SaveData
        {
            SaveId = saveId,
            WorldSeed = Guid.NewGuid().ToString("N"),
            StorySoFar = "You awaken at a crossroads under a violet sky."
        };
        Data.Players[SelfId] = new PlayerState
        {
            SteamId = SelfId, Name = SteamBridge.PersonaName, IsHost = asHost, IsAI = false,
            Inventory = new List<string> { "Torch" }
        };
        if (asHost && addAIBots)
        {
            var rng = new Random();
            for (int i = 0; i < botCount; i++)
            {
                ulong botId = (ulong)rng.Next(100_000_000, int.MaxValue);
                Data.Players[botId] = new PlayerState
                {
                    SteamId = botId, Name = "AI-" + (i + 1), IsAI = true, Inventory = new() { "Rations" }
                };
            }
        }
        Append($"[SYSTEM] New world created. Save: {saveId}");
        SyncState();
        OnUiRefresh?.Invoke();
        if (asHost) _ = PrologueAsync();
    }

    public void LoadGame(string saveId)
    {
        string path = SlotPath(saveId);
        if (File.Exists(path))
        {
            Data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path)) ?? new SaveData();
            Append($"[SYSTEM] Loaded save {saveId}");
            SyncState();
            OnUiRefresh?.Invoke();
        }
        else Append($"[SYSTEM] No save found in {saveId}");
    }

    public void SaveGame()
    {
        Data.LastSavedUtc = DateTime.UtcNow;
        File.WriteAllText(SlotPath(Data.SaveId), JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        Append($"[SYSTEM] Saved to {Data.SaveId}");
    }

    string SlotPath(string slot) => Path.Combine(GameConst.SaveDir, $"{slot}.json");
    void Append(string s) => OnAppend?.Invoke(s);
    public List<PlayerState> Roster() => Data.Players.Values.OrderBy(p => p.IsAI).ToList();

    public void HandleIncoming(NetMsg msg)
    {
        switch (msg.Kind)
        {
            case NetMsgKind.Command:
                if (IsHost) _ = ResolveTurnAsync(msg);
                break;
            case NetMsgKind.Chat:
                Append(msg.Payload);
                break;
            case NetMsgKind.StateSync:
                var newData = JsonSerializer.Deserialize<SaveData>(msg.Payload);
                if (newData != null) { Data = newData; OnUiRefresh?.Invoke(); }
                break;
        }
    }

    public void SendChat(string text)
    {
        var msg = new NetMsg { Kind = NetMsgKind.Chat, From = SelfId, Payload = $"{SteamBridge.PersonaName}: {text}", SaveId = Data.SaveId };
        SteamBridge.Send(msg);
        Append(msg.Payload);
    }

    public void SendCommand(string cmd)
    {
        var msg = new NetMsg { Kind = NetMsgKind.Command, From = SelfId, Payload = cmd, SaveId = Data.SaveId };
        if (IsHost)
        {
            Append($"[ACTION] {SteamFriends.GetPersonaName()}: {cmd}");
            if (ShouldProcess(msg.Id)) _ = ResolveTurnAsync(msg);
        }
        else
        {
            Append($"[ACTION] {SteamFriends.GetPersonaName()}: {cmd}");
            SteamBridge.Send(msg);
        }
    }

    async Task ResolveTurnAsync(NetMsg trigger)
    {
        var partyLines = new List<string> { $"{NameOf(trigger.From)} -> {trigger.Payload}" };

        // AI companions (host synthesizes quick actions)
        foreach (var ai in Data.Players.Values.Where(p => p.IsAI))
        {
            var aiLine = await Gemini.GenerateAsync(
                system: "You are an AI companion in a co-op text adventure. Reply with ONE short first-person action that reacts to the scene and the other players this turn. No narration, no extra sentences—just your action.",
                storySoFar: Data.StorySoFar,
                turn: "Choose a helpful action this turn.",
                partyLines: partyLines);
            var compact = (aiLine.Split('\n')[0] ?? aiLine).Trim();
            if (compact.Length > 120) compact = compact[..120] + "...";
            partyLines.Add($"{ai.Name} -> {compact}");
        }

        var dm = await Gemini.GenerateAsync(
            system: "You are an AI GAME MASTER. STRICTLY resolve only the actions listed in [PARTY ACTIONS THIS TURN]; do not invent player actions. Acknowledge each action briefly with the actor’s name and immediate outcome, then narrate consequences that keep continuity with [CURRENT STORY]. Describe only world/NPC reactions you control. Keep 3–6 short sentences total. Use inventory tags [GAIN item] / [LOSE item] when appropriate. End with a line starting with NEXT: followed by 1–2 clear choices (no questions).",
            storySoFar: Data.StorySoFar,
            turn: "Narrate consequences and offer 1–2 obvious next choices.",
            partyLines: partyLines);

        Data.StorySoFar += "\n\n" + dm.Trim();
        foreach (var line in dm.Split('\n'))
        {
            string gain = GetBetween(line, "[GAIN ", "]");
            if (!string.IsNullOrWhiteSpace(gain)) GrantToAll(gain);
            string lose = GetBetween(line, "[LOSE ", "]");
            if (!string.IsNullOrWhiteSpace(lose)) RemoveFromAll(lose);
        }

        SyncState();
        Append("[GM] " + (dm.Split(new[] { "\n\n" }, StringSplitOptions.None)[0].Trim()));
        OnUiRefresh?.Invoke();
    }

    void SyncState()
    {
        var msg = new NetMsg { Kind = NetMsgKind.StateSync, From = SelfId, Payload = JsonSerializer.Serialize(Data), SaveId = Data.SaveId };
        SteamBridge.Send(msg);
    }

    string NameOf(ulong id) => Data.Players.TryGetValue(id, out var p) ? p.Name : id.ToString();

    static string GetBetween(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return ""; i += a.Length;
        int j = s.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
        if (j < 0) return ""; return s.Substring(i, j - i).Trim();
    }

    void GrantToAll(string item)
    {
        foreach (var p in Data.Players.Values)
            if (!p.Inventory.Any(x => x.Equals(item, StringComparison.OrdinalIgnoreCase)))
                p.Inventory.Add(item);
    }
    void RemoveFromAll(string item)
    {
        foreach (var p in Data.Players.Values)
            p.Inventory.RemoveAll(x => x.Equals(item, StringComparison.OrdinalIgnoreCase));
    }
}

// =========================
// UI
// =========================
class MainForm : Form
{
    TextBox log = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    TextBox input = new() { PlaceholderText = "Type a command or /chat … (Enter)", Dock = DockStyle.Fill };
    Button sendBtn = new() { Text = "Send", Dock = DockStyle.Right, Width = 100 };
    ListBox roster = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    ListBox inventory = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    Label lobbyLabel = new() { Text = "Not in lobby", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 4) };

    ToolStrip strip = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Stretch = true, Dock = DockStyle.Top, AutoSize = true, CanOverflow = true, Padding = new Padding(6,6,6,6) };
    ToolStripButton hostBtn = new() { Text = "Host Lobby" };
    ToolStripButton browseBtn = new() { Text = "Browse Lobbies" };
    ToolStripButton joinBtn = new() { Text = "Join by ID" };
    ToolStripTextBox joinIdBox = new() { ToolTipText = "Lobby ID (ulong)", Width = 220, AutoSize = false };
    ToolStripSeparator sep1 = new();

    ToolStripButton fillBotsBtn = new() { Text = "Fill with AI", CheckOnClick = true, Checked = true };
    ToolStripLabel botsLbl = new() { Text = "Bots:" };
    ToolStripComboBox botCountBox = new() { Width = 60 };
    ToolStripButton newWorldBtn = new() { Text = "New World" };
    ToolStripButton addBotsBtn = new() { Text = "Add Bots" };

    ToolStripSeparator sep2 = new();
    ToolStripComboBox slotBox = new() { Width = 100 };
    ToolStripButton saveBtn = new() { Text = "Save" };
    ToolStripButton loadBtn = new() { Text = "Load" };
    ToolStripTextBox apiKeyBox = new() { ToolTipText = "Gemini API Key (or set GEMINI_API_KEY env var)", Width = 220 };
    ToolStripButton setKeyBtn = new() { Text = "Set Key" };

    readonly GameCore game = new();

    public MainForm()
    {
        Text = "StorySteamAI — Text Co-op with AI DM (Steam)";
        Width = 1200; Height = 760;
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(900, 600);
        BackColor = SystemColors.ControlLightLight;

        // Build ToolStrip
        botCountBox.Items.AddRange(new object[] { "0","1","2","3" });
        botCountBox.SelectedIndex = 1;
        slotBox.Items.AddRange(GameConst.Slots);
        slotBox.SelectedIndex = 0;

        // Left cluster
        strip.Items.Add(hostBtn);
        strip.Items.Add(browseBtn);
        strip.Items.Add(joinBtn);
        strip.Items.Add(joinIdBox);
        strip.Items.Add(sep1);
        strip.Items.Add(fillBotsBtn);
        strip.Items.Add(botsLbl);
        strip.Items.Add(botCountBox);
        strip.Items.Add(newWorldBtn);
        strip.Items.Add(addBotsBtn);

        // Right cluster (set Alignment BEFORE adding or after—both okay)
        slotBox.Alignment = ToolStripItemAlignment.Right;
        saveBtn.Alignment = ToolStripItemAlignment.Right;
        loadBtn.Alignment = ToolStripItemAlignment.Right;
        sep2.Alignment = ToolStripItemAlignment.Right;
        apiKeyBox.Alignment = ToolStripItemAlignment.Right;
        setKeyBtn.Alignment = ToolStripItemAlignment.Right;

        strip.Items.Add(setKeyBtn);
        strip.Items.Add(apiKeyBox);
        strip.Items.Add(sep2);
        strip.Items.Add(loadBtn);
        strip.Items.Add(saveBtn);
        strip.Items.Add(slotBox);

        // Center layout
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(main);
        Controls.Add(strip);

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        left.Controls.Add(log);
        left.Controls.Add(lobbyLabel);
        log.BringToFront();
        main.Controls.Add(left, 0, 1);

        var mid = new GroupBox { Text = "Party", Dock = DockStyle.Fill, Padding = new Padding(6) };
        mid.Controls.Add(roster);
        main.Controls.Add(mid, 1, 1);

        var right = new GroupBox { Text = "Inventory (You)", Dock = DockStyle.Fill, Padding = new Padding(6) };
        right.Controls.Add(inventory);
        main.Controls.Add(right, 2, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        bottom.Controls.Add(input, 0, 0);
        bottom.Controls.Add(sendBtn, 1, 0);
        main.Controls.Add(bottom, 0, 2);
        main.SetColumnSpan(bottom, 3);

        // Events
        hostBtn.Click += (_, __) => { EnsureSteam(); SteamBridge.CreateLobby(); };
        browseBtn.Click += (_, __) => {
            EnsureSteam();
            browseBtn.Enabled = false;
            SteamBridge.BrowseLobbies(list => {
                browseBtn.Enabled = true;
                if (list.Count == 0) Info("No lobbies found.");
                foreach (var id in list)
                    Info($"Found lobby: {id.m_SteamID}  name='{SteamMatchmaking.GetLobbyData(id, "NAME")}'  members={SteamMatchmaking.GetNumLobbyMembers(id)}");
            });
        };
        joinBtn.Click += (_, __) => {
            EnsureSteam();
            if (ulong.TryParse(joinIdBox.Text.Trim(), out var id)) SteamBridge.JoinLobby(id);
            else Info("Invalid lobby ID.");
        };
        newWorldBtn.Click += (_, __) => {
            if (!InLobby()) { Info("Host or join a lobby first."); return; }
            if (!SteamBridge.IsHost) { Info("Only host can start a new world."); return; }
            int bots = 1; int.TryParse(Convert.ToString(botCountBox.SelectedItem) ?? "1", out bots);
            game.NewGame(slotBox.SelectedItem?.ToString() ?? GameConst.Slots[0],
                         asHost: true,
                         addAIBots: fillBotsBtn.Checked,
                         botCount: bots);
        };

        addBotsBtn.Click += (_, __) => { if (InLobby() && SteamBridge.IsHost) { int bots = 1; int.TryParse(Convert.ToString(botCountBox.SelectedItem) ?? "1", out bots); game.AddBots(bots); } else Info("Only host can add bots."); };

        saveBtn.Click += (_, __) => game.SaveGame();
        loadBtn.Click += (_, __) => game.LoadGame(slotBox.SelectedItem?.ToString() ?? GameConst.Slots[0]);

        setKeyBtn.Click += (_, __) => {
            var key = apiKeyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key)) { Gemini.ApiKey = ""; ApiKeyStore.Clear(); Info("Gemini key cleared."); }
            else { Gemini.ApiKey = key; ApiKeyStore.Save(key); apiKeyBox.Text = "[saved]"; Info("Gemini key set & saved."); }
        };

        sendBtn.Click += (_, __) => SendLine();
        input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendLine(); } };

        SteamBridge.OnLobbyJoined += () => {
            lobbyLabel.Text = $"Lobby: {SteamBridge.LobbyId.m_SteamID} {(SteamBridge.IsHost ? "(Host)" : "(Client)") }";
            if (SteamBridge.IsHost && string.IsNullOrEmpty(game.Data.SaveId))
            {
                int bots2 = 1; int.TryParse(Convert.ToString(botCountBox.SelectedItem) ?? "1", out bots2);
                game.NewGame(slotBox.SelectedItem?.ToString() ?? GameConst.Slots[0],
                             asHost: true,
                             addAIBots: fillBotsBtn.Checked,
                             botCount: bots2);
            }
            RefreshUi();
        };
        SteamBridge.OnLog += Info;
        SteamBridge.OnNet += (m) => this.Invoke(new Action(() => { game.HandleIncoming(m); if (m.Kind == NetMsgKind.Chat) log.AppendText(Environment.NewLine); RefreshUi(); }));
        game.OnAppend += s => this.Invoke(new Action(() => Append(s)));
        game.OnUiRefresh += () => this.Invoke(new Action(() => RefreshUi()));

        this.FormClosing += (_, __) => { if (!string.IsNullOrWhiteSpace(Gemini.ApiKey)) ApiKeyStore.Save(Gemini.ApiKey); };

        EnsureSteam();
        // Mask and load API key
        try { apiKeyBox.TextBox.UseSystemPasswordChar = true; } catch { }
        var saved = ApiKeyStore.Load();
        if (!string.IsNullOrWhiteSpace(saved)) { Gemini.ApiKey = saved; apiKeyBox.Text = "[saved]"; Info("Gemini key loaded."); }
        else {
            var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) { Gemini.ApiKey = env; apiKeyBox.Text = "[env var set]"; Info("Gemini key loaded from ENV."); }
        }
    }

    bool _steamTried = false;
    void EnsureSteam()
    {
        if (_steamTried) return; _steamTried = true;
        if (!SteamBridge.SafeInit())
            Info("Steam not initialized. Make sure Steam is running and steam_api64.dll + steam_appid.txt are beside the EXE.");
    }

    bool InLobby() => SteamBridge.LobbyId.IsValid();

    void SendLine()
    {
        var text = input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        input.Clear();
        if (text.StartsWith("/")) game.SendChat(text[1..]);
        else game.SendCommand(text);
    }

    void Append(string s) { if (log.TextLength > 0) log.AppendText(Environment.NewLine); log.AppendText(s); }
    void Info(string s) => Append("[INFO] " + s);

    void RefreshUi()
    {
        roster.Items.Clear();
        foreach (var p in game.Roster())
            roster.Items.Add($"{(p.IsAI ? "[AI]" : "[HUMAN]")} {p.Name} {(p.IsHost ? "(Host)" : "")}");

        inventory.Items.Clear();
        if (game.Data.Players.TryGetValue(SteamBridge.Self.m_SteamID, out var me))
            foreach (var i in me.Inventory) inventory.Items.Add(i);
    }
}
