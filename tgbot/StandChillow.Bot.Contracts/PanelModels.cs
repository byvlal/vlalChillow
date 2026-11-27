using System.Text.Json.Serialization;

namespace StandChillow.Bot.Contracts;

// ── рукопожатие и статус ────────────────────────────────────────────────────

/// <summary>Ответ <c>GET /api/bot</c>: бот проверяет адрес и версию API.</summary>
public sealed class Handshake
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public string Service { get; set; } = "";
    [JsonRequired] public int Api { get; set; }
    [JsonRequired] public DateTimeOffset ServerTime { get; set; }
}

/// <summary>Сводка сервера (<c>GET /api/status</c>).</summary>
public sealed class StatusSnapshot
{
    [JsonRequired] public int OnlineLobby { get; set; }
    [JsonRequired] public int OnlineMatch { get; set; }
    [JsonRequired] public int OnlineTotal { get; set; }
    [JsonRequired] public int RoomsCount { get; set; }
    [JsonRequired] public int RoomsMax { get; set; }
    [JsonRequired] public int NextNumber { get; set; }
    [JsonRequired] public bool GameListening { get; set; }
    [JsonRequired] public bool GamePortFailed { get; set; }
    [JsonRequired] public bool MatchRunning { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string Levels { get; set; } = "";
    [JsonRequired] public string PublicAddress { get; set; } = "";
    [JsonRequired] public int PublicLobbyPort { get; set; }
    [JsonRequired] public int PublicMatchPort { get; set; }
    [JsonRequired] public int LocalLobbyPort { get; set; }
    [JsonRequired] public int LocalMatchPort { get; set; }
    [JsonRequired] public DateTimeOffset ServerUtc { get; set; }
}

// ── каталог режимов ─────────────────────────────────────────────────────────

public sealed class ModesResponse
{
    [JsonRequired] public List<GameMode> Modes { get; set; } = new();
}

public sealed class GameMode
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Alias { get; set; } = "";
    [JsonRequired] public string DefaultLevel { get; set; } = "";
    [JsonRequired] public bool Implemented { get; set; }
    [JsonRequired] public int PerTeam { get; set; }
    [JsonRequired] public int MaxPlayers { get; set; }
    [JsonRequired] public string TeamLabel { get; set; } = "";
    [JsonRequired] public List<int> TeamSizes { get; set; } = new();
    [JsonRequired] public List<string> Levels { get; set; } = new();
    [JsonRequired] public List<string> Aliases { get; set; } = new();
}

// ── комнаты ─────────────────────────────────────────────────────────────────

public sealed class RoomsResponse
{
    [JsonRequired] public int NextNumber { get; set; }
    [JsonRequired] public int Count { get; set; }
    [JsonRequired] public int MaxRooms { get; set; }
    [JsonRequired] public List<RoomSummary> Rooms { get; set; } = new();
}

