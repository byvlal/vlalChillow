namespace StandChillow.LanServer.Reports;

/// <summary>
/// Категории жалоб. Строки, а не enum: они уходят в JSON и в файл, и переименование
/// поля не должно ломать уже сохранённые репорты.
/// </summary>
public static class ReportCategories
{
    public const string Cheat = "cheat";
    public const string TeamKill = "teamkill";
    public const string Insult = "insult";
    public const string Spam = "spam";
    public const string Other = "other";

    public static bool IsValid(string? value) => value is
        Cheat or TeamKill or Insult or Spam or Other;

    public static string Label(string? value) => value switch
    {
        Cheat => "читы",
        TeamKill => "тимкилл",
        Insult => "оскорбления",
        Spam => "спам",
        Other => "другое",
        _ => "другое",
    };
}

/// <summary>Статусы жалобы: открытая, подтверждённая, отклонённая.</summary>
public static class ReportStatuses
{
    public const string Open = "open";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public static bool IsValid(string? value) => value is Open or Accepted or Rejected;
}

/// <summary>
/// Одна жалоба в <c>reports.json</c>. Ник и устройства цели — снимок на момент
/// отправки: человек может сменить ник, а жалоба должна остаться узнаваемой.
/// </summary>
public sealed class ReportRecord
{
    /// <summary>Короткий идентификатор записи (для кнопок модерации).</summary>
    public string Id { get; set; } = "";

    /// <summary>Числовой серверный ID цели — главный ключ, как у банов.</summary>
    public int TargetId { get; set; }

    /// <summary>Ник цели на момент жалобы.</summary>
    public string TargetNick { get; set; } = "";

    /// <summary>Была ли цель админом на момент жалобы (для бейджа в списке).</summary>
    public bool TargetAdmin { get; set; }

    /// <summary>Ключи устройств цели, если сервер их уже знал.</summary>
    public List<string> TargetDevices { get; set; } = new();

    /// <summary>Кто пожаловался — числовой ID и ник на момент жалобы.</summary>
    public int ReporterId { get; set; }
    public string ReporterNick { get; set; } = "";

    /// <summary>Категория из <see cref="ReportCategories"/>.</summary>
    public string Category { get; set; } = ReportCategories.Other;

    /// <summary>Свободный текст, до <see cref="ReportStore.MaxReasonLength"/> символов.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Когда отправлена (unix ms, UTC).</summary>
    public long CreatedUtcMs { get; set; }

    /// <summary>Статус из <see cref="ReportStatuses"/>.</summary>
    public string Status { get; set; } = ReportStatuses.Open;

    /// <summary>Кто разобрал (ник админа), если уже разобрана.</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>Когда разобрана (unix ms, UTC).</summary>
    public long? ReviewedUtcMs { get; set; }

    /// <summary>Короткая заметка админа (необязательно).</summary>
    public string? AdminNote { get; set; }
}

/// <summary>
/// Игрок, на которого можно пожаловаться, и он же — публичный профиль.
/// <see cref="Devices"/> наружу не отдаётся: это внутренний ключ для склейки жалоб.
/// </summary>
public sealed record ReportTarget(
    int Id,
    string Nick,
    string? CustomId,
    string DisplayId,
    bool IsAdmin,
    string Status,
    bool HasAvatar,
    bool Banned,
    DateTime? LastSeenUtc,
    IReadOnlyList<string> Devices,
    string? Note);

/// <summary>Сколько жалоб у игрока: всего (без отклонённых), открытых и подтверждённых.</summary>
public sealed record ReportCounts(int Total, int Open, int Accepted);

/// <summary>Счётчики для шапки админской вкладки «Репорты».</summary>
public sealed record ReportSummary(int Open, int Accepted, int Rejected);

/// <summary>Страница списка жалоб — для «моих» и для админской модерации.</summary>
public sealed record ReportPage(
    IReadOnlyList<ReportRecord> Items,
    int Total,
    int Page,
    int PageSize)
{
    public int Pages => PageSize <= 0 ? 0 : (Total + PageSize - 1) / PageSize;
}

/// <summary>Почему сервер не принял жалобу.</summary>
public enum ReportSubmitError
{
    None,
    Self,
    Duplicate,
    DailyLimit,
    Cooldown,
    Storage,
}

/// <summary>Результат отправки: запись или причина отказа и пауза для 429.</summary>
public sealed record ReportSubmitResult(
    ReportSubmitError Error,
    ReportRecord? Record,
    int RetryAfterSec);
