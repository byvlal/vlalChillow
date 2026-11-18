using StandChillow.LanServer.Reports;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// <c>reports.json</c>: сохранение, лимиты, окно счётчика, device-склейка и защита
/// от файла будущей версии.
/// </summary>
public sealed class ReportStoreTests : IDisposable
{
    private const string DeviceA = "LB143C7D41FAE30EB14F49B1678E3933C9";
    private const string DeviceB = "LBAAAAC7D41FAE30EB14F49B1678E3933C9";

    private readonly string _dir;
    private readonly string _path;

    public ReportStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "reports.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private DateTime Now => new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private ReportSubmitResult Add(
        ReportStore store,
        int targetId,
        DateTime now,
        string category = ReportCategories.Cheat,
        string reason = "тест",
        int reporterId = 5001,
        IEnumerable<string>? devices = null)
    {
        return store.Add(
            targetId,
            "target" + targetId,
            targetAdmin: false,
            devices,
            reporterId,
            "reporter",
            category,
            reason,
            now);
    }

    [Fact]
    public void AddPersistsAndLoads()
    {
        var store = new ReportStore(_path);
        var result = Add(store, 2001, Now, ReportCategories.Insult, "обижает", devices: new[] { DeviceA });

        Assert.Equal(ReportSubmitError.None, result.Error);
        Assert.NotNull(result.Record);
        Assert.True(File.Exists(_path));

        var loaded = new ReportStore(_path);
        Assert.Equal(1, loaded.Count);
        var record = Assert.Single(loaded.Export());
        Assert.Equal(2001, record.TargetId);
        Assert.Equal("target2001", record.TargetNick);
        Assert.Equal(5001, record.ReporterId);
        Assert.Equal(ReportCategories.Insult, record.Category);
        Assert.Equal("обижает", record.Reason);
        Assert.Equal(ReportStatuses.Open, record.Status);
        Assert.Contains(DeviceA, record.TargetDevices);
    }

    [Fact]
    public void RejectedReportsAreNotCounted()
    {
        var store = new ReportStore(_path);
        var first = Add(store, 2001, Now);
        /* Второй автор: дедуп считает пару «автор + цель», а здесь проверяется статус. */
        var second = Add(store, 2001, Now.AddMinutes(1), reporterId: 5002);
        Assert.NotNull(first.Record);
        Assert.NotNull(second.Record);

        var openCounts = store.CountsFor(2001, null, Now);
        Assert.Equal(2, openCounts.Total);
        Assert.Equal(2, openCounts.Open);

        var (error, record) = store.SetStatus(
            second.Record!.Id, ReportStatuses.Rejected, "админ", "ложная", Now.AddMinutes(2));
        Assert.Null(error);
        Assert.NotNull(record);

        var counts = store.CountsFor(2001, null, Now.AddMinutes(3));
        Assert.Equal(1, counts.Total);
        Assert.Equal(0, counts.Accepted);
        Assert.Equal(1, counts.Open);

        var (acceptError, _) = store.SetStatus(
            first.Record!.Id, ReportStatuses.Accepted, "админ", null, Now.AddMinutes(4));
        Assert.Null(acceptError);
        var after = store.CountsFor(2001, null, Now.AddMinutes(5));
        Assert.Equal(1, after.Total);
        Assert.Equal(1, after.Accepted);
    }

    [Fact]
    public void BadgeWindowDropsOldReports()
    {
        var store = new ReportStore(_path);
        Add(store, 2001, Now);
        Add(store, 2002, Now.AddDays(-31));

        Assert.Equal(1, store.CountsFor(2001, null, Now).Total);
        Assert.Equal(0, store.CountsFor(2002, null, Now).Total);
    }

    [Fact]
    public void DeviceMatchesAfterIdChanges()
    {
        var store = new ReportStore(_path);
        Add(store, 2001, Now, devices: new[] { DeviceA });

        Assert.Equal(1, store.CountsFor(2001, null, Now).Total);
        Assert.Equal(1, store.CountsFor(9001, new[] { DeviceA }, Now).Total);
        Assert.Equal(0, store.CountsFor(9001, new[] { DeviceB }, Now).Total);
    }

    [Fact]
    public void LearnDevicesAddsToOldReports()
    {
        var store = new ReportStore(_path);
        Add(store, 2001, Now);

        var changed = store.LearnDevices(2001, new[] { DeviceA }, Now);
        Assert.Equal(1, changed);

        var record = Assert.Single(new ReportStore(_path).Export());
        Assert.Contains(DeviceA, record.TargetDevices);
        Assert.Equal(1, store.CountsFor(7777, new[] { DeviceA }, Now).Total);
    }

    [Fact]
    public void DuplicateWithinDayIsRejected()
    {
        var store = new ReportStore(_path);
        Assert.Equal(ReportSubmitError.None, Add(store, 2001, Now).Error);

        var again = Add(store, 2001, Now.AddHours(1));
        Assert.Equal(ReportSubmitError.Duplicate, again.Error);
        Assert.True(again.RetryAfterSec > 0);
        Assert.Equal(1, store.Count);

        var nextDay = Add(store, 2001, Now.AddHours(25));
        Assert.Equal(ReportSubmitError.None, nextDay.Error);
    }

    [Fact]
    public void CooldownBetweenDifferentTargets()
    {
        var store = new ReportStore(_path);
        Assert.Equal(ReportSubmitError.None, Add(store, 2001, Now).Error);

        var fast = Add(store, 2002, Now.AddSeconds(5));
        Assert.Equal(ReportSubmitError.Cooldown, fast.Error);
        Assert.True(fast.RetryAfterSec > 0);

        var later = Add(store, 2002, Now.AddSeconds(21));
        Assert.Equal(ReportSubmitError.None, later.Error);
    }

    [Fact]
    public void DailyLimitStopsSixthReport()
    {
        var store = new ReportStore(_path);
        for (var i = 0; i < ReportStore.DailyLimit; i++)
        {
            var result = Add(store, 2001 + i, Now.AddSeconds(i * 30));
            Assert.Equal(ReportSubmitError.None, result.Error);
        }

        var sixth = Add(store, 2009, Now.AddSeconds(200));
        Assert.Equal(ReportSubmitError.DailyLimit, sixth.Error);
        Assert.Equal(ReportStore.DailyLimit, store.Count);
    }

    [Fact]
    public void ReasonIsTruncatedToLimit()
    {
        var store = new ReportStore(_path);
        var result = Add(store, 2001, Now, reason: new string('я', ReportStore.MaxReasonLength + 100));
        Assert.NotNull(result.Record);

        var loaded = Assert.Single(new ReportStore(_path).Export());
        Assert.Equal(ReportStore.MaxReasonLength, loaded.Reason.Length);
    }

    [Fact]
    public void CorruptFileStartsEmptyAndKeepsCopy()
    {
        File.WriteAllText(_path, "{это не json");

        var store = new ReportStore(_path);

        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(_path + ".corrupt.bak"));
        Assert.Equal("{это не json", File.ReadAllText(_path + ".corrupt.bak"));
    }

    [Fact]
    public void FutureVersionIsReadOnlyAndNeverOverwritten()
    {
        var json = """{"version":99,"reports":[]}""";
        File.WriteAllText(_path, json);

        var store = new ReportStore(_path);
        Assert.True(store.IsReadOnly);
        Assert.Equal(99, store.LastLoadedVersion);

        var added = Add(store, 2001, Now);
        Assert.Equal(ReportSubmitError.Storage, added.Error);

        var (error, _) = store.SetStatus("нет", ReportStatuses.Accepted, "админ", null, Now);
        Assert.NotNull(error);
        Assert.Equal(json, File.ReadAllText(_path));
    }

    [Fact]
    public void ListPagesNewestFirst()
    {
        var store = new ReportStore(_path);
        for (var i = 0; i < 3; i++)
            Add(store, 2001 + i, Now.AddSeconds(i * 30), reporterId: 5001 + i);

        var page = store.List(null, 1, 2);
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Pages);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].CreatedUtcMs > page.Items[1].CreatedUtcMs);

        var second = store.List(null, 2, 2);
        Assert.Single(second.Items);
    }
}
