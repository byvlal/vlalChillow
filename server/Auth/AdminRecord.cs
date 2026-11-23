namespace StandChillow.LanServer.Auth;

/// <summary>
/// Запись админа в <c>auth/admins.json</c>. Права и пароль живут только здесь;
/// <c>config.json</c> знает лишь <c>admin.ids</c> — кто вообще может быть админом.
/// </summary>
public sealed class AdminRecord
{
    /// <summary>Числовой id аккаунта — тот же, что в лобби и банах.</summary>
    public int Id { get; set; }

    /// <summary>Ник на момент последнего входа (для списка и журнала).</summary>
    public string Nick { get; set; } = "";

    /// <summary>
    /// <c>admin</c> или <c>mega</c>. Роль информационная: настоящие мега-права даёт
    /// только совпадение IP с <see cref="MegaIps"/>.
    /// </summary>
    public string Role { get; set; } = "admin";

    /// <summary>Выданные права. <c>dashboard</c> и <c>journal</c> есть всегда.</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>Кто добавил: <c>config</c>, <c>console</c> или id мега-админа.</summary>
    public string AddedBy { get; set; } = "config";

    /// <summary>
    /// Версия пароля. Растёт при регенерации; старые веб-сессии и игровые
    /// авторизации с другой версией считаются мёртвыми.
    /// </summary>
    public int AuthVersion { get; set; } = 1;

    /// <summary>IP, с которых этот админ получает мега-права после обычного входа.</summary>
    public List<string> MegaIps { get; set; } = new();

    /// <summary>SHA-256 полного пароля <c>&lt;id&gt;-&lt;hex64&gt;</c>.</summary>
    public string PasswordHash { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Копия для чтения: вызывающий не должен менять состояние хранилища.</summary>
    public AdminRecord Clone() => new()
    {
        Id = Id,
        Nick = Nick,
        Role = Role,
        Permissions = new List<string>(Permissions),
        AddedBy = AddedBy,
        AuthVersion = AuthVersion,
        MegaIps = new List<string>(MegaIps),
        PasswordHash = PasswordHash,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };
}
