using StandChillow.LanServer.Config;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Лимиты панели в масштабе «много людей снаружи» (#62). Проверяем не механику
/// отдельных чисел, а инварианты, из-за которых сайт отдавал 429 всем сразу:
/// одна загрузка страницы должна пролезать целиком, а один гость — не блокировать
/// остальных.
/// </summary>
public sealed class WebLimitsTests
{
    /// <summary>Сколько соединений открывает одна загрузка страницы.</summary>
    /// <remarks>
    /// <para>
    /// <c>index.html</c> грузит каталог и затем ПАРАЛЛЕЛЬНО профиль, комнаты и сводку
    /// (<c>Promise.all</c>) — это пять соединений сразу. Если поднять это число, надо
    /// поднять и <c>web.limits.maxConnectionsPerIp</c>, иначе первый же гость получит
    /// <c>429</c>.
    /// </para>
    /// <para>
    /// Значение зашито намеренно и проверяется первым тестом: это не настройка, а закон
    /// «кап не меньше того, что открывает браузер».
    /// </para>
    /// </remarks>
    private const int PageLoadConnections = 5;

    [Fact]
    public void DefaultConnectionCapFitsOnePageLoad()
    {
        var limits = new ServerConfig().Web.Limits;

        Assert.True(
            limits.MaxConnectionsPerIp >= PageLoadConnections,
            $"кап {limits.MaxConnectionsPerIp} меньше числа запросов одной загрузки страницы " +
            $"({PageLoadConnections}) — гость получит 429, открыв сайт");
    }

    [Fact]
    public void DefaultsSurviveNormalisation()
    {
        // Значения по умолчанию не должны «чиниться» нормализацией: конфиг без секции
        // limits обязан давать те же цифры, что и код.
        var config = new ServerConfig();
        config.Normalize();

        Assert.Equal(8, config.Web.Limits.MaxConnectionsPerIp);
        Assert.Equal(256, config.Web.Limits.MaxConnectionsTotal);
        Assert.Equal(30, config.Web.Limits.RequestsPerSecond);
    }

    [Fact]
    public void PanelOptionsDoNotUnderCutThePageLoad()
    {
        // Вторая копия дефолтов живёт в WebPanelOptions; разъехавшись, она снова
        // включила бы массовые 429 при пустом config.json.
        var limits = new WebPanelOptions.WebLimits();

        Assert.True(limits.MaxConnectionsPerIp >= PageLoadConnections);
        Assert.Equal(new ServerConfig().Web.Limits.MaxConnectionsPerIp, limits.MaxConnectionsPerIp);
        Assert.Equal(new ServerConfig().Web.Limits.RequestsPerSecond, limits.RequestsPerSecond);
    }

    [Fact]
    public void TemplateConfigDocumentsBothCaps()
    {
        var template = ServerConfig.Template;

        Assert.Contains("maxConnectionsPerIp", template);
        Assert.Contains("maxConnectionsTotal", template);
    }
}