public sealed class RoomSummary
{
    [JsonRequired] public int Number { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string ModeName { get; set; } = "";
    [JsonRequired] public string Level { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Owner { get; set; } = "";
    [JsonRequired] public int OwnerId { get; set; }
    [JsonRequired] public string State { get; set; } = "";
    [JsonRequired] public int Players { get; set; }
    [JsonRequired] public int ScoreCt { get; set; }
    [JsonRequired] public int ScoreTr { get; set; }
    [JsonRequired] public int PerTeam { get; set; }
    [JsonRequired] public int MaxPlayers { get; set; }
    [JsonRequired] public string TeamLabel { get; set; } = "";
    [JsonRequired] public string JoinCommand { get; set; } = "";
    [JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
    [JsonRequired] public bool IsMine { get; set; }
}

public sealed class RoomResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public RoomCard Room { get; set; } = new();
}

public sealed class RoomCard
{
    [JsonRequired] public int Number { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string ModeName { get; set; } = "";
    [JsonRequired] public string Map { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string State { get; set; } = "";
    [JsonRequired] public int PlayerCount { get; set; }
    [JsonRequired] public int ScoreCt { get; set; }
    [JsonRequired] public int ScoreTr { get; set; }
    [JsonRequired] public int Round { get; set; }
    [JsonRequired] public int PerTeam { get; set; }
    [JsonRequired] public int MaxPlayers { get; set; }
    [JsonRequired] public string TeamLabel { get; set; } = "";
    [JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
    [JsonRequired] public RoomOwner Owner { get; set; } = new();
    public int? MatchId { get; set; }
    [JsonRequired] public List<RoomPlayer> Players { get; set; } = new();
}

public sealed class RoomOwner
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public bool Admin { get; set; }
}

public sealed class RoomPlayer
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public bool Admin { get; set; }
    [JsonRequired] public string Team { get; set; } = "";
    [JsonRequired] public int Kills { get; set; }
    [JsonRequired] public int Assists { get; set; }
    [JsonRequired] public int Deaths { get; set; }
    [JsonRequired] public int Score { get; set; }
    [JsonRequired] public int Ping { get; set; }
    [JsonRequired] public bool Left { get; set; }
}

public sealed class CreateRoomRequest
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("perTeam")] public int PerTeam { get; set; }
}

public sealed class CreatedRoom
{
    [JsonRequired] public int Number { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string ModeName { get; set; } = "";
    [JsonRequired] public string Level { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Owner { get; set; } = "";
    [JsonRequired] public int OwnerId { get; set; }
    [JsonRequired] public string JoinCommand { get; set; } = "";
    [JsonRequired] public string State { get; set; } = "";
    [JsonRequired] public int PerTeam { get; set; }
    [JsonRequired] public int MaxPlayers { get; set; }
    [JsonRequired] public string TeamLabel { get; set; } = "";
}

public sealed class EndRoomResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public int Number { get; set; }
}

// ── игрок и статистика ──────────────────────────────────────────────────────

public sealed class Profile
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string? CustomId { get; set; }
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public DateTimeOffset Since { get; set; }
    [JsonRequired] public bool Admin { get; set; }
}

public sealed class MyStats
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string? CustomId { get; set; }
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public int Kills { get; set; }
    [JsonRequired] public int Deaths { get; set; }
    [JsonRequired] public double Kd { get; set; }
    [JsonRequired] public int Seconds { get; set; }
    [JsonRequired] public int Matches { get; set; }
}

public sealed class PlayerStats
{
    [JsonRequired] public int Kills { get; set; }
    [JsonRequired] public int Deaths { get; set; }
    [JsonRequired] public double Kd { get; set; }
    [JsonRequired] public int Seconds { get; set; }
    [JsonRequired] public int Matches { get; set; }
}

public sealed class ReportCounts
{
    [JsonRequired] public int Total { get; set; }
    [JsonRequired] public int Open { get; set; }
    [JsonRequired] public int Accepted { get; set; }
    [JsonRequired] public int WindowDays { get; set; }
}

public sealed class PublicPlayer
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string? CustomId { get; set; }
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public bool Admin { get; set; }
    [JsonRequired] public string Status { get; set; } = "";
    [JsonRequired] public bool HasAvatar { get; set; }
    [JsonRequired] public bool Banned { get; set; }
    [JsonRequired] public DateTimeOffset? LastSeen { get; set; }
    public string? Note { get; set; }
    [JsonRequired] public PlayerStats Stats { get; set; } = new();
    [JsonRequired] public List<MatchSummary> Matches { get; set; } = new();
    [JsonRequired] public ReportCounts Reports { get; set; } = new();
}

// ── матчи ───────────────────────────────────────────────────────────────────

public sealed class MatchesResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public int Player { get; set; }
    [JsonRequired] public List<MatchSummary> Matches { get; set; } = new();
}

public sealed class MatchSummary
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public DateTimeOffset Started { get; set; }
    [JsonRequired] public DateTimeOffset Ended { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string Map { get; set; } = "";
    [JsonRequired] public int ScoreCt { get; set; }
    [JsonRequired] public int ScoreTr { get; set; }
    [JsonRequired] public string Winner { get; set; } = "";
    [JsonRequired] public int Rounds { get; set; }
    [JsonRequired] public int Room { get; set; }
    [JsonRequired] public string RoomName { get; set; } = "";
    public MatchYou? You { get; set; }
}

