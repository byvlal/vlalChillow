using System.Globalization;
using System.Text;

namespace StandChillow.Bot;

/// <summary>Короткие человеческие форматы для сообщений бота.</summary>
public static class Fmt
{
    /// <summary>Экранировать текст для Telegram HTML.</summary>
    public static string E(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    public static string Short(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length <= max)
            return text;
        return text[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    public static string Kd(double kd) =>
        kd.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Duration(int seconds)
    {
        if (seconds <= 0)
            return "0 мин";

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes == 0 ? $"{hours} ч" : $"{hours} ч {minutes} мин";
        }

        return $"{Math.Max(1, span.Minutes)} мин";
    }

    public static string Date(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public static string DateShort(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz).ToString("dd.MM", CultureInfo.InvariantCulture);

    public static string State(string? state) => state switch
    {
        "waiting" => "ждём игроков",
        "live" => "идёт катка",
        "finished" => "завершена",
        "starting" => "запускается",
        null or "" => "неизвестно",
        _ => state,
    };

    public static string PlayerStatus(string? status) => status switch
    {
        "online" => "в сети",
        "lobby" => "в лобби",
        "match" => "в бою",
        "offline" => "не в сети",
        null or "" => "неизвестно",
        _ => status,
    };

    public static string Winner(string? winner) => winner switch
    {
        "CT" => "CT",
        "T" => "T",
        "draw" or "DRAW" => "ничья",
        null or "" => "—",
        _ => winner,
    };

    public static string Team(string? team) => team switch
    {
        "CT" => "CT",
        "T" => "T",
        "SPEC" => "зрители",
        "NONE" => "без команды",
        null or "" => "—",
        _ => team,
    };

    /// <summary>Склеить строки, выбросив пустые.</summary>
    public static string JoinLines(params string?[] lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);
        }

        return builder.ToString();
    }
}