public sealed class MatchYou
{
    [JsonRequired] public string Team { get; set; } = "";
    [JsonRequired] public int Kills { get; set; }
    [JsonRequired] public int Assists { get; set; }
    [JsonRequired] public int Deaths { get; set; }
    [JsonRequired] public int Score { get; set; }
    [JsonRequired] public int Money { get; set; }
    [JsonRequired] public int Ping { get; set; }
    [JsonRequired] public bool Left { get; set; }
}

public sealed class MatchResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public MatchFull Match { get; set; } = new();
}

public sealed class MatchFull
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public DateTimeOffset Started { get; set; }
    [JsonRequired] public DateTimeOffset Ended { get; set; }
    [JsonRequired] public string Mode { get; set; } = "";
    [JsonRequired] public string Map { get; set; } = "";
    [JsonRequired] public int ScoreCt { get; set; }
    [JsonRequired] public int ScoreTr { get; set; }
    [JsonRequired] public string Winner { get; set; } = "";
    [JsonRequired] public int Rounds { get; set; }
    [JsonRequired] public int Room { get; set; }
    [JsonRequired] public string RoomName { get; set; } = "";
    [JsonRequired] public List<MatchPlayer> Players { get; set; } = new();
}

public sealed class MatchPlayer
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public bool Admin { get; set; }
    [JsonRequired] public string Team { get; set; } = "";
    [JsonRequired] public int Kills { get; set; }
    [JsonRequired] public int Assists { get; set; }
    [JsonRequired] public int Deaths { get; set; }
    [JsonRequired] public int Score { get; set; }
    [JsonRequired] public int Money { get; set; }
    [JsonRequired] public int Ping { get; set; }
    [JsonRequired] public bool Left { get; set; }
}

// ── жалобы и администрация ──────────────────────────────────────────────────

public sealed class ReportRequest
{
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class ReportCreated
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public ReportItem Report { get; set; } = new();
    [JsonRequired] public ReportCounts Reports { get; set; } = new();
}

public sealed class MyReportsResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public int Page { get; set; }
    [JsonRequired] public int PageSize { get; set; }
    [JsonRequired] public int Total { get; set; }
    [JsonRequired] public int Pages { get; set; }
    [JsonRequired] public List<ReportItem> Items { get; set; } = new();
}

public sealed class ReportItem
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public int TargetId { get; set; }
    [JsonRequired] public string TargetNick { get; set; } = "";
    [JsonRequired] public bool TargetAdmin { get; set; }
    [JsonRequired] public int ReporterId { get; set; }
    [JsonRequired] public string ReporterNick { get; set; } = "";
    [JsonRequired] public string Category { get; set; } = "";
    [JsonRequired] public string CategoryLabel { get; set; } = "";
    [JsonRequired] public string Reason { get; set; } = "";
    [JsonRequired] public string Status { get; set; } = "";
    [JsonRequired] public string StatusLabel { get; set; } = "";
    [JsonRequired] public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Reviewed { get; set; }
}

public sealed class AdministrationResponse
{
    [JsonRequired] public bool Ok { get; set; }
    [JsonRequired] public List<AdminEntry> Admins { get; set; } = new();
}

public sealed class AdminEntry
{
    [JsonRequired] public int Id { get; set; }
    [JsonRequired] public string Nick { get; set; } = "";
    [JsonRequired] public string? CustomId { get; set; }
    [JsonRequired] public string DisplayId { get; set; } = "";
    [JsonRequired] public bool Admin { get; set; }
    [JsonRequired] public string Status { get; set; } = "";
    [JsonRequired] public bool HasAvatar { get; set; }
    [JsonRequired] public DateTimeOffset? LastSeen { get; set; }
    [JsonRequired] public ReportCounts Reports { get; set; } = new();
}

// ── служебное ───────────────────────────────────────────────────────────────

public sealed class OkResponse
{
    [JsonRequired] public bool Ok { get; set; }
}

public sealed class ErrorBody
{
    [JsonRequired] public string Code { get; set; } = "";
    [JsonRequired] public string Message { get; set; } = "";
}
